using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.PluginKit;
using OpenClaw.Providers.ClaudeCode;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ClaudeCodeProviderTests
{
    [Fact]
    public void Register_DefaultConfig_RegistersClaudeCodeProvider()
    {
        var context = new CapturingPluginContext(null);

        new ClaudeCodeProviderPlugin().Register(context);

        var provider = Assert.Single(context.Providers);
        Assert.Equal("claude-code", provider.ProviderId);
        Assert.Equal(["sonnet", "opus", "haiku"], provider.Models);
        Assert.IsType<ClaudeCodeChatClient>(provider.Client);
    }

    [Fact]
    public void Register_BlankModels_Throws()
    {
        var context = new CapturingPluginContext(JsonSerializer.SerializeToElement(new
        {
            models = Array.Empty<string>()
        }));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ClaudeCodeProviderPlugin().Register(context));

        Assert.Contains("models", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseAsync_FinalResponse_ReturnsTextAndUsage()
    {
        var runner = new FakeRunner(new ClaudeCodeProcessResult(
            """{"kind":"final","text":"done","tool":"","arguments":{}}""",
            12,
            7,
            3,
            4));
        using var client = CreateClient(runner);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions
            {
                ModelId = "sonnet",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["reasoning_effort"] = "high"
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("done", response.Text);
        Assert.Equal(12, response.Usage?.InputTokenCount);
        Assert.Equal(7, response.Usage?.OutputTokenCount);
        Assert.Equal(3, response.Usage?.CachedInputTokenCount);
        Assert.Equal(4, response.Usage?.AdditionalCounts?["cache_creation_input_tokens"]);
        Assert.Equal("sonnet", runner.LastInvocation?.Model);
        Assert.Equal("high", runner.LastInvocation?.Effort);
        Assert.Contains("\"conversation\"", runner.LastInvocation?.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"hello\"", runner.LastInvocation?.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseAsync_ToolCall_ReturnsFunctionCallContent()
    {
        var runner = new FakeRunner(new ClaudeCodeProcessResult(
            """{"kind":"tool_call","text":"","tool":"weather","arguments":{"city":"Rome","days":2}}""",
            1,
            1,
            0,
            0));
        using var client = CreateClient(runner);
        var tool = AIFunctionFactory.CreateDeclaration(
            "weather",
            "Get the weather.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    city = new { type = "string" },
                    days = new { type = "integer" }
                },
                required = new[] { "city" }
            }),
            returnJsonSchema: null);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "weather in Rome")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages.SelectMany(static message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("weather", call.Name);
        var arguments = call.Arguments ?? throw new InvalidOperationException("Expected tool arguments.");
        Assert.Equal("Rome", arguments["city"]);
        Assert.Equal(2L, arguments["days"]);
        Assert.Contains("\"weather\"", runner.LastInvocation?.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"tool_call\"", runner.LastInvocation?.JsonSchema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseAsync_UnavailableTool_RejectsResponse()
    {
        var runner = new FakeRunner(new ClaudeCodeProcessResult(
            """{"kind":"tool_call","text":"","tool":"shell","arguments":{}}""",
            1,
            1,
            0,
            0));
        using var client = CreateClient(runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "run it")],
                new ChatOptions(),
                TestContext.Current.CancellationToken));

        Assert.Contains("unavailable tool", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithToolResult_SerializesOpenClawOwnedHistory()
    {
        var request = ClaudeCodePromptProtocol.Build(
            [
                new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-1",
                        "weather",
                        new Dictionary<string, object?> { ["city"] = "Rome" })
                ]),
                new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent("call-1", "sunny")
                ])
            ],
            null);

        Assert.Contains("\"tool_call\"", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"tool_result\"", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"sunny\"", request.Prompt, StringComparison.Ordinal);
    }

    private static ClaudeCodeChatClient CreateClient(IClaudeCodeProcessRunner runner)
        => new(
            new ClaudeCodeProviderConfig
            {
                ProviderId = "claude-code",
                Models = ["sonnet"]
            },
            NullLogger.Instance,
            runner);

    private sealed class FakeRunner(ClaudeCodeProcessResult result) : IClaudeCodeProcessRunner
    {
        public ClaudeCodeInvocation? LastInvocation { get; private set; }

        public Task<ClaudeCodeProcessResult> RunAsync(ClaudeCodeInvocation invocation, CancellationToken ct)
        {
            LastInvocation = invocation;
            return Task.FromResult(result);
        }
    }

    private sealed class CapturingPluginContext(JsonElement? config) : INativeDynamicPluginContext
    {
        public string PluginId => "test-claude-code-provider";
        public JsonElement? Config { get; } = config;
        public ILogger Logger => NullLogger.Instance;
        public List<(string ProviderId, string[] Models, IChatClient Client)> Providers { get; } = [];

        public void RegisterTool(ITool tool) { }
        public void RegisterChannel(IChannelAdapter adapter) { }
        public void RegisterCommand(string name, string description, Func<string, CancellationToken, Task<string>> handler) { }
        public void RegisterProvider(string providerId, string[] models, IChatClient client) => Providers.Add((providerId, models, client));
        public void RegisterMemoryProvider(string providerId, Func<NativeDynamicMemoryProviderContext, IMemoryStore> factory) { }
        public void RegisterHook(IToolHook hook) { }
        public void RegisterService(INativeDynamicPluginService service) { }
        public void RegisterResultInterceptor(IToolResultInterceptor interceptor) { }
    }
}
