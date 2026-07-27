using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Providers.ClaudeCode;

internal sealed class ClaudeCodeChatClient(
    ClaudeCodeProviderConfig config,
    ILogger logger,
    IClaudeCodeProcessRunner? runner = null) : IChatClient
{
    private static readonly HashSet<string> SupportedEfforts =
        new(["low", "medium", "high", "xhigh", "max"], StringComparer.OrdinalIgnoreCase);

    private readonly ClaudeCodeProviderConfig _config = config;
    private readonly IClaudeCodeProcessRunner _runner = runner ?? new ClaudeCodeProcessRunner(config, logger);

    public ChatClientMetadata Metadata =>
        new(nameof(ClaudeCodeChatClient), null, _config.ProviderId);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = ClaudeCodePromptProtocol.Build(messages, options);
        var toolNames = GetToolNames(options);
        var model = string.IsNullOrWhiteSpace(options?.ModelId)
            ? _config.Models[0]
            : options.ModelId.Trim();
        var invocation = new ClaudeCodeInvocation(
            request.Prompt,
            request.JsonSchema,
            model,
            ResolveEffort(options));
        var result = await _runner.RunAsync(invocation, cancellationToken);
        var protocol = ClaudeCodePromptProtocol.ParseResponse(result.StructuredOutput, toolNames);

        List<AIContent> contents;
        if (protocol.Kind == "tool_call")
        {
            contents =
            [
                new FunctionCallContent(
                    $"claude_code_{Guid.NewGuid():N}",
                    protocol.Tool,
                    ToArguments(protocol.Arguments))
            ];
        }
        else
        {
            contents = [new TextContent(protocol.Text)];
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            Usage = CreateUsage(result)
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            if (message.Contents.Count > 0)
                yield return new ChatResponseUpdate(ChatRole.Assistant, message.Contents);
        }

        if (response.Usage is not null)
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(response.Usage)]);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(IChatClient) || serviceType.IsInstanceOfType(this)
            ? this
            : serviceType == typeof(ChatClientMetadata)
                ? Metadata
                : null;

    public void Dispose()
    {
    }

    private static HashSet<string> GetToolNames(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } ||
            options.ToolMode?.Equals(ChatToolMode.None) == true)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return options.Tools
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? ResolveEffort(ChatOptions? options)
    {
        if (options?.AdditionalProperties is null ||
            !options.AdditionalProperties.TryGetValue("reasoning_effort", out var value))
        {
            return null;
        }

        var effort = value?.ToString();
        return !string.IsNullOrWhiteSpace(effort) && SupportedEfforts.Contains(effort)
            ? effort.ToLowerInvariant()
            : null;
    }

    private static Dictionary<string, object?> ToArguments(JsonElement arguments)
        => arguments.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => ToObject(property.Value),
            StringComparer.Ordinal);

    private static object? ToObject(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => ToObject(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };

    private static UsageDetails CreateUsage(ClaudeCodeProcessResult result)
    {
        var usage = new UsageDetails
        {
            InputTokenCount = result.InputTokens,
            OutputTokenCount = result.OutputTokens,
            CachedInputTokenCount = result.CacheReadTokens
        };
        if (result.CacheWriteTokens > 0)
        {
            usage.AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["cache_creation_input_tokens"] = result.CacheWriteTokens
            };
        }

        return usage;
    }
}
