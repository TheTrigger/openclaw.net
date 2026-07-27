using System.Text.Json;
using OpenClaw.PluginKit;

namespace OpenClaw.Providers.ClaudeCode;

public sealed class ClaudeCodeProviderPlugin : INativeDynamicPlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public void Register(INativeDynamicPluginContext context)
    {
        var config = ReadConfig(context.Config);
        config.ProviderId = NormalizeRequired(config.ProviderId, "providerId");
        config.Executable = NormalizeRequired(config.Executable, "executable");
        config.Models = NormalizeModels(config.Models);

        if (config.TimeoutSeconds <= 0)
            throw new InvalidOperationException("Claude Code provider config field 'timeoutSeconds' must be positive.");

        if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
        {
            config.WorkingDirectory = Path.GetFullPath(config.WorkingDirectory.Trim());
            if (!Directory.Exists(config.WorkingDirectory))
                throw new InvalidOperationException($"Claude Code working directory '{config.WorkingDirectory}' does not exist.");
        }

        context.RegisterProvider(
            config.ProviderId,
            config.Models,
            new ClaudeCodeChatClient(config, context.Logger));
    }

    private static ClaudeCodeProviderConfig ReadConfig(JsonElement? config)
    {
        if (config is null || config.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return new ClaudeCodeProviderConfig();

        try
        {
            return config.Value.Deserialize<ClaudeCodeProviderConfig>(JsonOptions)
                ?? throw new InvalidOperationException("Claude Code provider config could not be deserialized.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Claude Code provider config is invalid JSON.", ex);
        }
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"Claude Code provider config field '{fieldName}' is required.");

        return normalized;
    }

    private static string[] NormalizeModels(IEnumerable<string>? models)
    {
        var normalized = models?
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        return normalized.Length > 0
            ? normalized
            : throw new InvalidOperationException("Claude Code provider config field 'models' must contain at least one model.");
    }
}
