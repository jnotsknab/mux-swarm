# Mux-Swarm System Reference

Source repository: https://github.com/jnotsknab/mux-swarm

Read this document before modifying any configuration file. All schemas and examples here are canonical.

## File Locations

- Config: `{{paths.config}}/Config.json` -- infrastructure, providers, MCP servers, filesystem, daemon, telemetry
- Swarm: `{{paths.config}}/Swarm.json` -- agent topology, model routing, model tuning, execution limits
- Context: `{{paths.context}}/` -- BRAIN.md, MEMORY.md, ONBOARD.md, this file
- Prompts: `{{paths.prompts}}/` -- agent system prompts (*.md)
- Skills: `{{paths.skills}}/` -- agent skill modules
- Sessions: `{{paths.sessions}}/` -- persisted session data
- Sandbox: `{{paths.sandbox}}` -- operator's working directory for agent output

## Config.json Structure

Top-level fields:

```json
{
  "setupCompleted": true,
  "isUsingDockerForExec": false,
  "mcpServers": { },
  "llmProviders": [ ],
  "filesystem": { },
  "userInfo": { },
  "telemetry": { },
  "daemon": { }
}
```

### mcpServers

Each key is a server name. Two transport types: `stdio` and `http`.

```json
"Filesystem": {
  "type": "stdio",
  "command": "npx",
  "args": ["-y", "@modelcontextprotocol/server-filesystem"],
  "env": {},
  "enabled": true
}
```

```json
"RemoteServer": {
  "type": "http",
  "url": "https://example.com/mcp/sse",
  "headers": { "Authorization": "Bearer ${API_KEY}" },
  "enabled": true
}
```

### llmProviders

Array of provider configs. First enabled entry is the default. Runtime swappable via `/provider`.

```json
"llmProviders": [
  {
    "name": "openrouter",
    "enabled": true,
    "apiKeyEnvVar": "OPENROUTER_API_KEY",
    "endpoint": "https://openrouter.ai/api/v1"
  },
  {
    "name": "ollama",
    "enabled": true,
    "endpoint": "http://localhost:11434/v1"
  }
]
```

### filesystem

```json
"filesystem": {
  "allowedPaths": ["/path/to/project", "/path/to/sandbox"],
  "sandboxPath": "/path/to/sandbox",
  "chromaDbPath": "/path/to/sandbox/chroma-db",
  "knowledgeGraphPath": "/path/to/sandbox/memory.jsonl",
  "skillsPath": "Skills/bundled",
  "sessionsPath": "Sessions",
  "promptsPath": "Prompts/Agents",
  "configDir": "Configs"
}
```

Agents can ONLY write to paths listed in `allowedPaths`. Always call `Filesystem_list_allowed_directories` before writing.

### userInfo

Optional. Injected into the preamble for all agents.

```json
"userInfo": {
  "name": "Jonathan",
  "role": "admin",
  "timezone": "America/New_York",
  "locale": "en-US",
  "info": "Prefers terse responses. Primary stack is .NET/C#."
}
```

### telemetry

Optional OpenTelemetry export.

```json
"telemetry": {
  "enabled": true,
  "endpoint": "http://localhost:4317",
  "protocol": "grpc",
  "serviceName": "mux-swarm",
  "verbosity": "standard",
  "headers": {}
}
```

Verbosity levels: `minimal` (spans only), `standard` (spans + model tags), `verbose` (full message content).

### daemon

Controls background trigger loops. Requires `--daemon` flag to activate.

```json
"daemon": {
  "enabled": true,
  "triggers": [ ]
}
```

## Daemon Triggers

There are exactly four trigger types: `watch`, `cron`, `status`, and `bridge`. No other values are valid.

### Common Trigger Fields

Every trigger shares these base fields:

| Field | Type | Default | Description |
|---|---|---|---|
| `id` | string | `""` | Unique identifier for this trigger |
| `type` | string | `""` | One of: `watch`, `cron`, `status`, `bridge` |
| `mode` | string | `"agent"` | Orchestration mode for goal execution: `agent`, `swarm`, or `pswarm` |
| `agent` | string? | null | Optional agent name override for single-agent mode goals |
| `interval` | uint | 30 | Seconds between polls (status), restart delay (bridge), or debounce (watch) |
| `cooldown` | uint | 0 | Alias for interval on watch triggers. If set (>0), takes precedence over interval |
| `restart` | bool | false | Whether to restart on failure (status) or on exit (bridge) |
| `goal` | string? | null | Goal text for watch/cron triggers. Supports template variables |
| `failThreshold` | int | 3 | Consecutive status check failures before alerting (0 = alert every failure) |

Effective interval logic: if `cooldown > 0`, use cooldown; otherwise use `interval`.

