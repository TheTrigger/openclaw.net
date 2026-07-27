using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Providers.ClaudeCode;

internal sealed record ClaudeCodeInvocation(
    string Prompt,
    string JsonSchema,
    string Model,
    string? Effort);

internal sealed record ClaudeCodeProcessResult(
    string StructuredOutput,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens);

internal interface IClaudeCodeProcessRunner
{
    Task<ClaudeCodeProcessResult> RunAsync(ClaudeCodeInvocation invocation, CancellationToken ct);
}

internal sealed class ClaudeCodeProcessRunner(
    ClaudeCodeProviderConfig config,
    ILogger logger) : IClaudeCodeProcessRunner
{
    public async Task<ClaudeCodeProcessResult> RunAsync(ClaudeCodeInvocation invocation, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(invocation)
        };

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Starting Claude Code CLI provider process model={Model} effort={Effort} timeout={TimeoutSeconds}s",
                invocation.Model,
                invocation.Effort ?? "default",
                config.TimeoutSeconds);
        }

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Claude Code CLI process could not be started.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.StandardInput.WriteAsync(invocation.Prompt.AsMemory(), linked.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(linked.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw CreateProcessFailure(process.ExitCode, stdout, stderr);

            return ParseResult(stdout);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Claude Code CLI did not finish within {config.TimeoutSeconds} seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(ClaudeCodeInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = config.Executable,
            WorkingDirectory = config.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--json-schema");
        startInfo.ArgumentList.Add(invocation.JsonSchema);
        startInfo.ArgumentList.Add("--tools");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add("--disable-slash-commands");
        startInfo.ArgumentList.Add("--no-session-persistence");
        startInfo.ArgumentList.Add("--safe-mode");
        startInfo.ArgumentList.Add("--system-prompt");
        startInfo.ArgumentList.Add(config.SystemPrompt ?? ClaudeCodePromptProtocol.SystemPrompt);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(invocation.Model);

        if (!string.IsNullOrWhiteSpace(invocation.Effort))
        {
            startInfo.ArgumentList.Add("--effort");
            startInfo.ArgumentList.Add(invocation.Effort);
        }

        RemoveSensitiveEnvironment(startInfo.Environment);
        return startInfo;
    }

    private void RemoveSensitiveEnvironment(IDictionary<string, string?> environment)
    {
        var names = environment.Keys.ToArray();
        foreach (var name in names)
        {
            if (!IsSensitiveName(name))
                continue;

            if (config.AllowApiKeyAuthentication &&
                string.Equals(name, "ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            environment.Remove(name);
        }
    }

    private static bool IsSensitiveName(string name)
        => name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
            || name.Contains("API_KEY", StringComparison.OrdinalIgnoreCase);

    private static ClaudeCodeProcessResult ParseResult(string stdout)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var isError = root.TryGetProperty("is_error", out var error) &&
                error.ValueKind == JsonValueKind.True;
            var resultText = root.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.String
                    ? result.GetString()
                    : null;

            if (isError)
                throw new InvalidOperationException($"Claude Code CLI returned an error: {Truncate(resultText)}");

            string structuredOutput;
            if (root.TryGetProperty("structured_output", out var structured) &&
                structured.ValueKind == JsonValueKind.Object)
            {
                structuredOutput = structured.GetRawText();
            }
            else if (!string.IsNullOrWhiteSpace(resultText))
            {
                structuredOutput = resultText;
            }
            else
            {
                throw new InvalidOperationException("Claude Code CLI did not return structured_output or result.");
            }

            var inputTokens = 0L;
            var outputTokens = 0L;
            var cacheReadTokens = 0L;
            var cacheWriteTokens = 0L;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = ReadLong(usage, "input_tokens");
                outputTokens = ReadLong(usage, "output_tokens");
                cacheReadTokens = ReadLong(usage, "cache_read_input_tokens");
                cacheWriteTokens = ReadLong(usage, "cache_creation_input_tokens");
            }

            return new ClaudeCodeProcessResult(
                structuredOutput,
                inputTokens,
                outputTokens,
                cacheReadTokens,
                cacheWriteTokens);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Claude Code CLI returned invalid JSON: {Truncate(stdout)}",
                ex);
        }
    }

    private static InvalidOperationException CreateProcessFailure(int exitCode, string stdout, string stderr)
    {
        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return new InvalidOperationException(
            $"Claude Code CLI exited with code {exitCode}: {Truncate(detail)}");
    }

    private static long ReadLong(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number)
                ? number
                : 0;

    private static string Truncate(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "(no details)" : value.Trim();
        return normalized.Length <= 2_000 ? normalized : normalized[..2_000] + "…";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
    }
}
