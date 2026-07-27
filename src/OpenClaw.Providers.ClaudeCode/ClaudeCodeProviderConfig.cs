namespace OpenClaw.Providers.ClaudeCode;

public sealed class ClaudeCodeProviderConfig
{
    public string ProviderId { get; set; } = "claude-code";
    public string[] Models { get; set; } = ["sonnet", "opus", "haiku"];
    public string Executable { get; set; } = "claude";
    public string? WorkingDirectory { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public bool AllowApiKeyAuthentication { get; set; }
    public string? SystemPrompt { get; set; }
}
