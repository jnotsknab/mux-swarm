# Configuration Reference

mux-swarm separates configuration into two files under `Configs/`:

- [**`config.json`**](#configjson-infrastructure) - Infrastructure and runtime environment: providers, MCP servers, filesystem boundaries, shell security, sandbox, telemetry, daemon, and the web serve surface.
- [**`swarm.json`**](#swarmjson-topology--roles) - Swarm topology and agent behavior: roles, model routing, model tuning, delegation permissions, tool scope, teams, hooks, and memory/reflection tuning.

This separation lets you swap providers without redesigning the swarm, or redesign the swarm without changing infrastructure wiring. Both files are created with sensible defaults on first run (`/setup`). Most blocks are optional: omit a block and the runtime uses its defaults.

---

## `config.json` (Infrastructure)

Defines which external integrations are available, where the runtime can read/write, and which provider endpoints to use. Supports multiple providers with runtime swapping via `/provider` or `--provider`.

### Top-level keys

| Key | Purpose |
|-----|---------|
| `setupCompleted` | Set by the `/setup` wizard on completion; gates first-time setup. |
| `isUsingDockerForExec` | Legacy Docker execution posture flag (superseded by the `sandbox` block). |
| `serveAddress` | Default bind address for `--serve` (web UI + API + WebSocket). |
| `mcpConnectTimeoutSeconds` | Per-server timeout for MCP connection at startup. |
| `showReasoning` | Whether model reasoning traces render in the console. |
| `startupArgs` | Persisted default CLI arguments applied at launch. |

### MCP Servers (`mcpServers`)

Registry of Model Context Protocol servers available to agents. Which agents see which servers is decided in `swarm.json` (`mcpServers` per agent).

```json
"mcpServers": {
  "Filesystem": {
    "type": "stdio",
    "command": "npx",
    "args": ["-y", "@modelcontextprotocol/server-filesystem"],
    "enabled": true
  },
  "Memory": {
    "type": "stdio",
    "command": "npx",
    "args": ["-y", "@modelcontextprotocol/server-memory"],
    "env": { "MEMORY_FILE_PATH": "/path/to/sandbox/memory.jsonl" },
    "enabled": true
  }
}
```

| Key | Purpose |
|-----|---------|
| `type` | Transport: `stdio` (child process) or `http`/`sse` (remote). |
| `command` / `args` | Executable and arguments for stdio servers. |
| `env` | Environment variables passed to the child process. |
| `url` | Endpoint for remote (HTTP/SSE) servers. |
| `headers` | Extra HTTP headers for remote servers (auth tokens etc.). |
| `enabled` | Toggle without deleting the entry. |

### LLM Providers (`llmProviders`)

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

| Key | Purpose |
|-----|---------|
| `name` | Provider identifier used by `/provider` and `--provider`. |
| `enabled` | Toggle without deleting the entry. |
| `apiKeyEnvVar` | Environment variable holding the API key (never store keys in the file). |
| `endpoint` | OpenAI-compatible base URL. |
| `headers` | Extra HTTP headers sent with every request. |
| `authType` | Auth scheme override where a provider deviates from bearer-key convention. |

The bundled CLIProxy sidecar (subscription OAuth for Claude/Codex plans) registers as a plain OpenAI-compatible entry pointing at the local sidecar port; manage it with `/login`, `/ping`, and `/proxy status|update|restart`.

### Filesystem (`filesystem`)

```json
"filesystem": {
  "allowedPaths": ["/path/to/project"],
  "sandboxPath": "/path/to/project",
  "chromaDbPath": "/path/to/project/chroma-db",
  "knowledgeGraphPath": "/path/to/project/memory.jsonl",
  "securityMode": "standard"
}
```

| Key | Purpose |
|-----|---------|
| `allowedPaths` | Directories agents may read/write. Everything else is denied. |
| `sandboxPath` | Primary scratch/deliverable directory for agents. |
| `skillsPath` | Root of the skills tree (defaults to the bundled `Skills/` dir). |
| `sessionsPath` | Where session transcripts persist. |
| `chromaDbPath` | On-disk path for the ChromaDB vector store. |
| `knowledgeGraphPath` | JSONL file backing the knowledge-graph memory server. |
| `promptsPath` | Root of the prompt files tree. |
| `configDir` | Override for the `Configs/` resolution directory. |
| `securityMode` | Enforcement level for native filesystem tools (see below). |

> **Enterprise storage:** `allowedPaths` works with any storage that presents as a filesystem path - Azure Blob Storage ([BlobFuse](https://github.com/Azure/azure-storage-fuse)), AWS S3 ([Mountpoint](https://github.com/awslabs/mountpoint-s3), [s3fs](https://github.com/s3fs-fuse/s3fs-fuse)), Google Cloud Storage ([GCS FUSE](https://cloud.google.com/storage/docs/cloud-storage-fuse/overview)), SMB/CIFS shares, and NFS mounts. Mount the storage, add the mount path to `allowedPaths`, and agents use it like any local directory.

### Filesystem & Shell Security (`filesystem.securityMode`, `shell`)

The native in-process Filesystem and Shell/REPL tools enforce configurable security postures independent of any MCP server.

```json
"filesystem": { "securityMode": "standard" },
"shell": { "securityMode": "off", "allowedCommands": [] }
```

| Key | Values | Description |
|-----|--------|-------------|
| `filesystem.securityMode` | `standard` (default), `secure`, `lax`, `none` | Enforcement level for native filesystem tools. `standard` honors `allowedPaths`; `secure` is strictest; `lax`/`none` relax checks. |
| `shell.securityMode` | `off` (default), `prompt`, `allowlist` | Gate on native Shell/REPL execution. `off` runs commands ungated (default, run-anything); `prompt` asks for confirmation on every command; `allowlist` runs commands whose first token is in `allowedCommands` and prompts for anything else. Non-interactive sessions auto-deny a prompt. |
| `shell.allowedCommands` | string[] | Commands permitted when `securityMode` is `allowlist`. |

### Execution Sandbox (`sandbox`)

Optionally run native shell + REPL execution inside a per-session sandbox. Hot-swap at runtime with `/sandbox` or select at launch with `--sandbox`.

```json
"sandbox": { "backend": "host", "image": "python:3.12-slim", "network": false, "allowedDomains": [] }
```

| Key | Default | Description |
|-----|---------|-------------|
| `backend` | `host` | `host` (no sandbox), `docker`, `podman`, `nerdctl`, `gvisor`, `kata` (microVM), `bwrap`/`firejail`/`sandbox-exec` (wrapper), or `custom`. |
| `image` | `python:3.12-slim` | Container image for OCI backends. |
| `network` | `false` | Allow network egress. With a non-empty `allowedDomains`, a deny-by-default CONNECT allowlist is enforced. |
| `allowedDomains` | `[]` | Domains the sandbox may reach (OCI backends only). |
| `command` | - | Launch command template for the `custom` backend. |
| `runtime` | `""` | Optional `--runtime` passthrough for OCI backends (e.g. `kata-runtime`). |

### User Info (`userInfo`)

Operator profile injected into agent context.

```json
"userInfo": {
  "name": "Micky",
  "role": "admin",
  "timezone": "America/New_York",
  "locale": "en-US",
  "info": "Prefers concise responses. Primary stack is .NET/C#."
}
```

### Context File Caps (`contextLimits`)

Optional hard char-caps on the long-lived `BRAIN.md` / `MEMORY.md` memory files, with an opt-in background prune.

```json
"contextLimits": { "memoryMdCharLimit": 0, "memoryMdCapMode": "off", "prunePulseSeconds": 0 }
```

| Key | Default | Description |
|-----|---------|-------------|
| `brainMdCharLimit` / `memoryMdCharLimit` | `0` | Char cap per file (`0` = uncapped). |
| `brainMdCapMode` / `memoryMdCapMode` | `off` | `off`, `warn` (warn when over cap), or `force` (LLM-rewrite under the cap, backing up first). |
| `prunePulseSeconds` | `0` | When `> 0` and a file is in `force` mode, a background pulse re-checks every N seconds (first tick +30s) and rewrites only when over cap. `0` disables. |

### Telemetry (`telemetry`)

Optional OpenTelemetry configuration for exporting traces, logs, and metrics. All agent sessions, turns, tool calls, delegations, and orchestrator iterations emit OTEL spans. Structured logs attach as span events. Token counters, turn durations, and compaction metrics export as OTEL metrics.

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

| Parameter | Default | Description |
|-----------|---------|-------------|
| `enabled` | `false` | Enable OTLP export. No overhead when disabled. |
| `endpoint` | - | OTLP receiver (Jaeger, OTEL Collector, Tempo, Datadog agent). |
| `protocol` | `grpc` | `grpc` or `http/protobuf`. |
| `serviceName` | `mux-swarm` | Service name in traces. Useful for multi-instance deployments. |
| `logLevel` | - | Minimum level for exported logs. |
| `verbosity` | `standard` | `minimal` (spans only), `standard` (spans + model tags), `verbose` (spans + full message content). |
| `headers` | - | Auth headers for hosted backends (e.g. `{"Authorization": "Basic ..."}`). |

Resource attributes are set automatically: `host.name`, `os.type`, `service.version`, `service.instance.id`. The instance ID includes the serve port when running with `--serve`.

**Quick start with Jaeger:**
```bash
docker run -d --name jaeger -p 4317:4317 -p 16686:16686 jaegertracing/jaeger:latest
```

Add the telemetry block to `config.json`, launch mux-swarm, run a session, then open `http://localhost:16686` to see the trace tree.

### Daemon (`daemon`)

Background trigger engine for unattended work. Scaffold triggers interactively with `/daemon` (natural-language cron supported) or `/createhook`. See [Hooks, Webhooks & the Daemon](hooks.md) for the full guide.

```json
"daemon": {
  "enabled": true,
  "triggers": [
    { "id": "nightly-report", "type": "cron", "schedule": "0 3 * * *", "goal": "Summarize yesterday's logs", "mode": "agent", "agent": "CodeAgent" },
    { "id": "gh-release", "type": "webhook", "goal": "React to the release payload", "secret": "<secret>", "payloadLimit": 65536 }
  ]
}
```

| Key | Purpose |
|-----|---------|
| `enabled` | Master switch for the daemon. |
| `triggers[].id` | Unique trigger identifier (also the inbound URL segment for webhook triggers). |
| `triggers[].type` | `cron`, `watch`, `status`, `interval`, or `webhook`. |
| `triggers[].schedule` | Cron expression (cron type). |
| `triggers[].path` | Watched file/directory (watch type). |
| `triggers[].interval` / `cooldown` | Polling cadence and re-fire suppression (interval/status types). |
| `triggers[].check` / `restart` / `failThreshold` | Health-check command, restart command, and failure tolerance (status type). |
| `triggers[].command` / `env` / `args` | External command execution instead of (or alongside) a goal. |
| `triggers[].goal` / `mode` / `agent` | Agent goal to run, execution mode (`agent`/`swarm`/pipeline variants), and target agent. |
| `triggers[].secret` | Webhook trigger only: HMAC-SHA256 secret (or bearer token) validating inbound `POST /api/hook/{id}` calls. |
| `triggers[].payloadLimit` | Webhook trigger only: max accepted request body size in bytes. |

A `webhook` trigger exposes `POST /api/hook/{id}` on the serve surface. It is excluded from the global serve bearer middleware; auth is enforced per-trigger (HMAC signature or bearer) in the handler.

### Serve (`serve`)

Gates for the web UI and HTTP API when running `--serve`. All gates default to the safe/off position.

```json
"serve": {
  "editable": false,
  "configExposed": false,
  "auth": { "enabled": false, "token": "", "scheme": "Bearer" },
  "editor": { "autoFetch": true, "version": "" }
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `editable` | `false` | Allow the in-browser Monaco editor to WRITE files (read is always available). |
| `configExposed` | `false` | Expose config file contents through the API/editor. |
| `auth.enabled` | `false` | Require a bearer token on `/api/*` and `/ws`. |
| `auth.token` | - | The shared token clients must present. |
| `auth.scheme` | `Bearer` | Authorization scheme name. |
| `editor.autoFetch` | `true` | Auto-fetch Monaco editor assets. |
| `editor.version` | - | Pin a specific Monaco version. |

### Ultra Mode (`ultra`)

| Key | Purpose |
|-----|---------|
| `thinkingBudget` | Reasoning-token budget applied in ultra mode. |
| `includeSubAgents` | Whether ultra settings propagate to sub-agents. |
| `autoSubAgents` | Automatically fan out to sub-agents in ultra mode. |

### Console (`console`)

TUI rendering preferences.

| Key | Purpose |
|-----|---------|
| `renderMode` | Rendering pipeline selection (live-region TUI vs classic scroll). |
| `theme` | Color theme. |
| `toolOutput` | How tool results render (full/collapsed/hidden). |
| `dockedFooter` | Keep the status footer docked at the bottom. |
| `collapseToolLines` | Collapse multi-line tool call output. |
| `delegationSpacing` | Vertical spacing around delegation cards. |
| `collapseSubAgents` | Collapse sub-agent activity into summary rows. |
| `collapseDaemon` | Collapse daemon lane output. |
| `collapseDelegations` | Collapse completed delegation cards. |
| `inputHighlight` | Syntax highlight for the input line. |
| `cardMarkdown` | Render markdown inside cards. |
| `bracketedPaste` | Enable bracketed-paste handling for multi-line input. |

---

## `swarm.json` (Topology & Roles)

Defines which agents exist, what they specialize in, which models and MCP servers each role can access, who can delegate, and optional per-agent model tuning via [`modelOpts`](#model-tuning-modelopts).

```json
{
  "executionLimits": {
    "progressEntryBudget": 1000,
    "crossAgentContextBudget": 2000,
    "progressLogTotalBudget": 4500,
    "maxOrchestratorIterations": 15,
    "maxSubAgentIterations": 8,
    "maxSubTaskRetries": 4,
    "maxStuckCount": 3,
    "compactionCharBudget": 6000,
    "subAgentSummaryMode": "auto"
  },
  "compactionAgent": {
    "model": "google/gemini-3-flash-preview",
    "autoCompactTokenThreshold": 80000,
    "modelOpts": { "temperature": 0.2, "topP": 0.85, "maxOutputTokens": 4096 }
  },
  "singleAgent": {
    "name": "MuxAgent",
    "promptPath": "Prompts/Agents/chat_prompt.md",
    "model": "google/gemini-3.1-pro-preview",
    "modelOpts": { "temperature": 0.6, "topP": 0.95, "maxOutputTokens": 16384 },
    "mcpServers": ["Filesystem", "Memory", "BraveSearchMCP"],
    "toolPatterns": []
  },
  "orchestrator": {
    "promptPath": "Prompts/Agents/orchestrator.md",
    "model": "google/gemini-3.1-pro-preview",
    "modelOpts": { "temperature": 0.3, "topP": 0.9, "maxOutputTokens": 4096 },
    "toolPatterns": ["Filesystem_list_directory", "Filesystem_read_file"]
  },
  "agents": [
    {
      "name": "WebAgent",
      "description": "Web browsing, research, and internet tasks.",
      "promptPath": "Prompts/Agents/web_agent.md",
      "model": "google/gemini-3.1-pro-preview",
      "mcpServers": ["BraveSearchMCP", "Fetch", "Filesystem"],
      "canDelegate": true
    },
    {
      "name": "CodeAgent",
      "description": "Code generation, editing, and debugging.",
      "promptPath": "Prompts/Agents/code_agent.md",
      "model": "google/gemini-3.1-pro-preview",
      "modelOpts": { "temperature": 0.4, "maxOutputTokens": 8192 },
      "mcpServers": ["Filesystem", "BraveSearchMCP", "ReplShellMCP"],
      "canDelegate": true
    }
  ]
}
```

### Agent Definitions (`singleAgent`, `agents[]`, `orchestrator`)

| Key | Purpose |
|-----|---------|
| `name` | Agent identity (used for delegation, mailbox routing, and display). |
| `description` | One-line specialty shown to delegating agents. |
| `promptPath` | Path to the role prompt file (behavioral contract). |
| `model` | Model id, `provider/model` routed through the active provider. |
| `modelOpts` | Per-agent sampling/tuning overrides (see [Model Tuning](#model-tuning-modelopts)). |
| `mcpServers` | Which registered MCP servers this agent can use. |
| `toolPatterns` | Restrict to specific tool names/patterns within those servers. |
| `skillPatterns` | Restrict which skills the agent can discover/load. |
| `canDelegate` | Whether the agent may re-delegate in swarm mode. |

The `orchestrator` block uses the same shape (no `name`/`canDelegate`) and drives swarm-mode planning and delegation.

### Execution Limits (`executionLimits`)

Optional tuning for orchestration budgets, iteration caps, retry behavior, and taskboard resilience. All fields are serialized with sensible defaults on first run. Raise limits for complex goals on capable models, lower for cost-sensitive deployments. Inspect active values at runtime with `/limits`.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `progressEntryBudget` | 1000 | Max chars per compacted agent result returned to the orchestrator. |
| `crossAgentContextBudget` | 2000 | Max chars of prior agent context injected into a new sub-agent's task. |
| `progressLogTotalBudget` | 4500 | Max total chars of progress history sent in orchestrator continuation prompts. Oldest entries trimmed first. |
| `maxOrchestratorIterations` | 15 | Planning/delegation cycles before the orchestrator gives up. Overridden to unlimited in continuous mode. |
| `maxSubAgentIterations` | 8 | Tool-call loops per sub-agent delegation before forced completion. |
| `maxSubTaskRetries` | 4 | Retry attempts per failed sub-task with progressive recovery hints. |
| `maxStuckCount` | 3 | Consecutive empty responses before aborting. |
| `compactionCharBudget` | 6000 | Target char budget for the LLM session-compaction summary (`/compact`, auto-compaction). |
| `contextInjection` | - | Controls how much cross-agent context is injected into delegations. |
| `compactionMaxMessageChars` | - | Per-message char ceiling fed to the compaction model. |
| `subAgentSummaryMode` | `auto` | How an over-budget sub-agent result is compacted before returning to the lead: `auto`/`llm` run the compaction model and append signal-scored extracted references; `extractive` skips the LLM entirely (no extra cost). |
| `delegationRetentionDays` | - | How long spilled delegation payloads (size-tiered context passing) are kept on disk. |
| `activityTimeoutSeconds` | - | Inactivity watchdog for long-running agent turns. |
| `maxToolIterationsPerTurn` | 1000 | Max model-to-tool round-trips within a single turn before the tool loop stops. `<= 0` = unlimited. |
| `maxAutoContinuesPerTurn` | 3 | Times a turn may transparently self-continue when a response is cut off by the output/reasoning cap (finish_reason=length). 0 disables. |
| `taskClaimTtlSeconds` | 900 | Team taskboard: how long a claimed task may go without a heartbeat before it is reaped and requeued. |
| `maxTaskAttempts` | 3 | Team taskboard: bounded retry cap per task before the circuit breaker marks it `Failed`. |

### Compaction Agent (`compactionAgent`)

| Key | Purpose |
|-----|---------|
| `model` / `modelOpts` | Model used for session compaction and sub-agent result summarization. |
| `autoCompactTokenThreshold` | Session token count that triggers automatic compaction for the lead. |
| `memberAutoCompactTokenThreshold` | Auto-compaction threshold for team member sessions. |

### Reflection Agent / Deep Memory (`reflectionAgent`, `memoryMode`)

Background deep-memory system: a gatherer distills session activity into reflections (`Context/reflections.json`), and an injector surfaces the highest-scoring reflections back into agent context. Toggle at runtime with `/memory` or `/deep` (persists to `swarm.json`).

```json
"memoryMode": "deep",
"reflectionAgent": {
  "mode": "deep",
  "model": "google/gemini-3-flash-preview",
  "injectTokenBudget": 1200,
  "pollIntervalSeconds": 120,
  "relevanceFloor": 0.2,
  "scope": "lead"
}
```

| Key | Purpose |
|-----|---------|
| `mode` | `standard` (off) or `deep` (gatherer + injector active). |
| `memoryMode` (top-level) | Convenience alias: overrides `reflectionAgent.mode` at load. |
| `model` / `modelOpts` | Model used by the background gatherer. |
| `injectTokenBudget` | Hard token cap on the TOTAL injected reflection block. |
| `pollIntervalSeconds` | Gatherer tick interval; activity-gated (no LLM call when idle). |
| `relevanceFloor` | Minimum relevance score for a reflection to be injected. |
| `scope` | `lead` (lead agent only) or `all` (sub-agents too). |
| `maxReflections` | Store prune ceiling (oldest beyond this are dropped). |
| `injectQueryTimeoutMs` | Timeout for the semantic (Chroma) relevance query at inject time. |
| `historyWindow` | How much recent transcript the gatherer distills per tick. |
| `maxDigsPerTick` | Cap on Pass-2 investigator (DIG) runs per gatherer tick. |
| `digMaxFilesScanned` / `digMaxMatches` / `digMaxReadChars` | Read-only investigator resource caps. |

### Task Decomposition (`decompose`)

Opt-in background dispatcher that expands a goal into a dependency graph of taskboard entries (`/taskgraph`, `/decompose on|off`).

| Key | Purpose |
|-----|---------|
| `enabled` | Off by default. |
| `model` | Light model used for the single decomposition call. |
| `pollIntervalSeconds` | Dispatcher tick interval. |
| `maxSubtasks` | Cap on generated subtasks per decomposition. |

### Vision Agent (`visionAgent`)

| Key | Purpose |
|-----|---------|
| `model` / `modelOpts` | Model used by the `analyze_image` tool for image analysis. |

### Teams (`teams[]`)

Team definitions for coordinated multi-agent execution (taskboard, mailbox, auto-run).

| Key | Purpose |
|-----|---------|
| `name` / `description` | Team identity. |
| `lead` | Lead agent name. |
| `members` | Member agent names. |
| `coordination` | Coordination style for the team. |
| `maxParallel` | Max members working concurrently. |
| `agentView` | What members see of each other's activity. |
| `autoRun` / `autoRunIntervalSeconds` | Background auto-run loop and its cadence. |
| `memberContext` | How much shared context members receive. |
| `pickupPolicy` | How members claim taskboard tasks. |
| `mailbox` | Enable inter-member mailbox messaging. |

### Hooks (`hooks[]`)

Lifecycle hooks: run an external command when a runtime event fires. Scaffold with `/createhook`; manage with `/hooks`. Full guide: [Hooks, Webhooks & the Daemon](hooks.md).

| Key | Purpose |
|-----|---------|
| `id` | Unique hook identifier. |
| `mode` | Execution mode for the hook command. |
| `persistent` | Keep the process alive across events vs spawn-per-event. |
| `command` | The external command to run. |
| `when` | Lifecycle event(s) that fire the hook. |
| `timeoutSeconds` | Kill the hook command after this long. |

### Outbound Webhooks (`webhooks[]`)

POST a signed JSON envelope to external URLs whenever matched stream events occur. Fire-and-forget with bounded retry; the subsystem is inert when no entries exist.

```json
"webhooks": [
  { "url": "https://example.com/mux", "events": ["turn_complete", "task_complete"], "secret": "<secret>", "headers": {} }
]
```

| Key | Purpose |
|-----|---------|
| `url` | Destination endpoint. |
| `events` | Event names to forward; `"*"` matches all events; an empty list disables the sink. |
| `secret` | When set, requests carry a GitHub-style HMAC-SHA256 `X-Hub-Signature-256` header. |
| `headers` | Extra static headers per request. |

Inbound webhooks are configured on the `config.json` side as daemon triggers of `type: "webhook"` (see [Daemon](#daemon-daemon)).

### Model Tuning (`modelOpts`)

Any agent, orchestrator, singleAgent, compactionAgent, reflectionAgent, or visionAgent supports an optional `modelOpts` block for per-agent model parameter tuning. All fields are optional - omitted values use provider defaults.

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

| Parameter | Type | Range | Description |
|-----------|------|-------|-------------|
| `temperature` | float | 0.0-2.0 | Controls output randomness. Lower = more deterministic, higher = more creative. |
| `topP` | float | 0.0-1.0 | Nucleus sampling - considers tokens within this cumulative probability mass. |
| `topK` | int | 1+ | Considers only the top K most probable tokens. Not all providers support this. |
| `maxOutputTokens` | int | 1+ | Hard ceiling on response length. |
| `frequencyPenalty` | float | -2.0-2.0 | Penalizes tokens proportionally to how often they appear. Reduces repetition. |
| `presencePenalty` | float | -2.0-2.0 | Penalizes any token that has appeared at all. Encourages topic diversity. |
| `seed` | long | - | Attempts deterministic output for identical inputs. Provider support varies. |

### Reasoning Options (`reasoning`)

Any agent, orchestrator, singleAgent, or compactionAgent supports an optional `reasoning` block for controlling model reasoning behavior. Both fields are optional.

```json
"reasoning": { "effort": "high", "output": "full" }
```

| Parameter | Values | Description |
|-----------|--------|-------------|
| `effort` | `none`, `low`, `medium`, `high`, `extra_high` | Controls how much computational effort the model puts into reasoning before responding. |
| `output` | `none`, `summary`, `full` | Controls whether reasoning traces are included in the response. |

**Tuning guidelines:**
- **Orchestrators:** Low temperature (0.2-0.4), medium reasoning effort, no reasoning output. Consistent planning without trace bloat.
- **Code based agents:** Moderate temperature (0.3-0.5), high reasoning effort, full output. Complex tasks benefit from deep visible reasoning.
- **Research/general agents:** Higher temperature (0.5-0.7), medium reasoning effort. Varied responses with moderate thinking.
- **Memory/utility agents:** Low temperature, low or no reasoning. CRUD operations where thinking adds latency without value.
- **Compaction agents:** Low temperature (0.1-0.3), no reasoning. Faithful summarization, not problem-solving.

### Provider-Specific Parameters (`additionalParams`)

For parameters not covered by the standard `modelOpts` fields or the `reasoning` block, use `additionalParams` to pass arbitrary key-value pairs directly to the provider via `ChatOptions.AdditionalProperties`. This is a pass-through - the runtime does not validate these values.

```json
"modelOpts": {
  "temperature": 0.3,
  "maxOutputTokens": 4096,
  "additionalParams": { "top_a": 0.08 }
}
```

Use this for provider-specific features not covered by the standard fields (e.g. `top_a`, `min_p`, `repetition_penalty`).

---

## Prompts: `Prompts/Agents/*.md`

Prompt files define the **behavioral contract** for each role - how an agent reasons, what it owns, which workflows it follows, and what constraints it respects. This is the main place to tune agent behavior without changing the runtime. Prompt files are appended to the runtime-built preamble; keep them role-focused and lean.

## Skills: `Skills/*`

Skills are reusable operational modules agents discover and load at runtime via `list_skills` and `read_skill`. They keep core prompts lean while giving agents access to structured instructions when needed. Prompts define the **role**; skills provide the **task-specific playbooks**.

**Installing skills** - `/installskill` pulls [Agent Skills](https://agentskills.io) (the `SKILL.md`-per-directory format) from public GitHub sources and normalizes them to mux conventions:

- `/installskill` (bare) - list installable skills across the curated registry (Anthropic, obra/superpowers, dotnet, Vercel Labs, tech-leads-club, ComposioHQ, OpenAI).
- `/installskill <name>` - install by name from the curated sources (first trusted match wins).
- `/installskill <owner>/<repo>` - install from a repo's skills (or list if it has several).
- `/installskill <owner>/<repo>/<path/to/skill>` or a full `https://github.com/.../tree/<branch>/<path>` URL - install a specific skill.
- Add `overwrite` to replace an existing install.

The installer fetches the whole skill directory (including `scripts/`, `references/`, `assets/`), then non-destructively stamps mux provenance into the `SKILL.md` frontmatter `metadata`. **Skills are untrusted third-party content loaded into the agent's context and may ship scripts - `/installskill` shows a prompt-injection/supply-chain warning and asks you to confirm before installing. Audit the installed `SKILL.md` + any scripts before relying on a skill.**

---
[Back to docs index](README.md) | [Main README](../README.md)
