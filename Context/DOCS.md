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
  "daemon": { },
  "serve": { }
}
```

### mcpServers

Each key is a server name. Two transport types: `stdio` and `http`. A top-level
`mcpConnectTimeoutSeconds` (default 90) bounds how long each server's connect + initial
tool-list may take before it is skipped; a slow cold-starting server (npx download, venv build,
remote HTTP MCP) that exceeds it is reported as an error rather than blocking startup forever.

```json
"mcpConnectTimeoutSeconds": 90
```

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

`Filesystem` and `Shell` are served by **native in-process tools** by default (no external
MCP process); they still appear here so the two-gate model is unchanged (config `enabled` =
global on/off; per-agent `mcpServers` in swarm.json = who gets them). A native entry has
`"command": "native-runtime-tools"`. Listing the npx `@modelcontextprotocol/server-filesystem`
form instead overrides Filesystem back to the external server.

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

### serve

Serve-layer (web UI / HTTP API) settings. Entire object is optional; absent or
partial configs fall back to safe defaults (read-only, no auth). Default keeps the
runtime **zero-auth-by-design** behind an nginx perimeter.

```json
"serve": {
  "editable": false,
  "auth": { "enabled": false, "token": "", "scheme": "bearer" }
}
```

| Key | Default | Meaning |
|-----|---------|---------|
| `editable` | `false` | When true, IDE write endpoints (`POST /api/save`, `POST /api/fs`) are enabled for the **sandbox** root only. Sessions stay read-only. When false they return `403`. |
| `auth.enabled` | `false` | Master switch for app-level bearer auth. When false, behaves exactly as before (open). |
| `auth.token` | `""` | Literal token, or an env-var reference: `{VAR}`, `${VAR}`, or `$VAR` (resolved at startup). |
| `auth.scheme` | `"bearer"` | Auth scheme. |

When `auth.enabled` is true, **all** `/api/*` requests and the `/ws` upgrade require
the token (HTTP `Authorization: Bearer <token>`; WS `?token=<token>` query param or
`Sec-WebSocket-Protocol: bearer,<token>`). Static assets stay open so the web app
can load its login prompt. Token compare is constant-time, never logged, and never
returned by `/api/config` (which exposes only a boolean `authRequired`). If enabled
but no token resolves, auth stays inactive with a warning (never silently locks out).

## Execution Sandbox

Native shell + Python execution (`repl_shell_exec`, `execute_command_async`, the REPL worker, and
`install_package_async`) can be confined to a sandbox. The `sandbox` block in **config.json** selects the
backend; both exec surfaces run inside the SAME confinement (model code cannot escape by picking a
different tool). The sandbox is resolved once per session and is per-agent scoped (one sandbox per
sub-agent). An invalid/unavailable backend fails LOUD at first tool use -- never a silent fall back to
host execution.

```json
"sandbox": {
  "backend": "host",
  "image": "python:3.12-slim",
  "network": false,
  "allowedDomains": [],
  "command": "",
  "runtime": ""
}
```

| Field | Meaning |
|---|---|
| `backend` | `host` (default, no sandbox) \| `docker` \| `podman` \| `nerdctl` \| `gvisor` \| `kata` \| `bwrap` \| `firejail` \| `sandbox-exec` \| `custom`. |
| `image` | Container image for OCI backends. Ignored by wrapper/host. Default `python:3.12-slim`. |
| `network` | When `allowedDomains` is empty: `true` = open egress, `false` = air-gapped. Ignored when an allowlist is set. |
| `allowedDomains` | Non-empty => the sandbox reaches ONLY these hosts via an injected CONNECT-filtering proxy on an internal (egress-less) network. **OCI backends only.** Deny-by-default. |
| `command` | Template for the `custom` backend. Placeholders `{cmd}` `{workdir}` `{image}`. Required when `backend: custom`. |
| `runtime` | Explicit OCI runtime passed as `--runtime=<value>` for OCI backends. Empty => engine default, except `gvisor`=>`runsc` and `kata`=>`kata-runtime` which imply their runtime. Lets you layer a microVM runtime onto a base engine (e.g. `backend: podman`, `runtime: kata-runtime`). Ignored by wrapper/custom/host. |

### Isolation tiers (weakest -> strongest)

| Backend | Mechanism | Isolation |
|---|---|---|
| `host` | none | no isolation (runs natively) |
| `bwrap` / `firejail` | Linux namespaces + seccomp | OS-native (Linux only) |
| `sandbox-exec` | macOS Seatbelt (SBPL) | OS-native (macOS only) |
| `docker` / `podman` / `nerdctl` | OCI container (namespaces/cgroups) | container |
| `gvisor` | docker + `--runtime=runsc` | user-space kernel (syscall interposition) |
| `kata` | docker/podman + `--runtime=kata-runtime` | **microVM** -- a real guest kernel via hardware virtualization |
| `custom` | user template | whatever the template points at |

OCI backends run a persistent per-session container (`sleep infinity`) that the session's shell jobs and
Python worker `exec` into. `gvisor` and `kata` reuse that exact lifecycle -- they only change the OCI
runtime, so all of the OCI features (network allowlist proxy, allowed-path bind mounts mapped to
`filesystem.securityMode`, OCI hardening `--cap-drop=ALL --security-opt=no-new-privileges`, self-heal,
Windows UNC drive-mapping) apply unchanged.

### microVM isolation (kata)

`kata` is the strongest local tier: a true microVM with its own guest kernel (QEMU / Cloud-Hypervisor /
Firecracker under Kata), giving a hardware-virtualization boundary instead of a shared host kernel. Use it
when running untrusted code on a multi-tenant host (a container/gVisor boundary may be insufficient).

- **Requirements:** Linux + hardware virtualization (`/dev/kvm`) + `kata-containers` installed and
  registered as a runtime for the OCI engine. Both are validated up front; a missing OS/KVM/runtime is a
  hard, legible error (never a silent fallback to host or a weaker backend).
- **Swap it in:** `/sandbox kata` (or set `sandbox.backend: kata` in config.json). To layer Kata onto
  podman/nerdctl, keep `backend` as that engine and set `runtime` to the engine's kata runtime
  (e.g. `io.containerd.kata.v2` for nerdctl/containerd).

**Deliberately NOT supported (deferred):** `libkrun`/`krun` and direct Firecracker/Cloud-Hypervisor/E2B
control planes. The sandbox lifecycle is built on container `exec`; `krun` microVMs do not service
`exec` into the guest (exec runs on the host kernel), and the VM-direct platforms have no `exec`
semantics at all -- both would need a separate vsock/SSH exec channel and control plane. Kata is the
microVM path that fits the existing model.

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
  "maxStuckCount": 3,
  "compactionCharBudget": 6000,
  "contextInjection": "full",
  "compactionMaxMessageChars": 2500,
  "subAgentSummaryMode": "auto",
  "delegationRetentionDays": 30,
  "activityTimeoutSeconds": 3600,
  "maxToolIterationsPerTurn": 1000,
  "maxAutoContinuesPerTurn": 3
}
```

Any key may be omitted; missing keys inherit the built-in default shown above.

- `progressEntryBudget` - char budget for a single sub-agent result handed back to the lead. Governs the size-tiered delegation engine: results <= this go inline; the spill-to-pointer threshold is `3x` this value.
- `crossAgentContextBudget` - char cap on prior-agent context auto-injected into a new sub-agent (swarm/pswarm only).
- `progressLogTotalBudget` - char budget for the orchestrator running progress log; also the soft cap for the delegation blowout gate (hard cap = 2x).
- `maxOrchestratorIterations` / `maxSubAgentIterations` - orchestration and sub-agent loop ceilings.
- `maxSubTaskRetries` / `maxStuckCount` - retry + stall-detection ceilings.
- `compactionCharBudget` / `compactionMaxMessageChars` - conversation-compaction budgets.
- `contextInjection` - `full` (default) injects full prior context; other modes trim it.
- `subAgentSummaryMode` - how mid-size sub-agent results are compacted: `auto`/`llm` (LLM summary + extracted refs) or `extractive` (no LLM call, money-saving).
- `delegationRetentionDays` - days spilled sub-agent raw outputs are kept under `<sandbox>/delegations` (or `%LOCALAPPDATA%/Mux-Swarm/delegations`) before a startup prune. 0 disables pruning.
- `activityTimeoutSeconds` - deadman's-switch window for a single streaming response (reset on every chunk) and the OpenAI client HTTP NetworkTimeout. NOT an idle-between-turns timeout. Default 3600 (1h) so long tool-running turns and slow providers are tolerated.
- `maxToolIterationsPerTurn` - max model->tool round-trips per turn before the invocation middleware stops looping (<= 0 = unlimited).
- `maxAutoContinuesPerTurn` - how many times a turn may transparently continue itself after `finish_reason == length` (output/reasoning cap hit mid-generation). 0 disables.


### compactionAgent

```json
"compactionAgent": {
  "model": "google/gemini-3-flash-preview",
  "autoCompactTokenThreshold": 200000,
  "modelOpts": { "temperature": 0.2, "topP": 0.85, "maxOutputTokens": 4096 }
}
```

`autoCompactTokenThreshold` is the session-token count at which a single-agent session auto-compacts its history (default 200000). It also drives the docked context-meter denominator. `0` disables auto-compaction.

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

### teams

Optional array of named teams (additive; absent leaves all behavior unchanged). A team is a
selection over the existing `agents[]` plus a coordination policy, launched with `/teams <name>`.

```json
{
  "name": "research-build",
  "description": "Research a topic then implement + document it.",
  "lead": "Orchestrator",
  "members": ["WebAgent", "CodeAgent", "DocumentationAgent"],
  "coordination": "taskboard",
  "maxParallel": 4,
  "agentView": "auto",
  "autoRun": false,
  "autoRunIntervalSeconds": 15,
  "memberContext": "persistent",
  "pickupPolicy": "assigned",
  "mailbox": true
}
```

- `lead` - the coordinating agent the user talks to (defaults to `Orchestrator`).
- `members` - agent names resolved against `agents[]`; spawned as isolated sub-agent sessions.
- `coordination` - `fanout` (independent concurrent tasks) or `taskboard` (shared dependency-gated
  task graph with file-locked claiming). `pipeline` is reserved (falls back to fanout).
- `maxParallel` - max members running at once; falls back to `/maxp` / `executionLimits`.
- `agentView` - `auto` (always-on status strip) or `minimal`.
- `autoRun` - taskboard only: start the background auto-runner at launch (see Teams & TaskBoard).
- `autoRunIntervalSeconds` - auto-runner poll interval (default 15, floor 3).
- `memberContext` - how a member's session carries across task pickups: `persistent` (default; warm
  session, context accumulates, auto-compacted at the member threshold below) or `fresh` (clean
  session each task, no carry-over).
- `pickupPolicy` - how members acquire work when the peer self-claim engine runs: `assigned`
  (default; a member only auto-claims tasks whose `assignee` is itself) or `open` (any idle member
  may also claim any unassigned, unblocked, ready task - a self-organizing pool). Claiming is
  file-locked, so both are race-safe.
- `mailbox` - inter-agent messaging (default `true`). When on, the lead AND every member get the
  `send_message` / `read_inbox` tools, members drain their inbox into each task brief, and an idle
  member wakes to handle an incoming question/handoff. Set `false` to disable messaging entirely
  for the team. See [Mailbox](#mailbox-inter-agent-messaging).

The per-member context ceiling lives on `compactionAgent` (next to `autoCompactTokenThreshold`):

- `memberAutoCompactTokenThreshold` - when a persistent member's session is estimated past this many
  tokens, its history is summarized and reseeded (the same mechanism the single-agent loop uses).
  Members get their own knob so they can run tighter than the lead. `0` (default) falls back to a
  bounded runtime default (never unbounded).

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
--sandbox [backend [img]]  Run shell/REPL exec inside a sandbox (host|docker|podman|gvisor|kata|bwrap|...)
--acp                      Zed Agent Client Protocol transport (JSON-RPC over stdio)
--stateless                Boot straight into a stateless single-agent loop
--agent-mode               Boot straight into the standard single-agent loop
--swarm / --pswarm         Boot straight into multi-agent / parallel swarm
--teams                    Boot into teams mode
--sub / --subagents        Enable sub-agent delegation in the single-agent loop
--psub / --parasubagents   Enable parallel sub-agent delegation in the single-agent loop
--giga                     Giga mode (agent can spawn teams + author/run workflows)
--verbose                  Verbose MCP/init logging
--report [session-id]      Generate report(s) and exit
--cfg <path>               Override config.json path
--swarmcfg <path>          Override swarm.json path
--clear                    Clear terminal
--help, -h                 Show help
```

Persist any combination of launch flags so they apply every start with `/startargs <args>`
(stored in `config.startupArgs`; clear with `/startargs clear`). Real flags passed on the
command line override the persisted set.

Flags can be combined. Common stacks:

- Interactive with web UI: `mux-swarm --serve`
- Web UI + background triggers: `mux-swarm --serve --daemon`
- Always-on resilient stack: `mux-swarm --serve --daemon --watchdog --register`
- Headless autonomous: `mux-swarm --daemon --continuous`
- One-shot goal: `mux-swarm --goal "do the thing"`

## Interactive Commands

### Modes
```
/swarm          Multi-agent orchestrated loop
/pswarm         Parallel concurrent dispatch
/agent          Single-agent conversation
/stateless      Stateless single-agent (no session persistence)
/sub, /psub     Enable sub-agent / parallel sub-agent delegation in the single-agent loop
/plan           Toggle plan mode (agent presents a plan + asks approval before executing)
/ultra          Toggle deep-reasoning mode (plan + max reasoning budget + heavy delegation)
/giga           Toggle giga mode (ultra + agent can spawn teams and author/run workflows)
/continuous     Toggle autonomous execution (/cont shorthand)
/workflow <f>   Run a deterministic, replayable workflow file
```

### Teams
```
/teams [name]   List configured teams, or launch one (members stream into the Agent View)
/createteam     Guided wizard to create a team (saved to swarm.json teams[])
/kanban         (in a taskboard team) editable board: add/assign/move/ready/remove tasks +
                toggle the peer self-claim engine. /kanban help for the full verb list.
```

### Session (inside an active single-agent session)
```
/compact [msg]  Compact session context now (optional steering instruction for the summarizer)
/handoff [msg|path.md]  Write a resume-from-cold handoff doc (active model) to the sandbox reports dir
/heal, /reflect [deep] [msg]  Review the session and propose BRAIN/MEMORY self-heal entries (approve to apply)
/fix [symptom]  Diagnose + propose repairs for a misbehaving subsystem (config, MCP, proxy, skills, sandbox)
/diff           Show the working-tree git diff (collapsible)
/doctor         Health check: providers, MCP servers, sandbox, proxy (no model call)
/cost           Session token usage + estimated cost ($ for API providers; usage-only for subscriptions)
/init           Analyze the workspace and scaffold a project context file (AGENTS.md)
/review         AI review of the working-tree diff (read-only findings)
!<command>      Run a shell command and inject its output into the conversation context
/tokens         Show the current token / context breakdown
/effort         Cycle the live reasoning-effort tier (also Shift+Tab)
/undo           Drop the last exchange from history
/retry          Re-run the last turn
/wipe           Clear the session history
/tag <text>     Tag the live session for easy resume/search
/detach         Park the live session to the background (re-enter with /attach)
/qc, /qm        Exit the active session
```

### Session lifecycle
```
/resume         Resume a previous single-agent session
/sessions       List saved sessions with type + agent count
/attach [id]    Re-enter a session parked via /detach (no id = pick from list)
/background, /bg  Manage detached background jobs (alias of the old /detach job command)
/report [id]    Generate session audit report(s); /report <id> audits a specific session
```

### Config & models
```
/model          View model assignments
/setmodel       Change the model for any agent / orchestrator / compaction agent
/swap           Swap the active single-agent
/newagent, /editagent, /delagent  Create / edit / remove a swarm agent
/provider       View or switch the active LLM provider
/login [prov]   Log in to a subscription provider (claude|codex|kimi|...) via the CLIProxyAPI sidecar
/ping [prov]    Test the CLIProxyAPI sidecar + show per-provider login readiness
/proxy          Manage the CLIProxyAPI sidecar: /proxy status | /proxy update
/config         Show ALL config settings (every key is /set-able)
/set <key> <v>  Edit any config key (e.g. /set ultra.thinkingBudget 20000)
/showreasoning  full | summary (shown, grey italic) | none (hidden); persists to config
/sandbox [b][i] Show or swap the exec sandbox backend (host|docker|podman|gvisor|kata|bwrap|...)
/dockerexec     Toggle Docker execution mode
/startargs <a>  Persist launch flags to run every start (clear with /startargs clear)
/limits         Display current execution limits
/workspace <p>  Show or set the @-file workspace root
/addcontext     Configure what context each agent is injected with
```

### System
```
/onboard        Create or update operator profile (BRAIN.md + MEMORY.md)
/tools          List available MCP + native tools
/skills         List loaded skills
/memory         View knowledge graph
/status         System status overview (provider, models, tools, skills, sessions)
/setup          Run initial setup / reconfigure
/reloadskills   Refresh the skills directory
/installskill   Install a skill by name (openai/skills, VoltAgent) or from a GitHub repo URL
/refresh        Full system refresh (config, MCP servers, skills)
/classic, /tui  Switch renderer (classic line-by-line / live full-screen TUI)
/verbose, /sav  Toggle full-vs-collapsed tool output / sub-agent output
/shortcuts      Show keyboard shortcuts (alias /keys)
/clear          Clear terminal
/exit           Exit runtime
```

## Teams & TaskBoard

A **team** runs one selected `agents[]` roster under a **lead** agent. `/teams <name>` drops you
into an interactive session WITH the lead; you drive the team by talking to the lead, which holds
team-only tools. Members are spawned on demand as isolated sub-agent sessions and stream live in
the Agent View. State persists under `<install>/Teams/{slug}/` (`team.json` + `tasks/{id}.json`),
so a team is resumable across restarts. Off-team behavior is unchanged.

**Keys:** `\` opens the Agent View dashboard (foreground one member with Enter); `Ctrl+T` toggles
the color-coded TaskBoard strip (pending=dim, in-progress=accent, blocked=warn, done=ok, failed=err).

### Coordination: fanout

Independent work, no dependencies. The lead has `team_dispatch` to fan a batch of member tasks out
concurrently (bounded by `maxParallel`) and collect their results. Both coordination modes also have
the always-on **mailbox** (`send_message` / `read_inbox` on the lead and every member - see
[Mailbox](#mailbox-inter-agent-messaging)).

### Coordination: taskboard

A shared, persisted, dependency-gated task graph. Claiming is file-locked (no two members ever own
one task); a task blocked by unfinished dependencies cannot run; completing a blocker auto-unblocks
its dependents. M2 is lead-orchestrated: the lead creates and assigns; members execute what they are
handed. The lead has these tools:

```
task_create    Create a task. Args: subject, description, blockedBy (csv task ids),
               assignee (member; required for auto-run), startInSeconds (timer/trigger delay).
task_assign    Assign (or reassign) a task to a member: claims it, runs it, marks Done/Failed.
               Reassigning an already-owned task moves it to the new member.
task_unassign  Clear a task's owner -> back to Pending/Blocked. Optional assignee to redirect,
               or "none" to drop the designation.
task_reopen    Revert a Done/Failed task to Pending and re-block its dependents (redo/correct work).
task_clear     Remove a task by id, or the ENTIRE board when no id (or "all") is given.
task_info      Full detail for one task: status, owner, assignee, deps (and which still pending),
               what it blocks, timestamps.
task_list      List every task with status, owner/assignee, and dependencies.
task_autorun   Toggle the background auto-runner. Args: enabled (bool), intervalSeconds (default 15).
team_peerwork  Toggle peer self-claiming (each member claims its own eligible tasks per pickupPolicy).
               Args: enabled (bool), intervalSeconds (default 15).
```

### Auto-runner (timer / trigger)

A daemon-like background loop for taskboard teams. While ON it scans the board on a timer and
automatically claims + runs every task that is eligible -- **unblocked, unowned, has a designated
`assignee`, and is past its `startInSeconds` timer** -- bounded to one in-flight task per member.
A whole dependency graph drains on its own: as each blocker completes, its dependents auto-unblock
and get picked up on the next scan. It honors the session cancellation (Esc / quit).

Start it at launch with `"autoRun": true` in the team config, or at runtime via the lead calling
`task_autorun(enabled: true)`. To schedule work, create tasks with an `assignee` and an optional
`startInSeconds` delay, then let the runner drain them. Turn it off with `task_autorun(enabled: false)`;
tasks then run only when the lead assigns them.

### Peer self-claiming (members pull their own work)

Beyond the single lead-keyed auto-runner, every member can run its **own** poll-claim-run loop and
pull work off the board on its own initiative (the Claude-Code "teammates claim their own tasks"
model). Each member's loop scans for the next task it may claim under the team's `pickupPolicy`
(`assigned` = only its own assigned tasks; `open` = also any unassigned, unblocked, ready task),
claims it atomically (file-locked, so two members racing an open task can never both win), runs it,
marks it Done/Failed (auto-unblocking dependents), and loops - one task in-flight per member.

Toggle it with the lead tool `team_peerwork(enabled, intervalSeconds)` or, interactively, with
`/kanban peer on|off`. Mark a task claimable from the board with `/kanban ready <id>` (or
`/kanban assign <id> <member>` to direct it to a specific member under the `assigned` policy).

### Persistent member context

With `memberContext: "persistent"` (the default) each member keeps a **warm session** so its context
accumulates across the tasks it picks up - it remembers what it did on previous tasks, like the main
agent's session does. To keep that bounded, a member's session is **auto-compacted** when it grows
past `compactionAgent.memberAutoCompactTokenThreshold`: its history is summarized and the session is
reseeded with the summary (the exact mechanism the single-agent loop uses for its own
`autoCompactTokenThreshold`), so a warm member can never grow without bound. Set
`memberContext: "fresh"` to opt a team back into one-shot members (clean session per task, no
carry-over). Per-member working state (activity, completed count, compaction count, token estimate)
persists under `<install>/Teams/{slug}/members/{member}.json` for resume + the Agent View.

### Mailbox (inter-agent messaging)

Every team has an inter-agent **mailbox** (unless `mailbox: false`). It lets the lead and members
exchange peer-to-peer messages while a team runs - asking questions, handing off work, broadcasting,
and gracefully shutting a member down.

**Tools** (on the lead AND every member; each bound to its own identity):
- `send_message(to, type, body)` - deliver a message to a teammate, the lead, or `"all"`/`"*"`
  (broadcast to every other member + lead). `type` is one of `info | question | answer | handoff |
  shutdown`. Only the lead may send `shutdown` (a member cannot stop a peer).
- `read_inbox()` - read and clear (drain) the new messages addressed to you.

**Delivery semantics:**
- **Persistent.** Each message is one JSON file in the recipient's inbox, so a team survives a
  restart with its conversation intact. An undelivered `shutdown` re-arms its stop flag on reload.
- **Live drain (members don't sit on mail).** A member drains its inbox at the **start of every
  task** - unread messages are prepended to the task brief, so it acts on them immediately rather
  than only when it independently calls `read_inbox`.
- **Idle wake.** When a member has no claimable board task but has an actionable unread message (a
  `question` or `handoff`), its self-claim loop runs a short turn to read and respond - so a peer's
  message is handled promptly instead of waiting for the next task. `info`/`answer` messages are FYI
  and do not by themselves wake an idle member (they are still delivered into the next task brief).
- **Graceful shutdown.** The lead sends a `shutdown`-type message to a member; that member stops its
  self-claim loop **between tasks** (never mid-call), so in-flight work finishes cleanly.

**On-disk layout** (per team, under `<install>/Teams/{slug}/`):

```
inboxes/
  {agent}/
    m1.json   # { "id":"m1", "from":"Lead", "to":"Alice",
    m2.json   #   "type":"question", "body":"status?",
    ...        #   "sent":"2026-06-26T...Z", "read":false }
```

The Agent View `m` key shows any agent's full message history (the m-log) on demand, without
draining its inbox.

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

### In-app editor & auth

The web UI includes a bundled **Monaco** code editor (offline, syntax highlighting
for ~90 languages, theme-aware). Right-click a sandbox file → **Open in editor**.
Saving requires `serve.editable: true` (see [serve](#serve)); otherwise the editor
is read-only.

Optional app-level auth is configured via `serve.auth` (see [serve](#serve)). The
web app prompts for the token, stores it in `sessionStorage`, and attaches it to all
API/WS calls. App auth complements (does not replace) an nginx perimeter.

## Subscription Auth (CLIProxy sidecar)

Besides plain API-key providers, Mux can authenticate with subscription accounts
(Claude Max/Pro, ChatGPT/Codex, Kimi, etc.) through a bundled **CLIProxyAPI** sidecar -
a small MIT loopback proxy that holds the OAuth tokens and exposes a normal
OpenAI-compatible endpoint Mux talks to unchanged.

- `/login [provider]` - run the provider's OAuth flow (browser opens automatically).
  Providers: `claude`, `codex`, `codex-device` (headless device-code), `kimi`, `xai`,
  `antigravity`. The first successful login auto-registers a single `cliproxy` provider
  (endpoint `http://127.0.0.1:<port>/v1`, bearer via the `MUX_CLIPROXY_KEY` env var);
  the proxy then routes by model id, so later logins add nothing.
- `/ping [provider]` - ensure the sidecar is up and report per-provider login readiness.
- `/proxy status` - pinned version, binary presence, running state + endpoint, auth state.
- `/proxy update` - re-download + verify the pinned proxy binary and restart it.

The sidecar is downloaded on first use into `%LOCALAPPDATA%/Mux-Swarm/cliproxy/<ver>/`
(SHA256-verified), runs detached so it survives Mux exit, and is re-adopted by port on the
next launch (no respawn). After login, activate the provider with `/provider`, set the
agent model to a real id (e.g. `claude-opus-4-6`, `gpt-5-codex`), and chat.

## ACP Transport (Zed Agent Client Protocol)

`mux-swarm --acp` exposes Mux as an ACP agent over stdio (JSON-RPC 2.0, newline-delimited;
stdout is pure protocol, logs go to stderr). This lets ACP-capable editors/clients (Zed,
GitHub Copilot CLI agent panel, the Intelligent Terminal) drive a Mux single-agent session.

- Supports: `initialize`, `session/new`, `session/load` (transcript replay -> context
  resume), `session/prompt` (text + resource_link + embedded text), `session/cancel`,
  `session/set_mode`, `session/resume`, model selection (both the canonical
  `configOptions` and the Zed `models` / `session/set_model` mechanisms), `logout`.
- Streams: `agent_message_chunk`, `agent_thought_chunk` (reasoning), `tool_call` +
  `tool_call_update` (with diffs + locations), `plan`, `usage_update`; stop reasons
  `end_turn` / `cancelled`.
- Filesystem + terminal execution and permission prompting deliberately stay inside Mux's
  own tool layer (not rerouted through the ACP client) - this is spec-compliant (those
  client capabilities are optional).

## Native Tools & Size-Tiered Delegation

**Native tools.** Filesystem, shell, and a persistent Python REPL are implemented natively
in-process (no external MCP server) and are **session-scoped**: each sub-agent run gets its
own REPL worker + shell-job table, so parallel sub-agents never clash on shared interpreter
state. Exposed as `repl_shell_exec` (+ `check_python_status`, `send_python_input`,
`list_variables`, `restart_python_worker`) and `execute_command_async` (+ `check_job_status`,
`send_command_input`, `install_package_async`). Filesystem/shell access is gated by the
`filesystem.securityMode` / `shell.securityMode` settings, and may run inside a sandbox (see
[Execution Sandbox](#execution-sandbox)).

**Size-tiered sub-agent context passing.** When a lead delegates work, the sub-agent's
result is returned to the lead at a cost scaled to need:

| Result size | Lead receives |
|---|---|
| small (<= `progressEntryBudget`) | full raw, inline |
| medium (<= `3x` budget) | summary + extracted references (`subAgentSummaryMode`) |
| large (> spill threshold) or lead near its cap | a short pointer (status + 3-line headline + a `d:Agent#N` handle); the raw is spilled to disk |

The lead pulls detail on demand from a spilled result with the **`read_delegation`** tool
(`handle` + optional `pattern` grep / `head` / `tail`). Spilled raw lives under
`<sandbox>/delegations/<scope>/` (or `%LOCALAPPDATA%/Mux-Swarm/delegations/` when the sandbox
is unwritable) and is pruned after `delegationRetentionDays`. All thresholds scale off the
existing `executionLimits` budgets; the only dedicated knob is `delegationRetentionDays`.

**Blocking vs non-blocking delegation.** `delegate_to_agent_lite` and `delegate_parallel`
block the lead until the children finish. Passing `background:true` to `delegate_parallel`
instead fires the sub-agents into the **background** and returns job ids immediately, so the
lead keeps working and collects results later by polling `check_delegations` (background jobs
also appear in the `\` Agent View and are managed by `/background`). The model chooses which
to use; non-blocking is never forced.

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

## Giga Mode

Toggle with `/giga` (a superset of `/ultra`). Giga keeps ultra's maximum-reasoning + plan discipline
and ADDS **dynamic orchestration**: the live single agent can build and drive its own team or
workflow mid-conversation, without you wiring anything. Toggle it off and the capability + tools are
removed and the prior reasoning/plan flags are restored (the off-giga single-agent path is
byte-identical).

While giga is on the agent gains these tools:

```
spawn_team(name, members, coordination, persist)
               Create an ephemeral team from existing agents. members = comma-separated agent
               names; coordination = "fanout" | "taskboard". persist=true also writes it to
               swarm.json teams[] so it survives a restart. Ephemeral teams are tagged "giga:".
run_team(name, assignments)
               Dispatch a batch of member tasks concurrently and collect results. assignments =
               JSON array of {"agent":"<member>","task":"<instruction>"}. Each member runs in its
               own session and appears live in the Agent View.
write_workflow(name, steps)
               Author a reusable workflow file. steps = JSON array of "AgentName: instruction"
               strings (or plain "instruction" to route to the Orchestrator). Saved as
               <name>.workflow.json under the Teams directory.
run_workflow(path)
               Execute a workflow file phase-by-phase, routing each step to its agent and feeding
               each step's result forward as context to the next.
list_workflows()
               List the saved workflow files you can run.
```

Giga-spawned teams reuse the same execution substrate as `/teams` (parallel workers + the Agent
View), so members are visible and focus-switchable exactly like a configured team's. The agent
remains the single voice in your conversation; the team works under it.

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