### watch

Monitors a file path pattern. Fires a goal when matching files are created or modified.

```json
{
  "id": "inbox-watcher",
  "type": "watch",
  "path": "/path/to/inbox/*.txt",
  "goal": "Read and process this file: {file}",
  "mode": "agent",
  "cooldown": 60
}
```

Required fields: `path`

Goal template variables: `{file}` (full path), `{filename}` (name only), `{timestamp}` (yyyy-MM-dd HH:mm:ss), `{id}` (trigger id)

Behavior: uses `FileSystemWatcher` on the directory with the glob as filter. Watches for `Created` and `Changed` events. Files are debounced per `cooldown`/`interval` -- the same file will not re-trigger within the cooldown window. A 500ms delay is applied after detecting a change to allow writes to complete.

### cron

Standard 5-field cron expressions (minute hour day month weekday). Supports `*`, `*/N`, `N-M`, `N,M,O`.

```json
{
  "id": "daily-report",
  "type": "cron",
  "schedule": "0 9 * * 1-5",
  "goal": "Generate the daily status report and save to the sandbox",
  "mode": "swarm"
}
```

Required fields: `schedule`

Goal template variables: `{timestamp}`, `{id}`

Behavior: calculates the next occurrence from the cron expression, sleeps until that time, fires the goal, then loops. If no future occurrence exists, the trigger stops.

### status

Health checks that monitor resources. Does not fire goals. Optionally restarts failed resources.

```json
{
  "id": "serve-alive",
  "type": "status",
  "check": "http://localhost:6723",
  "restart": true,
  "interval": 30,
  "failThreshold": 3
}
```

Required fields: `check`

Check formats:
- `http://host:port` or `https://...` -- HTTP HEAD request (10s timeout)
- `process:name` -- process lookup by name
- `tcp:host:port` -- TCP connect check (5s timeout)

Behavior: polls at `interval` seconds. Tracks consecutive failures per trigger. Only alerts when `failThreshold` consecutive failures are reached (default 3, set to 0 to alert on every failure). On alert with `restart: true`, invokes any registered restart handler for the check pattern. Logs recovery when a previously failing check succeeds.

### bridge

Spawns and supervises a long-lived child process. Designed for messaging bridges but works for any persistent sidecar.

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

Required fields: `command`

| Field | Type | Description |
|---|---|---|
| `command` | string | The executable to run |
| `args` | string? | Command arguments as a single string |
| `env` | dict? | Environment variables injected into the child process |
| `restart` | bool | Whether to restart the process if it exits |
| `interval` | uint | Restart delay in seconds (default 30) |

Behavior: starts the process with `UseShellExecute=false`, redirects stdout/stderr to daemon log with `[Bridge:id:OUT]` and `[Bridge:id:ERR]` prefixes. Working directory is set to the mux-swarm base directory. The runtime auto-injects `MUX_WS_URL` (e.g. `ws://localhost:6723/ws`) into the process environment. If the process exits and `restart` is true, waits `interval` seconds then restarts. On daemon shutdown, all bridge processes are killed with `entireProcessTree: true`.

IMPORTANT: The `type` field MUST be `bridge`. Not `discord_bridge`, not `telegram_bridge`, just `bridge`. The bridge type is generic; what makes it a Telegram or Discord bridge is the `command` and `args` pointing to the appropriate Python script.

Bot tokens and secrets should be set as environment variables in the operator's shell, not in config.

## Bridge Setup

Three bridges ship under `Runtime/`:

### Telegram

Required env var: `TELEGRAM_BOT_TOKEN`
Optional: `WHISPER_MODEL` (default: base), `ALLOWED_CHAT_IDS` (comma-separated, empty = open access)

```json
{
  "id": "telegram-bridge",
  "type": "bridge",
  "command": "uv",
  "args": "run --project Runtime Runtime/telegram_bridge.py",
  "env": { "WHISPER_MODEL": "base" },
  "restart": true,
  "interval": 10
}
```

### Discord

Required env vars: `DISCORD_BOT_TOKEN`, `DISCORD_CHANNEL_ID`
Optional: `WHISPER_MODEL`

```json
{
  "id": "discord-bridge",
  "type": "bridge",
  "command": "uv",
  "args": "run --project Runtime Runtime/discord_bridge.py",
  "env": { "WHISPER_MODEL": "base" },
  "restart": true,
  "interval": 10
}
```

The bot only responds in the channel specified by `DISCORD_CHANNEL_ID`. Create a bot at https://discord.com/developers/applications, enable Message Content Intent, and invite with message permissions.

### Signal

