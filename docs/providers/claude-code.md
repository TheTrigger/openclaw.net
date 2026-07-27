# Claude Code CLI provider

The optional `OpenClaw.Providers.ClaudeCode` plugin lets a JIT OpenClaw.NET
gateway use the official Claude Code executable as an `IChatClient` provider.
This is useful for a personal, self-hosted gateway where Claude Code is already
authenticated with `claude auth login`.

The integration deliberately keeps OpenClaw in control:

- OpenClaw owns the durable session and conversation history.
- OpenClaw owns memory, tools, approvals, auditing, channels, and scheduling.
- Each provider call starts `claude -p` without Claude session persistence.
- Claude Code built-in tools, skills, hooks, MCP configuration, and project
  instructions are disabled for the provider process.
- OpenClaw tool declarations are sent through a structured protocol. Claude can
  request one tool call, then the normal OpenClaw runtime executes it.

There is therefore no second conversation store to synchronize and no
Telegram-specific behavior in the provider.

## Requirements

- A JIT-capable .NET runtime. Dynamic native plugins are not supported by the
  NativeAOT gateway.
- The official `claude` executable available to the gateway process.
- A prior `claude auth login`, or an Anthropic API key when explicitly enabled.

The provider invokes the official CLI; it does not implement or emulate the
Claude authentication protocol.

## Build the plugin

```bash
dotnet publish src/OpenClaw.Providers.ClaudeCode \
  -c Release \
  -o ./plugins/openclaw-claude-code
```

## Configure OpenClaw

```json
{
  "OpenClaw": {
    "Runtime": {
      "Mode": "jit",
      "Orchestrator": "maf"
    },
    "Llm": {
      "Provider": "claude-code",
      "Model": "sonnet",
      "ApiKey": null,
      "TimeoutSeconds": 300,
      "RetryCount": 1
    },
    "Plugins": {
      "DynamicNative": {
        "Enabled": true,
        "Load": {
          "Paths": ["./plugins/openclaw-claude-code"]
        },
        "Entries": {
          "openclaw-claude-code-provider": {
            "Config": {
              "providerId": "claude-code",
              "models": ["sonnet", "opus", "haiku"],
              "executable": "claude",
              "workingDirectory": "./workspace",
              "timeoutSeconds": 300,
              "allowApiKeyAuthentication": false
            }
          }
        }
      }
    }
  }
}
```

`Runtime.Orchestrator=maf` is recommended but not mandatory. The provider also
works with the native OpenClaw runtime because both use
`Microsoft.Extensions.AI.IChatClient`.

If you publish the gateway for this lane, disable NativeAOT:

```bash
dotnet publish src/OpenClaw.Gateway \
  -c Release \
  -p:PublishAot=false \
  -o ./publish/openclaw-gateway
```

## Authenticate

Run the login command as the same operating-system user and with the same home
directory used by the gateway:

```bash
claude auth login
claude auth status
```

For containers, mount the Claude configuration directory into both the
one-shot login container and the gateway container. Do not copy credentials
into an image layer.

By default, the provider removes environment variables whose names look like
tokens, secrets, passwords, or API keys before starting Claude. Authentication
from Claude's persisted login remains available. Set
`allowApiKeyAuthentication` to `true` only when the provider should inherit
`ANTHROPIC_API_KEY` and use metered API authentication.

## Telegram without a public IP

The Claude Code provider does not change channel networking. OpenClaw's current
Telegram adapter receives webhooks, so Telegram must be able to reach the
configured webhook URL. A private host can use a TLS reverse tunnel or a private
edge such as Tailscale Funnel/Serve where appropriate. The provider itself
requires no inbound port.

Long polling is not added by this plugin.

## Limitations

- Provider calls are currently buffered. The streaming API returns the
  completed response as one update.
- Claude Code's internal coding tools are intentionally disabled. Coding work
  uses OpenClaw's file, shell, Git, MCP, and other registered tools.
- Provider-specific prompt caching is controlled by Claude Code and may not map
  one-to-one to OpenClaw cache policy settings.
- Structured tool calls add a small protocol overhead compared with a direct
  Anthropic API provider.
- Availability and permitted use of subscription authentication remain subject
  to Anthropic's current Claude Code terms and product behavior.

## Troubleshooting

If startup says the provider is unavailable:

- confirm `Runtime.Mode` is `jit`;
- confirm `DynamicNative.Enabled` is `true`;
- point `Load.Paths` at the published plugin directory containing both
  `openclaw.native-plugin.json` and `OpenClaw.Providers.ClaudeCode.dll`;
- confirm the configured model is listed in the plugin's `models` array.

If a turn reports an authentication error, run `claude auth status` in the same
home directory and user context as the gateway. If a turn times out, increase
both `OpenClaw:Llm:TimeoutSeconds` and the plugin `timeoutSeconds`.
