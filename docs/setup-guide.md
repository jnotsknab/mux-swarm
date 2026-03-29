# Setup Guide

First-time setup walkthrough for Mux-Swarm.

## Prerequisites

The only hard requirement is an **LLM provider API key** (any OpenAI-compatible endpoint) set as an environment variable.

Everything else is config-driven. The default `config.json` ships with MCP servers that rely on **Node / npm** (`npx`) and **uvx / uv**, but these are not hard dependencies, they're just what the bundled MCP servers utilize. Mux-Swarm doesn't care how your MCP servers run. You can swap in any runtime, point to a binary you built yourself, launch a local MCP server you wrote from scratch, or connect to a remote HTTP/SSE endpoint. If it speaks MCP, it works.

The default configuration also includes:

- **BRAVE_API_KEY** environment variable for the Brave Search MCP server (optional but recommended for research quality)

> **Note:** The default config uses the ChromaDB MCP server which has a known issue with Python 3.14. If you're using the defaults, it is recommended that uv / uvx is configured to use a separate Python version (e.g. 3.12). If you're bringing your own vector store MCP, this doesn't apply to you.

## Installation

**Linux / macOS:**
```bash
curl -fsSL https://www.muxswarm.dev/install.sh | bash
```

**Windows (PowerShell):**
```powershell
irm https://www.muxswarm.dev/install.ps1 | iex
```

After installation completes, open a new terminal or reload your shell:
```bash
source ~/.bashrc   # or source ~/.zshrc
```

## First Run & `/setup`

On first launch, Mux-Swarm detects that setup hasn't been completed and automatically starts the setup wizard:

```bash
mux-swarm
```

The wizard walks you through seven steps:

### Step 1 — Dependency Check

The wizard checks for tooling used by the default MCP server configuration: `python`, `node`, `npm`, `npx`, `uv`, and `uvx`. If anything is missing, the wizard will offer to install it for you. If you're using the default config, accepting is the easiest path. If you've swapped in your own MCP servers or runtimes, missing defaults here won't affect you, the check is informational.

### Step 2 — File System Access

Define the paths your agents are allowed to read and write. Provide one or more comma-separated paths, then select which path to use as the agent output sandbox, this is where agents will write files and artifacts.

```
Paths: ~/mux-sandbox
```

### Step 3 — Storage Configuration

Configure where ChromaDB stores persistent vector data (embeddings, indexes) and where the knowledge graph file lives. Press Enter to accept the defaults inside your sandbox.

### Step 4 — Model Endpoint Configuration

Enter your OpenAI-compatible API endpoint and the environment variable name holding your API key:

```
Endpoint: https://openrouter.ai/api/v1
Env var name: OPENROUTER_API_KEY
```