Required env vars: `SIGNAL_NUMBER` (E.164 format), `SIGNAL_API_URL`
Optional: `WHISPER_MODEL`, `ALLOWED_NUMBERS` (comma-separated E.164)

Requires a self-hosted signal-cli-rest-api container:

```bash
docker run -d --name signal-api -p 8080:8080 -v signal-cli-data:/home/.local/share/signal-cli bbernhard/signal-cli-rest-api
```

Link via QR code at `http://localhost:8080/v1/qrcodelink?device_name=mux-swarm`.

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

## Swarm.json Structure

```json
{
  "executionLimits": { },
  "compactionAgent": { },
  "singleAgent": { },
  "orchestrator": { },
  "agents": [ ]
}
```

### executionLimits

```json
"executionLimits": {
  "progressEntryBudget": 1000,
  "crossAgentContextBudget": 2000,
  "progressLogTotalBudget": 4500,
  "maxOrchestratorIterations": 15,
  "maxSubAgentIterations": 8,
  "maxSubTaskRetries": 4,
  "maxStuckCount": 3
}
```

### compactionAgent

```json
"compactionAgent": {
  "model": "google/gemini-3-flash-preview",
  "autoCompactTokenThreshold": 80000,
  "modelOpts": { "temperature": 0.2, "topP": 0.85, "maxOutputTokens": 4096 }
}
```

### singleAgent

```json
"singleAgent": {
  "name": "MuxAgent",
  "promptPath": "Prompts/Agents/chat_prompt.md",
  "model": "google/gemini-3.1-pro-preview",
  "modelOpts": { "temperature": 0.6, "topP": 0.95, "maxOutputTokens": 16384 },
  "mcpServers": ["Filesystem", "Memory", "BraveSearchMCP"],
  "toolPatterns": []
}
```

### orchestrator

```json
"orchestrator": {
  "promptPath": "Prompts/Agents/orchestrator.md",
  "model": "google/gemini-3.1-pro-preview",
  "modelOpts": { "temperature": 0.3, "topP": 0.9, "maxOutputTokens": 4096 },
  "toolPatterns": ["Filesystem_list_directory", "Filesystem_read_file"]
}
```

### agents

Array of agent definitions for swarm/pswarm modes.

```json
{
  "name": "WebAgent",
  "description": "Web browsing, research, and internet tasks.",
  "promptPath": "Prompts/Agents/web_agent.md",
  "model": "google/gemini-3.1-pro-preview",
  "mcpServers": ["BraveSearchMCP", "Fetch", "Filesystem"],
  "canDelegate": true,
  "toolPatterns": []
}
```

### modelOpts

Any agent, orchestrator, singleAgent, or compactionAgent supports optional model tuning:

```json
"modelOpts": {
  "temperature": 0.7,
  "topP": 0.9,
  "topK": 40,
  "maxOutputTokens": 4096,
  "frequencyPenalty": 0.0,
  "presencePenalty": 0.0,
  "seed": 42
}
```

### reasoning

Optional reasoning control:

```json
"reasoning": {
  "effort": "high",
  "output": "full"
}
```

Effort: `none`, `low`, `medium`, `high`, `extra_high`
Output: `none`, `summary`, `full`

### additionalParams

Pass-through for provider-specific parameters not covered by standard fields:

```json
"modelOpts": {
  "temperature": 0.3,
  "additionalParams": { "top_a": 0.08 }
}
```

## CLI Flags

```
--goal <text|file>         Goal input (text or file path)
--agent <name>             Single-agent mode with specified agent
--plan                     Enable plan mode
--provider <name>          Set active provider on launch
--continuous               Autonomous loop mode
--goal-id <id>             Persistent goal/session identifier
--parallel                 Parallel swarm mode
--max-parallelism <n>      Max concurrent tasks (default 4)
--min-delay <secs>         Loop delay (default 300)
--persist-interval <secs>  Session save interval
--session-retention <n>    Keep last N sessions (default 10)
--stdio                    Machine-readable NDJSON output
--serve [port]             Start web UI (default 6723)
--daemon                   Enable daemon triggers
--register                 Register as OS service
--remove                   Unregister OS service
--watchdog                 Enable external watchdog
--workflow <file>          Run workflow file
--delimiter <str>          Multi-line input delimiter
--model <id>               Override single-agent model
--mcp-strict [true|false]  Require all MCP servers
--docker-exec [true|false] Route execution through Docker
--report [session-id]      Generate report(s) and exit
--cfg <path>               Override config.json path
--swarmcfg <path>          Override swarm.json path
--clear                    Clear terminal
--help, -h                 Show help
```

Flags can be combined. Common stacks:

- Interactive with web UI: `mux-swarm --serve`
- Web UI + background triggers: `mux-swarm --serve --daemon`
- Always-on resilient stack: `mux-swarm --serve --daemon --watchdog --register`
- Headless autonomous: `mux-swarm --daemon --continuous`
- One-shot goal: `mux-swarm --goal "do the thing"`

## Interactive Commands

```
/swarm          Multi-agent orchestrated loop
/pswarm         Parallel concurrent dispatch
/agent          Single-agent conversation
/stateless      Stateless single-agent (no session persistence)
/onboard        Create or update operator profile (BRAIN.md + MEMORY.md)
/plan           Toggle plan mode
/continuous     Toggle autonomous execution (/cont shorthand)
/workflow       Run a workflow file
/resume         Resume previous session
/compact        Compress session context
/model          View model assignments
/setmodel       Change model for any agent
/swap           Swap active single-agent
/provider       View or switch provider
/limits         Display execution limits
/tools          List MCP tools
/skills         List loaded skills
/memory         View knowledge graph
/sessions       List saved sessions
/status         System status overview
/dockerexec     Toggle Docker execution
/setup          Reconfigure
/reloadskills   Refresh skills
/refresh        Full system refresh (config, MCP, skills)
/report         Generate session audit reports
/report <id>    Audit specific session
/clear          Clear terminal
/exit           Exit runtime
/qc, /qm        Exit active session
```

## OS Service Registration

Register to start on boot:

```bash
# Windows (run elevated)
mux-swarm --register --serve --daemon --watchdog

# Linux
mux-swarm --register --serve 6724 --daemon

# Remove
mux-swarm --remove
```

Platform details:
- Windows: Task Scheduler with boot trigger, 30s delay, restart on failure
- Linux: systemd user service with Restart=always, enable-linger for headless boot
- macOS: launchd LaunchAgent with RunAtLoad and KeepAlive

The three-layer resilience stack: `--register` (OS ensures process starts) + `--watchdog` (process-level restart) + daemon status triggers (subsystem-level health checks).

## Web UI

Launch with `mux-swarm --serve` (default port 6723) or `mux-swarm --serve 8080` for a custom port.

Binds to all interfaces (0.0.0.0). Accessible on LAN, Tailscale, or any network the host is connected to at `http://<host-ip>:<port>`.

Features: streaming responses, markdown rendering, plan mode prompts, live diffs panel, theme engine (Zinc, Light, Ocean, Matrix), file browser, drag-drop upload, voice input, auto-reconnect. Single static HTML file, zero dependencies.

Combine with `--daemon` for background triggers alongside the web UI. Combine with `--register` for persistence across reboots.

## Workflow Engine

Deterministic replayable pipelines as JSON:

```json
{
  "name": "Research and Report",
  "steps": [
    "/agent",
    "Search for the latest developments in quantum computing",
    "/qc",
    "/swarm",
    "Produce a formatted report from the research",
    "/qm"
  ]
}
```

Run via `mux-swarm --workflow ./path/to/workflow.json` or `/workflow` interactively.

## Event Hooks

Execute external commands on lifecycle events. Configure in swarm.json:

```json
"hooks": [
  {
    "id": "notify-slack",
    "mode": "async",
    "command": "python scripts/notify.py",
    "when": { "event": "task_complete" }
  }
]
```

Dispatch modes: `async` (fire and continue), `blocking` (wait with timeout). Add `"persistent": true` for long-lived consumers receiving NDJSON on stdin.

12 supported events: `session_start`, `session_end`, `user_input`, `text_chunk`, `turn_end`, `agent_turn_start`, `tool_call`, `tool_result`, `task_complete`, `delegation`, `daemon_start`, `daemon_stop`.

Daemon-specific hook events emitted automatically:
- `daemon_start` -- fired when daemon starts with trigger count
- `daemon_stop` -- fired on daemon shutdown
- `daemon_trigger` -- fired when a watch or cron trigger activates (summary includes trigger type and id)
- `daemon_status` -- fired on health check state changes (recovered, unhealthy, restarted, restart_failed)
- `daemon_bridge` -- fired on bridge process start and exit

## Security Recommendations

- Keep `allowedPaths` minimal and purpose-specific
- Keep `--mcp-strict` enabled (default) so startup fails if required integrations are unavailable
- Use environment variables for all credentials, never put secrets in config files
- Scope MCP servers narrowly per agent role via `mcpServers` in swarm.json
- Route execution-heavy tasks through Docker when possible (`/dockerexec`)
- Review hook commands before confirming on startup (hooks run with user permissions)
- Scope daemon watch paths narrowly
- Use `--register` from an elevated terminal only after validating daemon and hook config
- For maximum isolation, run mux-swarm inside a Docker container with only necessary volumes mounted