Raw API keys can be pasted directly (not recommended — they're only held in memory for the current session). Leave blank for local endpoints like Ollama.

### Step 5 — User Profile (Optional)

Tell your agents who you are. This helps agents personalize responses and address you by name. All fields are optional — press Enter to skip any, or type `skip` to skip the entire step.

```
Name: John Doe
Role: systems analyst
Timezone: America/Chicago
Locale: en-US
Info: My preferred language of choice for backend is c#
```

### Step 6 — MCP API Keys

The wizard checks that environment variable names are configured for any MCP servers that require API keys (e.g. Brave Search). Press Enter to keep defaults, or provide alternate env var names.

### Step 7 — MCP Server Validation

Each enabled MCP server is validated for connectivity. On success you'll see a summary like:

```
✓ Loaded 9 tools from Memory
✓ Loaded 14 tools from Filesystem
✓ Loaded 1 tools from Fetch
✓ Loaded 13 tools from ChromaDB
✓ Loaded 6 tools from BraveSearchMCP
✓ Loaded 3 tools from PythonReplMCP
```

> **Troubleshooting:** If a server fails to connect and you're running in strict mode (default), Mux-Swarm will exit. You can disable a problematic server by editing `Configs/Config.json` and setting `"enabled": false` on that server, then re-launch. You can also replace any default MCP server with your own — point to a local binary, a custom runtime, or a remote HTTP/SSE endpoint. As long as it implements the MCP protocol, Mux-Swarm will pick it up. Re-run `/setup` at any time to reconfigure.

## Verifying Your Setup

The real test is whether your agents can make LLM calls end to end. Launch a single-agent session and send a simple prompt:

```bash
mux-swarm
> /agent
> Who are you and what can you help me with?
```

The agent should respond with a personalized greeting (using the name and preferences from your profile) and a summary of its capabilities. If you get a coherent response, your provider, API key, MCP tools, and agent config are all wired correctly. Type `/qc` to exit the session.

For a goal-driven run:
```bash
mux-swarm --goal "List the files in my sandbox and summarize what you find"
```

You can also use `/status` for a quick overview of your runtime configuration — active provider, model assignments, tool count, skill count, and session count:

```
╭─Mux-Swarm Status──────────────────────────────────────────────────╮
│   Provider:    default (https://openrouter.ai/api/v1)             │
│   Agent:       MuxAgent                                           │
│   Models:                                                         │
│                Orchestrator -> google/gemini-3.1-pro-preview      │
│                Compaction -> google/gemini-3-flash-preview        │
│                WebAgent -> google/gemini-3.1-pro-preview          │
│                CodeAgent -> google/gemini-3.1-pro-preview         │
│                MemoryAgent -> google/gemini-3-flash-preview       │
│                DataAnalysisAgent -> google/gemini-3.1-pro-preview │
│   Tools:       46                                                 │
│   Skills:      18                                                 │
│   Sessions:    1                                                  │
│   Docker Exec: disabled                                           │
╰───────────────────────────────────────────────────────────────────╯
```

## Next Steps

- **Launch the web UI**: `mux-swarm --serve` and open `http://localhost:6723`
- **Launch the full multi-agent swarm**: `/swarm`
- **Try parallel dispatch**: `/pswarm` or `mux-swarm --parallel --goal "<goal>"`
- Explore [Configuration](../README.md#configuration) to customize providers, agent roles, model routing, and MCP server scoping
- Enable plan mode with `/plan` for interactive approval before agent execution
- Add custom [Skills](../README.md#skills-skills) to extend agent capabilities
- Use [Scoped Instances](../README.md#scoped-instances) for multi-user or multi-environment deployments

## Messaging Bridges (Optional)

Mux-Swarm ships with Telegram, Discord, and Signal bridges that let you interact with your agents from your phone or any device with a messaging app. Bridges run as daemon triggers, the runtime manages their lifecycle automatically.

### Prerequisites

- **uv** (already installed if you followed setup)
- A bot token or account for your platform:
  - **Telegram**: Create a bot via [@BotFather](https://t.me/BotFather) and copy the token
  - **Discord**: Create an application at the [Discord Developer Portal](https://discord.com/developers/applications), add a bot, enable Message Content Intent, and copy the token. Invite the bot to your server with message permissions.
  - **Signal**: A phone number registered with Signal and a self-hosted [signal-cli-rest-api](https://github.com/bbernhard/signal-cli-rest-api) container (see below)

### Step 1 -- Set your credentials

Add tokens/config to your shell profile so they persist across sessions:

**Linux / macOS:**
```bash
echo 'export TELEGRAM_BOT_TOKEN="your-token-here"' >> ~/.bashrc
source ~/.bashrc
```

**Windows (PowerShell):**
```powershell
[System.Environment]::SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", "your-token-here", "User")
```

For Discord, use `DISCORD_BOT_TOKEN` instead. Discord also requires `DISCORD_CHANNEL_ID` set to the channel ID the bot should listen on.

For Signal, set `SIGNAL_NUMBER` and `SIGNAL_API_URL`:
```bash
echo 'export SIGNAL_NUMBER="+1XXXXXXXXXX"' >> ~/.bashrc
echo 'export SIGNAL_API_URL="http://localhost:8080"' >> ~/.bashrc
source ~/.bashrc
```

**Signal setup:** Signal requires a self-hosted REST API container. Start it with:
```bash
docker run -d --name signal-api \
  -p 8080:8080 \
  -v signal-cli-data:/home/.local/share/signal-cli \
  bbernhard/signal-cli-rest-api
```

Then link it to your Signal account by opening `http://localhost:8080/v1/qrcodelink?device_name=mux-swarm` in a browser and scanning the QR code with Signal on your phone (Settings > Linked Devices > Link New Device). Verify registration with `curl http://localhost:8080/v1/about`.

### Step 2 -- Add a bridge trigger

Add a bridge entry to the `daemon.triggers` array in `Configs/config.json`:

**Telegram:**
```json
{
  "id": "telegram-bridge",
  "type": "bridge",
  "command": "uv",
  "args": "run --project Runtime Runtime/telegram_bridge.py",
  "env": {
    "WHISPER_MODEL": "base"
  },
  "restart": true,
  "interval": 10
}
```

**Discord:**
```json
{
  "id": "discord-bridge",
  "type": "bridge",
  "command": "uv",
  "args": "run --project Runtime Runtime/discord_bridge.py",
  "env": {
    "WHISPER_MODEL": "base"
  },
  "restart": true,
  "interval": 10
}
```

**Signal (CLI args):**
```json
{
  "id": "signal-bridge",
  "type": "bridge",
  "command": "uv",
  "args": "run --project Runtime Runtime/signal_bridge.py --number +1XXXXXXXXXX --api http://localhost:8080 --ws ws://localhost:6724/ws",
  "restart": true,
  "interval": 60
}
```

**Signal (env vars):**
```json
{
  "id": "signal-bridge",
  "type": "bridge",
  "command": "uv",
  "args": "run --project Runtime Runtime/signal_bridge.py",
  "env": {
    "SIGNAL_NUMBER": "+1XXXXXXXXXX",
    "SIGNAL_API_URL": "http://localhost:8080",
    "WHISPER_MODEL": "base"
  },
  "restart": true,
  "interval": 60
}
```

Replace `+1XXXXXXXXXX` with your registered Signal number. Adjust the `--api` and `--ws` URLs if your signal-cli container or serve port differ.

The runtime automatically resolves and injects the correct websocket URI so bridges connect to the correct endpoint. All other config (tokens, channel IDs, allowed users) come from environment variables. You can override any value via the `env` block if needed.

The bridge starts automatically, connects to the runtime over WebSocket, and begins listening on your messaging platform. Send a message to your bot or linked number, and it arrives at the agent. Responses stream back.

### Step 3 -- Launch with daemon
```bash
mux-swarm --serve --daemon
```

### Audio Transcription

All three bridges support voice messages and audio attachments via local Whisper (no API key needed). FFmpeg is resolved automatically via the `static-ffmpeg` Python package. If you want to use a different Whisper model, set `WHISPER_MODEL` in the bridge's `env` block (options: `tiny`, `base`, `small`, `medium`, `large`, `turbo`).

### Restricting Access

**Telegram:** Set `ALLOWED_CHAT_IDS` as a comma-separated list of Telegram chat IDs. Empty means open access. Get your chat ID by sending `/start` to the bot.

**Discord:** The bot only responds in the channel specified by `DISCORD_CHANNEL_ID`.

**Signal:** Set `ALLOWED_NUMBERS` as a comma-separated list of E.164 phone numbers (e.g. `+15551234567,+15559876543`). Empty means open access.

## Telemetry (Optional)

Mux-Swarm exports OpenTelemetry traces, logs, and metrics when configured. This gives you full visibility into agent sessions, tool calls, delegation chains, and token usage in any OTLP-compatible backend.

### Quick Start with Jaeger

Start a local Jaeger instance:
```bash
docker run -d --name jaeger -p 4317:4317 -p 16686:16686 jaegertracing/jaeger:latest
```

Add the telemetry block to `Configs/config.json`:
```json
"telemetry": {
  "enabled": true,
  "endpoint": "http://localhost:4317",
  "protocol": "grpc",
  "serviceName": "mux-swarm"
}
```

Launch mux-swarm, run any agent session, then open `http://localhost:16686`. You'll see traces with the span tree: `runtime_startup` for init logs, and `agent_session > agent_turn > tool_call` for agent execution. Swarm mode adds `swarm_session > orchestrator_turn > delegation` wrapping the agent spans.

### Verbosity Levels

Control how much detail is exported via the `verbosity` field:

- **minimal**: Spans only, no model or content tags
- **standard** (default): Spans with model, agent, and tool tags
- **verbose**: Full message content and tool args/results attached as span events (use for debugging, not production)

### Enterprise Backends

The telemetry config works with any OTLP receiver. For metrics (Prometheus, Grafana) alongside traces, add an OTEL Collector between mux-swarm and your backends. No code changes needed, just infrastructure config. See the [README telemetry section](../README.md#telemetry-telemetry) for the full schema.