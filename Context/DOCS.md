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
  "configExposed": false,
  "fileEvents": true,
  "fileEventsIgnore": ["*.log", "*.lock"],
  "browser": { "enabled": true, "engine": "auto", "playwright": true, "headless": true, "homePage": "https://duckduckgo.com", "profileDir": "" },
  "auth": { "enabled": false, "token": "", "scheme": "bearer" },
  "editor": { "autoFetch": true, "version": "" }
}
```

| Key | Default | Meaning |
|-----|---------|---------|
| `configExposed` | `false` | When true, the native config-editor endpoints (`GET`/`PUT /api/config-files/{config\|swarm}`) are enabled, letting the web app read and edit `Config.json` / `Swarm.json` in the browser (Monaco). Server-side validates JSON before writing (a parse error returns 422, file untouched). Independent of `editable`. |
| `editable` | `false` | When true, IDE write endpoints (`POST /api/save`, `POST /api/fs`) are enabled for the **sandbox** root only. Sessions stay read-only. When false they return `403`. |
| `fileEvents` | `true` | When true, files created, modified, renamed or deleted while a tool call runs (any tool: file tools, REPL, shell, MCP servers) are reported as `file_changed` events `{path, change, tool}` on the serve/stdio stream. They never reach the model. Watched: workspace, sandbox, `filesystem.allowedPaths` (skips `.git`, `bin`, `obj`, `node_modules`, `.venv`, temp files, `fileEventsIgnore` matches, and Mux's own state: `Telemetry`, `repl`, `delegations`, `Runtime/browser`, `Runtime/whisper`, `cliproxy`, `Backups`, the browser profiles, the vector store and the knowledge-graph file; max 50 per tool call). The web UI's Follow agent mode uses them. False disables the watchers and the event. |
| `fileEventsIgnore` | `["*.log", "*.lock"]` | Glob patterns for changes that are never reported (so Follow skips them). No `/` = match the file name (`*.log`); with `/` = match the end of the path (`docs/generated/**`); `*` and `?` stay in one folder, `**` crosses folders; case-insensitive on Windows/macOS. Setting the key replaces the defaults, so `[]` follows logs too. Build/VCS folders, editor temp files and Mux's own state are always skipped. |
| `browser.enabled` | `true` | Built-in browser in the preview panel (globe button): tabs, address bar, back/forward/reload, live view with mouse/keyboard input. Uses an installed Chromium browser by default (`engine`); otherwise a pinned Chrome for Testing build (~200 MB, SHA-256 checked) is downloaded from Google on first use into `Runtime/browser/<version>`; Mux does not ship it. Runs with a loopback-only DevTools port; the panel talks to it through `/api/browser` + `/api/browser/ws` (behind `serve.auth`). |
| `browser.engine` | `"auto"` | Which browser runs. `auto` = the first installed Chrome, Edge, Brave or Chromium, falling back to managed (no download when one is installed; an installed browser measured ~40% less memory than the managed build). `managed` = the pinned Chrome for Testing build above, downloaded on first use. `chrome` / `edge` / `brave` / `chromium` = that installed browser; starting fails with a clear message if it is missing. Installed browsers run as a separate headless instance with their own profile (`BrowserProfiles/<engine>` in the install folder), never your everyday profile. Firefox and Safari are not supported (no DevTools screencast). |
| `browser.playwright` | `true` | The built-in `Playwright` MCP server attaches to this browser (`--cdp-endpoint`; launch-only flags such as `--browser`/`--executable-path`/`--user-data-dir`/`--headless` are dropped), so you can watch and take over the agent's tabs. Only while `browser.enabled`. To have the agent use a real installed browser, set `browser.engine` instead of pinning one in the Playwright entry. |
| `browser.headless` | `true` | False also opens a real browser window (desktop sessions), still mirrored in the panel. |
| *(identity)* | automatic | The browser presents the User-Agent and client hints of a Chromium browser installed on this machine (the one `browser.engine` runs, else the first found), else of the managed version, instead of `HeadlessChrome`. `GET /api/browser` reports the identity in use. |
| `browser.homePage` | `"https://duckduckgo.com"` | Address new tabs (and the Home button) open. A bare host such as `example.com` becomes https; empty = blank page. |
| `browser.profileDir` | `""` | Profile directory (cookies, logins). Empty = `BrowserProfiles/<engine>` in the install folder (`managed` for the downloaded build). Keep profiles out of `Sessions`. |
| `auth.enabled` | `false` | Master switch for app-level bearer auth. When false, behaves exactly as before (open). |
| `auth.token` | `""` | Literal token, or an env-var reference: `{VAR}`, `${VAR}`, or `$VAR` (resolved at startup). |
| `auth.scheme` | `"bearer"` | Auth scheme. |
| `editor.autoFetch` | `true` | When true, the Monaco editor assets are fetched on first web-UI load. |
| `editor.version` | `""` | Pin a specific Monaco editor version (empty = bundled default). |

When `auth.enabled` is true, **all** `/api/*` requests and the `/ws` upgrade require
the token (HTTP `Authorization: Bearer <token>`; WS `?token=<token>` query param or
`Sec-WebSocket-Protocol: bearer,<token>`). Static assets stay open so the web app
can load its login prompt. Token compare is constant-time, never logged, and never
returned by `/api/config` (which exposes only a boolean `authRequired`). If enabled
but no token resolves, auth stays inactive with a warning (never silently locks out).

On connect, `/ws` replays recent output (up to 500 lines) so a reopened web UI catches up. `/ws?replay=0` skips the
replay (used by messaging bridges, which would otherwise resend old output after a reconnect).

## Self-Update & Lifecycle

Mux-Swarm can update itself in place from the latest GitHub release, and restart/shut down over the
HTTP API.

**Update** - `/update` (in-session or at the menu) or the `--update` CLI flag. The updater:
1. Queries the latest GitHub release and compares its tag against the running version (semver core).
2. Downloads the platform asset (`mux-swarm-win-x64.zip`, `-linux-x64.tar.gz`, `-osx-{arm64,x64}.tar.gz`).
3. **Verifies the download against the SHA256 digest GitHub publishes on the asset** - a mismatch aborts.
4. Extracts and replaces only *shipped* files whose content hash differs. **User-owned paths are never
   touched**: `Configs/Config.json`, `Configs/Swarm.json`, `Sessions/`, `Teams/`, and the durable
   `Context/` memory files (`reflections.json`, `MEMORY.md`, `BRAIN.md`).
5. The running binary can't be overwritten while locked, so a changed exe is staged as `MuxSwarm.new`
   and swapped on restart (rename current -> `.old`, staged -> live). When the binary changed, Mux
   relaunches automatically to finish.

Over HTTP: `GET /api/update` (read-only availability check) and `POST /api/update` (download + apply;
restarts if the binary changed). Both inherit the serve-layer bearer auth when `serve.auth` is on.

**Restart / shutdown** - `POST /api/restart` spawns a successor process that waits for the current one
to fully exit (via an internal `--relaunch-after <pid>` handshake) before rebinding the serve port,
then exits - no port-bind race. `POST /api/shutdown` tears down and exits. The web app's restart
control uses `/api/restart` (replacing an older WS-timing dance).

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
| `backend` | `host` (default, no sandbox) \| `docker` (Docker Sandboxes microVM) \| `docker-container` (alias `docker-legacy`) \| `podman` \| `nerdctl` \| `gvisor` \| `kata` \| `bwrap` \| `firejail` \| `sandbox-exec` \| `custom`. |
| `image` | Image for the `docker` microVM and container backends. Ignored by wrapper/host. Default `python:3.12-slim`. |
| `network` | When `allowedDomains` is empty: `true` = open egress, `false` = air-gapped. Ignored when an allowlist is set. |
| `allowedDomains` | Non-empty => the sandbox reaches ONLY these hosts via an injected CONNECT-filtering proxy on an internal (egress-less) network. **Container backends only** (`docker-container`/`podman`/`nerdctl`/`gvisor`/`kata`); rejected on `docker`, see below. Deny-by-default. The proxy runs from `python:3.12-alpine` (pulled on first use), so any sandbox `image` works. On `podman` this needs podman >= 4 (netavark: DNS on internal networks); podman 3.x is rejected with a clear error. |
| `command` | Template for the `custom` backend. Placeholders `{cmd}` `{workdir}` `{image}`. Required when `backend: custom`. |
| `replStdio` | `custom` only (default `false`): `true` declares the template runs `{cmd}` with stdin/stdout passed through, so the Python REPL worker runs through it. `false` => the Python REPL tools refuse under `custom` (never a host fallback); shell tools still use the template. |
| `runtime` | Explicit OCI runtime passed as `--runtime=<value>` for container backends. Empty => engine default, except `gvisor`=>`runsc` and `kata`=>`kata-runtime` which imply their runtime. Lets you layer a microVM runtime onto a base engine (e.g. `backend: podman`, `runtime: kata-runtime`). Ignored by `docker` (microVM) and wrapper/custom/host. |

### Isolation tiers (weakest -> strongest)

| Backend | Mechanism | Isolation |
|---|---|---|
| `host` | none | no isolation (runs natively) |
| `bwrap` / `firejail` | Linux namespaces + seccomp | OS-native (Linux only). `firejail` is refused when nested (WSL/containers) or when its check can't run |
| `sandbox-exec` | macOS Seatbelt (SBPL) | OS-native (macOS only) |
| `docker-container` / `podman` / `nerdctl` | OCI container (namespaces/cgroups) | container |
| `gvisor` | docker + `--runtime=runsc` | user-space kernel (syscall interposition) |
| `docker` | Docker Sandboxes (`sbx`) | **microVM** -- own guest kernel and Docker engine; Windows, macOS, Linux |
| `kata` | docker/podman + `--runtime=kata-runtime` | **microVM** -- a real guest kernel via hardware virtualization (Linux + KVM) |
| `custom` | user template | whatever the template points at |

Container backends run a persistent per-session container (`sleep infinity`) that the session's shell jobs and
Python worker `exec` into. `gvisor` and `kata` reuse that exact lifecycle -- they only change the OCI
runtime, so all of the OCI features (network allowlist proxy, allowed-path bind mounts mapped to
`filesystem.securityMode`, OCI hardening `--cap-drop=ALL --security-opt=no-new-privileges`, self-heal,
Windows UNC drive-mapping) apply unchanged.

Wrapper backends (`bwrap`, `firejail`, `sandbox-exec`) have no persistent instance: each shell command AND
the persistent Python REPL worker are started as wrapped processes, so both get the same confinement (the
worker's stdin/stdout protocol passes through the wrapper). The venv's base interpreter is exposed
read-only when the wrapper would otherwise hide it (firejail home whitelist, Seatbelt deny-default).
`install_package_async` runs as a wrapped shell job, so `network: false` also blocks package downloads.

### Docker Sandboxes microVM (docker)

Since v0.15.1, `docker` means a [Docker Sandboxes](https://docs.docker.com/ai/sandboxes/) microVM, driven
through the `sbx` CLI: each session gets its own VM with its own kernel and Docker engine. Shell jobs and
the Python worker `sbx exec` into it, the same model as the container backends. The plain container path is
`docker-container` (alias `docker-legacy`), unchanged from earlier releases.

- **Requirements:** the `sbx` CLI, signed in (`sbx login`), and a global policy (`sbx policy init balanced`
  on first use). Windows 11 needs Windows Hypervisor Platform; macOS needs Apple silicon; Linux needs
  Ubuntu 24.04+ with KVM. Local sandboxes are free. Docker Desktop is not required. A missing or signed-out
  `sbx` is a hard, legible error; it never falls back to a container or the host.
- **Layout:** the session work dir is `/work` (always rw) and allowed paths are `/host/<leaf>`, as with
  containers. Mount mode follows `filesystem.securityMode`: `standard` = first allowed path rw, the rest
  ro; `lax`/`none` = all rw; `secure` = all ro. Mounts are fixed when the session's sandbox starts, so
  restart after changing the mode. Nested allowed paths keep their own mode (a read-write
  workspace under a read-only reference path, e.g. your home dir, stays writable). Network-share (UNC)
  and missing allowed paths are skipped with a warning; use `docker-container` if you need shares.
  `Filesystem_list_allowed_directories` lists each host path with its sandbox path and mode.
- **Network:** `network: false` denies all egress. `network: true` allows everything your global sbx
  policy allows (the per-sandbox rule is allow-all, but global deny rules still apply). `allowedDomains` is
  **rejected**: sbx deny rules outrank allow rules, so a strict allowlist cannot be expressed. Use
  `docker-container` for a strict allowlist, or manage hosts with `sbx policy`.
- **Lifecycle:** created on first tool use (`mux-<session>-<id>`), removed when the session ends. Leftovers
  from a crash show in `sbx ls` and can be removed with `sbx rm --force <name>`.
- **Upgrading from 0.15.0:** configs with `backend: docker` (and `/dockerexec`) now get the microVM. Set
  `backend: docker-container` to keep the old container.

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

### gVisor on podman, and snap Docker

- **podman + gVisor:** the `gvisor` backend is docker only. For podman use `backend: podman` with
  `runtime: /usr/local/bin/runsc` (absolute path; podman may not resolve runsc from `PATH`). Install runsc
  from https://gvisor.dev/docs/user_guide/install/ (releases ship as an archive; keep `gvisor-bin/` next to
  `runsc`). Rootless podman 3.x may need a wrapper script as the `runtime` that adds `--ignore-cgroups`.
- **snap Docker:** snap-packaged Docker on Ubuntu rejects `--security-opt=no-new-privileges` (snap
  confinement), so every container backend fails to start. Use Docker from Docker's apt repository, or podman.
- `/doctor` reports whether the configured backend is usable and why not (read-only probes; never starts a
  sandbox).

**Deliberately NOT supported (deferred):** `libkrun`/`krun` and direct Firecracker/Cloud-Hypervisor/E2B
control planes. The sandbox lifecycle is built on container `exec`; `krun` microVMs do not service
`exec` into the guest (exec runs on the host kernel), and the VM-direct platforms have no `exec`
semantics at all -- both would need a separate vsock/SSH exec channel and control plane. Kata is the
microVM path that fits the existing model.

## Daemon Triggers

There are five trigger types: `watch`, `cron`, `status`, `bridge`, and `webhook`.

### Common Trigger Fields

Every trigger shares these base fields:

| Field | Type | Default | Description |
|---|---|---|---|
| `id` | string | `""` | Unique identifier for this trigger |
| `type` | string | `""` | One of: `watch`, `cron`, `status`, `bridge`, `webhook` |
| `mode` | string | `"agent"` | Orchestration mode for goal execution: `agent`, `swarm`, or `pswarm` |
| `agent` | string? | null | Optional agent name override for single-agent mode goals |
| `interval` | uint | 30 (webhook: 0) | Seconds between polls (status), restart delay (bridge), debounce (watch), or min gap between accepted deliveries (webhook; inside it the POST gets 429 + Retry-After) |
| `cooldown` | uint | 0 | Alias for interval on watch triggers. If set (>0), takes precedence over interval |
| `restart` | bool | false | Whether to restart on failure (status) or on exit (bridge) |
| `goal` | string? | null | Goal text for watch/cron/webhook triggers. Template vars substituted in one pass (`{payload}` `{source}` `{deliveryId}` `{file}` `{filename}` `{timestamp}` `{id}`) |
| `failThreshold` | int | 3 | Consecutive status check failures before alerting (0 = alert every failure) |
| `secret` | string? | null | (webhook) HMAC-SHA256 shared secret. When set, `POST /api/hook/{id}` requires a valid `X-Hub-Signature-256` header |
| `payloadLimit` | int | 65536 | (webhook) Max body characters forwarded into the goal; longer bodies end with a `[payload truncated: N of M chars]` marker |
| `callbackUrl` | string? | null | (webhook) Receives `{id, deliveryId, status, result, error}` when the run finishes, signed with `secret` |

Effective interval logic: if `cooldown > 0`, use cooldown; otherwise use `interval`.

A `webhook` trigger is exposed at `POST /api/hook/{id}` (excluded from the serve bearer middleware; it
authenticates per-trigger via HMAC `secret` or, if none is set, the serve bearer token). External systems
POST a payload to start a run; the 202 body carries a `deliveryId`. Webhook runs are stateless (no session dir) and
a run's `agent` override applies to that run only (an unknown agent fails the delivery). Webhook triggers added at
runtime (web UI or `POST /api/daemon/trigger`) are live immediately; deleting one stops it. With `--serve --daemon`,
the HTTP server starts before the daemon finishes registering triggers: poll `GET /api/health` until
`daemon` is `ready` (`off` = no daemon, `starting` = triggers not registered yet) before the first webhook POST. `/createhook` saves a
trigger to config; it takes effect on the next daemon start. One process serves any number of webhooks: agent-mode
triggers run concurrently, daemon-fired swarm/pswarm runs are serialized with each other (an interactive `/swarm` at
the same time is not covered), and deliveries to the same
trigger run in order. A delivery cancelled mid-run reports `error: "cancelled"`; one still queued when the trigger
stops reports `error: "shutdown"`. Watch `path` is a directory plus a filename pattern (`dir/*`, `dir/*.md`); `/`
works on every OS. See the Event Hooks section for inbound/outbound webhook detail.

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

Behavior: starts the process with `UseShellExecute=false`, redirects stdout/stderr to daemon log with `[Bridge:id:OUT]` and `[Bridge:id:ERR]` prefixes. Working directory is set to the mux-swarm base directory. The runtime auto-injects `MUX_WS_URL` (the address serve bound, e.g. `ws://127.0.0.1:6723/ws`; loopback when bound to `0.0.0.0`) and, when `serve.auth` is on, `MUX_SERVE_TOKEN` into the process environment (a trigger's own `env` wins). Bridges should connect to `/ws?replay=0` so a reconnect does not resend old output. If the process exits and `restart` is true, waits `interval` seconds then restarts. On daemon shutdown, all bridge processes are killed with `entireProcessTree: true`.

IMPORTANT: The `type` field MUST be `bridge`. Not `discord_bridge`, not `telegram_bridge`, just `bridge`. The bridge type is generic; what makes it a Telegram or Discord bridge is the `command` and `args` pointing to the appropriate Python script.

Bot tokens and secrets should be set as environment variables in the operator's shell, not in config.

## Bridge Setup

Three bridges ship under `Runtime/`. Each runs with `uv run --project Runtime Runtime/<name>_bridge.py`. Voice
transcription (Whisper) is an optional extra: add `--extra voice` before the script path to install it. Bridge tests:
`uv run --project Runtime --extra test pytest Runtime/tests`.

### Telegram

Required env var: `TELEGRAM_BOT_TOKEN` (create a bot with @BotFather).
Optional: `ALLOWED_CHAT_IDS` (comma-separated), `MUX_BRIDGE_ALLOW_ALL=1` (let any chat in; not recommended),
`MUX_BRIDGE_STATE` (paired-chats file), `WHISPER_MODEL` (with `--extra voice`).

```json
{
  "id": "telegram-bridge",
  "type": "bridge",
  "command": "uv",
  "args": "run --project Runtime Runtime/telegram_bridge.py",
  "restart": true,
  "interval": 10
}
```

- **Access is closed by default.** Unknown chats get a "this bot is private" reply. To add your phone, find the
  one-time pairing code in the bridge log (`[Bridge:telegram-bridge:ERR] ... Pairing code: XXXXXX`) and send
  `/pair XXXXXX` to the bot. Paired chats are remembered across restarts.
- **Questions come as buttons:** `ask_user`, approvals and confirms show as Telegram buttons ("Type my own" and
  "Cancel" when the prompt allows them); multi-select takes numbers (`1,3`); text prompts take your next message.
  Secret prompts (API keys) are refused over the bridge.
- **Clean rendering:** answers are converted to Telegram HTML (bold, italics, code, links); tables and code blocks
  become monospace blocks; long answers split without breaking a code block. Thinking text is not sent; sub-agent
  output is labelled with the agent's name.
- Commands: `/stop` interrupts the running agent, `/cancel` dismisses a pending question, `/pair <code>`.

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
  "maxAutoContinuesPerTurn": 3,
  "taskClaimTtlSeconds": 900,
  "maxTaskAttempts": 3,
  "midTurnCompaction": true,
  "autoAllowWorkspace": true
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
- `activityTimeoutSeconds` - deadman's-switch window for a turn: reset on every streamed update (text, tool call or tool result), and paused entirely while `ask_user` waits on you (a slow answer never times a turn out). A single tool call that blocks longer than the window with no updates still times out. Also the OpenAI client HTTP NetworkTimeout. NOT an idle-between-turns timeout. Default 3600 (1h). When it fires, the turn ends with a "timed out" warning and the session records your message plus a `[turn ended: ...]` note listing the tools that ran; any other mid-turn failure is recorded the same way (previously such a turn was dropped from the saved session).
- `maxToolIterationsPerTurn` - max model->tool round-trips per turn before the invocation middleware stops looping (<= 0 = unlimited).
- `maxAutoContinuesPerTurn` - how many times a turn may transparently continue itself after `finish_reason == length` (output/reasoning cap hit mid-generation). 0 disables.
- `taskClaimTtlSeconds` - team TaskBoard claim time-to-live: a claimed task with no heartbeat past this window is reclaimed by the stale-reaper. Default 900.
- `maxTaskAttempts` - bounded retry ceiling for a team task before the circuit-breaker marks it Failed. Default 3.
- `midTurnCompaction` - compact mid-turn when the context crosses the threshold: the tools the model just requested run, then the turn is compacted (its tool calls appear in the tool manifest; their full output is in the pre-compaction snapshot) and continues. A crossing on the final answer is not cut; the next turn compacts first. Needs a compaction model. Default true; false = compact only between turns.
- Every compaction (auto, mid-turn, or `/compact`) first saves the full pre-compaction session to `<sandboxPath>/prune-recovery/pre-compact-<session>-<utc>-<id>.muxprune` (indented JSON, same writer as `/prune` snapshots, exact restore copy) plus a same-named `.txt` (every tool call as `name | callId | args`, files referenced, then the transcript with real newlines). After the model's summary the engine appends a deterministic tool manifest (calls per tool, up to 30 files, the last 40 calls with args cut to 120 chars) and a note giving the snapshot paths and the `pre-compact-<session>-*` pattern that finds every earlier compaction of the session; the agent is told to search the `.txt` rather than read it whole. The compaction prompt keeps "user decided" separate from "agent proposed, not confirmed", skips context that is re-injected every turn, and treats the transcript (fenced in `<transcript>`) as data, never a request; a reply that is not a summary is retried once, then replaced by a marked extractive summary. Engine-generated blocks from a previous compaction (tool manifest, snapshot note, and the extracted references when a model summary exists) are stripped before re-compacting. Mid-turn compaction also records the in-progress turn's goal and tools (not yet committed to the session) in the summary and the `.txt`. Team-member compaction shares the prompt but appends no manifest, so its summary keeps a short tool record. No sandbox configured = compaction proceeds without a snapshot (warning).
- `autoAllowWorkspace` - add the launch/`--workspace` directory to `filesystem.allowedPaths` automatically. Default true; false = declare every allowed path explicitly.


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
  Members get their own knob so they can run tighter than the lead. `0` (default) falls back to
  200,000 tokens (never unbounded). Checked after each task, not mid-task.

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

Effort: `none`, `low`, `med`/`medium`, `high`, `xhigh`/`extra_high`, `max`, or `custom <raw-value>`.
`xhigh` sends the extra-high tier; `max` sends the distinct literal `max` value using the OpenAI-compatible client.
`custom <raw-value>` preserves casing and interior spaces (leading/trailing whitespace is trimmed; control characters are rejected).
Max/custom values are provider-specific: unsupported values surface an error without the legacy xhigh-to-high fallback.
Ultra/giga retain their legacy xhigh + numeric-budget default, but preserve an explicit max/custom selection.
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
--telemetry [port]         Standalone telemetry dashboard (default 6725)
--selftest [checks]        Run built-in end-to-end checks against this binary, exit 0/1 (CI harness)
--daemon                   Enable daemon triggers
--join <link>              Join a shared session as a guest (no config/provider needed), then exit
--register                 Register as OS service
--remove                   Unregister OS service
--watchdog                 Enable external watchdog
--delimiter <str>          Multi-line input delimiter
--model <id>               Override single-agent model
--mcp-strict [true|false]  Require all MCP servers
--docker-exec [true|false] Route execution through Docker
--sandbox [backend [img]]  Run shell/REPL exec inside a sandbox (host|docker|docker-container|podman|gvisor|kata|bwrap|...)
--acp                      Zed Agent Client Protocol transport (JSON-RPC over stdio)
--stateless                Boot straight into a stateless single-agent loop
--agent-mode               Boot straight into the standard single-agent loop
--swarm / --pswarm         Boot straight into multi-agent / parallel swarm
--teams                    Boot into teams mode
--sub / --subagents        Enable sub-agent delegation in the single-agent loop
--psub / --parasubagents   Enable parallel sub-agent delegation in the single-agent loop
--giga                     Giga mode (agent can spawn teams + author/run workflows)
--ultra / --ultraplan      Interactive deep-reasoning mode (plan + max reasoning + heavy delegation)
--verbose                  Verbose MCP/init logging
--report [session-id]      Generate report(s) and exit
--cfg <path>               Override config.json path (relative = launch dir; falls back to the current directory if the file is not found there)
--swarmcfg <path>          Override swarm.json path (relative = launch dir; falls back to the current directory if the file is not found there)
--workspace / --ws <path>  Set the @-file workspace root
--workflow / --wf <file>   Run workflow file
--classic / --tui          Force classic line renderer or live TUI
--prod                     Prod mode (orchestrator [[MARKER]] output)
--update                   Self-update from the latest GitHub release, then exit
--clear                    Clear terminal
--help, -h                 Show help and exit (no config read/write, no setup)
--version, -V              Print the bare version (x.y.z) and exit (no config read/write, no setup)
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
/agent          Single-agent conversation; /agent <name> starts it with that lead (Tab completes the
                name, and a partial or misspelled name picks the closest agent, e.g. /agent code -> CodeAgent)
/stateless      Stateless single-agent (no session persistence)
/sub, /psub     Enable sub-agent / parallel sub-agent delegation in the single-agent loop
/plan           Toggle plan mode (agent presents a plan + asks approval before executing)
/ultra          Interactive deep-reasoning inside the single-agent loop (plan + max reasoning + heavy delegation)
/giga           Interactive giga mode (ultra + agent can spawn teams and author/run workflows on the fly)
/continuous     Toggle autonomous execution (/cont shorthand)
/workflow <f>   Run a deterministic, replayable workflow file
/workflows      Live workflow-run viewer (+ saved | save <runId> [as <name>] | rerun <runId> | run <name> | delete <name>)
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
/cost all       Per-model matrixed breakdown: system/tools/cached/input/output/reasoning, session + rolling, $
/init           Analyze the workspace and scaffold a project context file (AGENTS.md)
/review         AI review of the working-tree diff (read-only findings)
!<command>      Run a shell command and inject its output into the conversation context
/tokens         Show the current token / context breakdown
/context [n]    Show usage, or set the context window (/context <n|64k|max>)
/prune [kind]   Deterministic local context trim, no model call (dupes | tools | stale; bare = all)
/insight [lvl]  Show/set the agent's context self-awareness: off | basic | full | max (also at the menu)
/paste          Paste clipboard text or a screenshot into the draft
/tokens all     Per-model matrixed cost/token breakdown (alias of /cost all)
/effort         Cycle live effort: low -> med -> high -> xhigh -> max -> low (also Shift+Tab)
/effort xhigh   Select extra-high reasoning, distinct from provider max
/effort max     Select real provider max (no silent fallback)
/max            Shortcut for /effort max
/effort custom <raw-value>  Send a literal provider effort (no silent fallback); next cycle returns to low
/undo           Drop the last exchange from history
/retry, /redo   Re-run the last turn
/wipe           Clear the session history
/tag <text>     Tag the live session for easy resume/search
/detach         Park the live session to the background (re-enter with /attach)
/voice          Local speech-to-text dictation into the compose field (/voice [auto|off|vol <1-10>])
/unhide <agent> Restore a hidden sub-agent to the viewport (hide via the \ Agent View 'h' key)
/qc, /qm        Exit the active session
```

### Live sharing (see ## Session Sharing)
```
/share [--local|--lan]  Share this session live; prints a join link. Asks loopback vs all-interfaces unless a flag is given
/share status   Show the link + who is watching (and who can type)
/share control <name|#id|all> on|off  Let a guest type, or make them view-only (Ctrl+] revokes all typing instantly)
/share kick <name|#id>  Disconnect a guest
/share stop     Stop sharing (invalidates the link)
/join <link>    Watch someone else's shared session (Ctrl+] then q to leave, r to redraw)
```

### Session lifecycle
```
/resume         Resume a previous single-agent session (inside a session it switches straight away;
                set console.confirmResume true to be asked first)
/sessions       Alias of /history (classic/stdio: saved sessions with type + agent count)
/history        Browse past sessions full-screen (fuzzy find, open, scroll, search)
/attach [id]    Re-enter a session parked via /detach (no id = pick from list)
/background, /bg  Manage detached background jobs (alias of the old /detach job command)
/report [id]    Generate session audit report(s); /report <id> audits a specific session
```

### Config & models
```
/model          View model assignments
/setmodel       Change the model for any agent / orchestrator / compaction agent (saved to Swarm.json).
                In a session (/agent, /stateless, /teams, /swarm, /pswarm, ultra, giga) it switches live:
                /setmodel <model> moves the agent you are talking to onto <model> with the session kept
                as-is (same id and file, every message and tool call); /setmodel <agent> <model> retargets
                any slot (name matched like /agent); bare /setmodel opens the picker. Changed specialists
                are rebuilt for their next delegation. If the provider reports a smaller context window
                than the session uses, it compacts first. Swarm modes apply it from the next goal. Teams:
                the lead switches live; members keep their history. The web UI's model button does the same
                (applied before the session's next turn).
/swap           Swap the active single-agent
/newagent, /editagent, /delagent  Create / edit / remove a swarm agent
/provider       View or switch the active LLM provider
/login [prov]   Log in to a subscription provider (claude|codex|kimi|...) via the CLIProxyAPI sidecar
/ping [prov]    Test the CLIProxyAPI sidecar + show per-provider login readiness
/proxy          Manage the CLIProxyAPI sidecar: /proxy status | /proxy update [pinned] | /proxy restart
/config         Show ALL config settings (every key is /set-able)
/set <key> <v>  Edit any config key (e.g. /set ultra.thinkingBudget 20000)
/showreasoning  full | summary (shown, grey italic) | none (hidden); persists to config
/sandbox [b][i] Show or swap the exec sandbox backend (host|docker|docker-container|podman|gvisor|kata|bwrap|...)
/dockerexec     Toggle the docker sandbox (microVM) on/off
/startargs <a>  Persist launch flags to run every start (clear with /startargs clear)
/limits         Display current execution limits
/maxp <n>       Max agents running in parallel (default 4)
/split          Arm optional lead-context sharing for delegated sub-agents (toggle)
/workspace <p>  Show or set the @-file workspace root
/addcontext     Configure what context each agent is injected with
```

### System
```
/onboard        Create or update operator profile (BRAIN.md + MEMORY.md)
/tools          List available MCP + native tools
/skills         List loaded skills
/theme          View or switch the TUI color theme (21 presets; bare /theme opens a picker; /theme <name> sets one)
/layout         Switch the input box shape: standard, rule, bracket, box, bubble, smooth, rail, bare (saved as
                console.tui compose.shape; colours stay with /theme)
/memory         Deep-memory status / toggle (/memory [deep|standard|show])
/deep           Toggle deep memory on/off (/deep [off])
/taskgraph      Auto-decompose a goal into a blockedBy task graph (/taskgraph on|off|status)
/createhook     Guided wizard to scaffold a hook, outbound webhook, or inbound webhook trigger
/hooks          Manage event hooks (/hooks on|off|create)
/daemon         Runtime daemon control (/daemon on|off|jobs|cron|watch|cancel; cron/watch bare = interactive builder)
/update         Self-update from the latest GitHub release (hash-verified; restarts if the binary changed)
/status         System status overview (provider, models, tools, skills, sessions)
/setup          Run initial setup / reconfigure
/reloadskills   Refresh the skills directory
/installskill   Install a skill by name from a curated set of sources, or from owner/repo, owner/repo/path, or a GitHub URL (recursive fetch + provenance stamp)
/refresh        Full system refresh (config, MCP servers, skills, extensions)
/extensions     Extensions panel: enable/disable, conflicts, trust, create, edit, reload
/classic, /tui  Switch renderer (classic line-by-line / live full-screen TUI)
/mouse [preset] Mouse preset for the frame engine (off | wheel | buttons; default buttons)
/telemetry [p|off]  Start/stop the standalone telemetry dashboard (default port 6725)
/logs              Recent notices (incl. the quiet startup lines: provider, skills, telemetry...) and runtime log
                   lines in a panel. Startup prints only warnings/errors; MUXSWARM_VERBOSE=1 prints everything.
                   The hooks confirm ("Proceed with hooks enabled?") is asked once the TUI is up.
/daemonview, /dv  Toggle collapsed/expanded daemon-fired goal output
/delimiter      Toggle the multi-line input delimiter
/help           Full command reference
/verbose, /sav  Toggle full-vs-collapsed tool output / sub-agent output
/shortcuts      Show keyboard shortcuts (alias /keys)
/clear          Clear the screen; also inside a session and with Ctrl+L (history stays in Ctrl+G)
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

## Deep Memory (reflectionAgent)

A background "deep memory" subsystem (off by default). When enabled, a background **gatherer**
distills durable reflections from the live session into a single auto-pruned `Context/reflections.json`
(atomic temp + rename, dedup by content) and an **injector** selects a small, scored, budget-capped
block to push into agent context, so working agents never spend a turn querying memory. Injection is
two-tier: an EPHEMERAL mid-turn delta (reaches the model right after a tool result, within the turn)
plus a DURABLE cross-turn block the orchestrator persists into the thread so it replays every future turn.

Enable/inspect with `/memory` (or `/deep`):

```
/memory            Status: mode, reflection model, inject budget, poll interval, scope,
                   reflection count, last-reflection time, store health (chroma/KG).
/memory deep       Enable deep mode (persists reflectionAgent.mode = "deep" to Swarm.json).
/deep              Alias for /memory deep.
/memory standard   Back to standard (inert, byte-identical to today). /deep off is the alias.
/memory show       Legacy knowledge-graph JSON dump (power users).
/memory set <k> <v> Tune a reflectionAgent field + persist (keys: model, budget, poll, floor,
                   scope, max, historyWindow, maxDigsPerTick, digMaxFilesScanned, digMaxMatches,
                   digMaxReadChars).
```

Swarm.json block (all keys optional; absent = inert standard mode):

```jsonc
"reflectionAgent": {
  "mode": "standard",          // "standard" (default) | "deep"
  "model": null,               // null -> orchestrator/compaction model (a cheap model is recommended)
  "modelOpts": { },            // optional ChatOptions for the gatherer call
  "injectTokenBudget": 1500,   // hard cap on the injected block (truncated, never overflowed)
  "pollIntervalSeconds": 90,   // background cadence; NO LLM call on a tick with no activity
  "relevanceFloor": 0.35,      // min score to inject (anti-noise)
  "scope": "lead",             // "lead" (lead/orchestrator only) | "all" (also sub-agents)
  "maxReflections": 30000,     // single-file store prune cap (oldest evicted; 1000 fallback if unset)
  "historyWindow": 30,         // recent messages the Pass-1 distill observes
  "maxDigsPerTick": 2,         // Pass-2 read-only digs chased per gatherer tick
  "digMaxFilesScanned": 4000,  // Pass-2 grep file scan cap
  "digMaxMatches": 40,         // Pass-2 grep match cap
  "digMaxReadChars": 8000      // Pass-2 single-file read cap
}
```

A bare top-level `"memoryMode": "deep"` is also accepted and overrides the nested `mode`.

**Store tiers (filesystem-first, graceful degrade):** the single `Context/reflections.json` is always
the primary, durable store (a legacy `Context/Reflections/` directory is auto-migrated then removed on
first load). The ChromaDB + Memory (knowledge-graph) MCP servers are OPTIONAL accelerators, and when
connected the gatherer mirrors reflections into them for semantic recall. When they are absent or
failing, deep mode STAYS ACTIVE and the injector degrades silently to lexical + recency + importance
scoring over the filesystem reflections (no warning, no drop to standard).

**Activity-gated:** the gatherer makes at most one light model call per poll, and skips the call
entirely when nothing has happened since the last reflection. Interactive sessions only (never
serve/acp/stdio). The distillation is grounded by BRAIN.md + MEMORY.md so reflections track the real
working world. Injected reflections are background hints only; agents are told not to query memory
themselves and to flag any reflection that conflicts with what they observe.

`/heal` / `/reflect` can additionally propose a new `SKILL` (alongside BRAIN/MEMORY write-backs):
accepting one scaffolds a `SKILL.md` under the skills dir and hot-reloads the skill manifest.

## Context insight - `/insight` (config `insight.level`)

Tells the lead `/agent` session how full its context window is, so it can plan around auto-compaction.
Off by default. `/insight <level>` applies immediately and persists to config.json (`"insight": { "level": "basic" }`).

| Level | The agent gets |
|---|---|
| `off` | nothing (default) |
| `basic` | `[mux context: 46.7k/200k (23%), auto-compact at 200k]` on every model call |
| `full` | basic `\| prev turn +38k \| tool output 61% \| compactions 1` |
| `max` | full + `dropped N`, plus the `context_map` / `context_drop` tools (next session) |

The line is appended to the newest input of each model call: the user's prompt, or the latest tool result.
It is saved in the session exactly as sent, so it is replayed history and the provider prompt cache is
unaffected (measured on Claude: same cached share as no line). Each line costs ~20 tokens (basic) or ~40
(full) and stays in history; only the newest is current, and the system prompt says so while insight is on.
Numbers match the footer and `/api/status` (tokens / auto-compact threshold). Every lead session (`/agent`,
`/teams` lead, stateless, resume, ACP, `--agent`) gets the line.

Specialists (`/swarm`, `/pswarm`), delegated sub-agents and team members get it too (basic/full), with THEIR
OWN numbers: tokens estimated from the request they are sending, against the team's member threshold for
persistent team members (`memberAutoCompactTokenThreshold`, checked after each task), otherwise
`auto-compact off` (nothing compacts those histories). At full they see `agent <name> | tool output N%`;
the lead-only figures (prev turn, compactions, dropped) are not shown. Orchestrator and compaction calls are
never stamped.

**max** also gives the lead agent - and every **persistent** team member - two tools (fixed when the session or
team starts: the next `/agent` session or team launch). A member's drops apply after its task, under its member
lock, before the unchanged member compaction check; snapshots are named `pre-drop-<team>-<member>-*`. Fresh
members, `/swarm`, `/pswarm` and delegated sub-agents get no drop tools (their history is not kept or is shared):
- `context_map()` - the largest droppable blocks as `m12 tool_result Filesystem_read_text_file ~7.4k tok "preview"`.
  A message holding several blocks (parallel tool results) lists each as `m12.0`, `m12.1`, ...; passing `m12`
  drops all of them.
- `context_drop(ids, reason)` - queue blocks to drop. They are removed at the END of the turn (the session is
  idle then): a recovery snapshot `<sandboxPath>/prune-recovery/pre-drop-<session>-*.muxprune` (+ `.txt`) is
  saved first, then each block becomes `[dropped by agent: <reason>; N chars; recover: search <snapshot>.txt]`.
  No snapshot (no sandbox, or sandbox outside allowedPaths) = nothing is dropped.

Only tool results and assistant text of >= 1,000 characters from earlier turns can be dropped, except the
newest ~4k tokens of them (counted per block, so one large read cannot shield everything older);
user and system messages, errors, and already pruned/dropped blocks never are. Messages and tool-call ids
stay in place (copy-on-write, like `/prune`). A compaction or `/wipe` before the turn ends discards queued
drops. Dropping changes earlier history, so the provider prompt cache restarts from that point (same as
`/prune`).

## Cost breakdown - `/cost all` / `/tokens all`

`/cost` and `/tokens` are unchanged (session usage, footer-mirrored). Adding the `all` arg renders a
per-model matrixed breakdown sourced from an in-process ledger (the OTEL counters are write-only):

```
/cost all       Per-model table: system prompt | tools (schema) | input | cached | output | reasoning
/tokens all     Alias of /cost all
```

Each model shows two columns - **session** (current, cleared on `/wipe`) and **rolling** (process-
lifetime, survives `/wipe`) - plus tool-call and compaction counts and a proportional split bar.
Real `$` comes from `ModelPricing.cs` (list-price estimate, input+output) for priced models;
subscription/cliproxy providers render tokens-only (flat-rate, no `$`). System-prompt and tool-schema
rows are the one-time static estimates already shown in the footer breakdown.

Token figures are SUMS of per-call usage: each model call reports its own usage, and Mux adds them
(session + rolling, `/cost`, the `Telemetry/metrics-*.jsonl` `kind=usage` lines - one line per model
call - and the OTel `mux.tokens.*` counters). `input` includes cached prompt tokens. `/cost`'s
`context now` is the size of the latest request, not a sum.

## Task auto-decomposition - `task_decompose` + `/taskgraph`

`task_decompose(goal)` is a lead/giga tool (and an opt-in background dispatcher) that turns a high-
level goal into a ready `blockedBy` task graph on the active board via ONE light-model call -
subtasks + dependency edges + suggested assignees - instead of hand-authoring many `task_create`
calls. The model returns a strict JSON array; tasks are created in topological order with local
indices mapped to real board ids, cycles are rejected, and unknown assignees fall back to null.

```
/taskgraph status    Show enabled/running + poll interval + maxSubtasks + model
/taskgraph on        Persist decompose.enabled=true; start the background tick dispatcher (taskboard team)
/taskgraph off       Persist decompose.enabled=false; stop the dispatcher (one-shot tool still available)
```

The dispatcher mirrors the auto-runner: a single non-blocking loop on the session token, floored
poll interval, activity-gated (zero LLM calls when its goal queue is empty). OFF by default. The
one-shot `task_decompose` tool works regardless of the toggle.

Swarm.json block (all keys optional; absent = off):

```jsonc
"decompose": {
  "enabled": false,            // /taskgraph on|off persists this
  "model": null,               // null -> compaction model, then orchestrator (keep it LIGHT)
  "pollIntervalSeconds": 60,   // background dispatcher cadence (floor 10)
  "maxSubtasks": 12            // hard ceiling on subtasks per decompose call
}
```

## Interactive-prompt bailout (`ask_user`, Select/Confirm/MultiSelect)

The `ask_user` tool and the underlying choice prompts no longer trap the user into the offered
options. Each choice prompt carries two bailout affordances:

- **Cancel** - back out entirely; the tool returns a "cancelled" result the model handles gracefully
  (no forced choice, no wasted extra turn).
- **Enter custom** - type a free-text value outside the option set; returned as a custom response.

Implemented additively on the `MuxConsole` prompt primitives (`SelectChoice` / `ConfirmChoice` /
`MultiSelectChoice` + the `PromptChoice` result); the base `Select`/`Confirm`/`MultiSelect` are
unchanged, so existing callers keep their behaviour. (Spectre's blocking picker exposes no raw Esc
hook, so cancellation is delivered via an explicit "cancel" affordance labelled with Esc.)

## TUI color themes - `/theme` + setup step

The interactive TUI palette (banner/accent, success/warning/error, info/muted, prompt text, the
agent-name color, and rendered-markdown heading/code/link/quote colors) is theme-driven, not
hardcoded. Built-in presets: `default` (original palette), `dark`, `light`, `mono` (no-color /
accessibility - inherits the terminal foreground), `solarized`, `dracula`, `gruvbox`, plus
`catppuccin-mocha`, `catppuccin-macchiato`, `catppuccin-latte`, `nord`, `tokyo-night`, `tokyo-storm`, `rose-pine`, `kanagawa`, `everforest`, `one-dark`, `monokai`, `ayu-mirage`, `synthwave`, `rose-dawn` (21 total).

```
/theme            Open the theme picker (live preview; fuzzy find) - lists presets with swatches + the active marker
/theme <name>     Apply + persist to config.json (console.theme); live-applies immediately
```

`/setup` includes a **Color Theme** step that shows each preset's swatch row and a sample of themed
chrome before you pick. The choice persists to `config.json`:

```jsonc
"console": { "theme": "default" }   // any preset name above, e.g. default | dark | nord | tokyo-night
```

The `default` theme (or any unknown/absent value) reproduces the exact pre-theme palette, so legacy
configs are byte-identical. Card, input, diff and code backgrounds are theme roles (`cardBg`, `inputBg`,
`diffAddBg`, `diffDelBg`, `diffHunkBg`, `codeBg`, `inlineCodeBg`); badge tints and the per-agent lane colors
stay fixed. Rendered markdown tables use `mdHeading` for header text and the `border` role (default
`#3A3A3A`) for grid lines.

Your own per-region styles go in `console.tui`, with the same schema as an extension's `tui` block (see the
**tui** key under Extensions). They apply over every extension (yours win per property) and also work with
extensions off; problems are shown at startup and in `/extensions`:

```jsonc
"console": { "theme": "nord", "tui": { "glyphs": "ascii", "regions": { "input": { "placeholder": "" } } } }
```

Extensions can add more themes (see ## Extensions); they appear in `/theme`, the picker and setup.

## Extensions

Extensions customize Mux without rebuilding it. An extension is a folder holding an
`extension.json` manifest. Mux looks in `<install>/Extensions/` plus any folders listed in
`extensions.dirs`. Extensions are **off by default**: with the gate off, nothing is scanned.

```jsonc
// config.json
"extensions": {
  "enabled": true,            // master gate (default false); /set extensions.enabled on
  "dirs": ["D:/my-extensions"] // optional extra folders; relative paths resolve against the install dir
}
```

```jsonc
// <install>/Extensions/dusk/extension.json
{
  "name": "dusk",              // a-z, 0-9, '-' (max 32); defaults to the folder name
  "version": "1.0.0",
  "description": "Warm dark theme",
  "themes": [
    { "name": "dusk", "base": "nord", "accent": "#FF8800", "banner": "#FF8800", "cardBg": "#2B2A33" }
  ]
}
```

- **themes**: each entry needs a `name` (cannot reuse a built-in name), an optional `base` built-in
  preset (default `default`; one of `default dark light mono solarized dracula gruvbox catppuccin-mocha
  catppuccin-macchiato catppuccin-latte nord tokyo-night tokyo-storm rose-pine rose-dawn kanagawa
  everforest one-dark monokai ayu-mirage synthwave`), and any color roles to override: `step success warning error info muted
  accent prompt banner agent mdHeading mdCode mdLink mdQuote cardBg inputBg diffAddBg diffDelBg
  diffHunkBg codeBg inlineCodeBg border`. Values are `#RRGGBB`, `default`, or one of the names the TUI draws:
  `black white red green yellow blue grey` (other Spectre names such as `purple` are rejected).
- **spinner**: thinking-indicator frames (1-64, each 1-8 terminal cells: any text, multi-character,
  emoji or ZWJ sequences, e.g. `["-", "\\", "|", "/"]` or `["[=  ]", "[ = ]", "[  =]"]`). Narrower frames
  are padded to the widest so the line does not jitter. Replaces the built-in braille spinner on the main
  agent line (TUI and classic console).
- **quips**: words the indicator cycles while the agent reasons (1-200, max 80 chars). Replaces the built-ins.
- **thinking**: the whole indicator animation, every key optional:
  `{ "frames": [...], "intervalMs": 80, "quips": [...], "quipIntervalMs": 2000, "shuffle": true, "suffix": "..." }`.
  `frames`/`quips` follow the rules above and win over the top-level `spinner`/`quips`; `intervalMs` (40-1000)
  is the frame time, `quipIntervalMs` (500-60000) how long each quip shows, `shuffle: false` cycles quips in
  order, `suffix` (max 8 chars, may be empty) follows the quip. Invalid timing keys keep the built-in timing.
- **toolLabels**: verb stem -> action label, e.g. `{ "read": "Peeking at", "search": "Hunting" }` (max 40 chars).
  The verb stem is the first word of a tool name after its server prefix (`Filesystem_read_text_file` -> `read`).
  Merged over the built-ins; across extensions, keys merge in name order and later names win.
- **splash**: startup lines (1-200, max 80 chars). Replaces the whole built-in pool (quotes, facts, tips,
  taglines) in the TUI splash and the web UI tip.
- For spinner, quips, thinking and splash the later extension name wins (shown as a conflict in `/extensions`). All text is plain text:
  Spectre markup such as `[red]` is shown literally, never interpreted.
- **footer**: `{ "template": "{mode} {context} {model} {effort}" }` picks which TUI footer chips show and
  in what order. Ids: `share orchestration mode turn session context cached sys tools calls model effort hint`.
  Only the `{id}` placeholders count: text between them is ignored and chips keep the usual ` · `
  separator. Chips you leave out are hidden, except `share`, which always shows (first) so a live
  broadcast is never hidden. Unknown ids are skipped with a warning. Narrow terminals still drop chips
  in the built-in priority order. If several extensions set a template, the later name wins.
- Extensions load in name order. If two define the same theme, the later name wins (shown as a conflict).
  Invalid manifests or entries are skipped with a warning; they never stop startup.
- **markdown**: `{ "tableBorder": "rounded", "bullet": "▸", "rule": "═" }`. `tableBorder` is `heavy`
  (default), `light`, `rounded` or `ascii`; `bullet` and `rule` must each be one visible terminal cell.
  Invalid keys keep the built-in glyph (with a warning). If several extensions set it, the later name wins.
  Applies to the TUI and to markdown panels in the classic console.
- **tui**: per-region styles for the TUI, the terminal counterpart of the web `styles` CSS. `regions` maps a
  region to its properties:
  ```jsonc
  "tui": { "regions": {
    "notice":    { "okMark": "+", "ok": "accent" },
    "reasoning": { "fg": "#8A8A8A", "italic": false },
    "header":    { "show": false }
  } }
  ```
  Colour values are a theme role (`accent`, `muted`, `text`, `success`, `cardBg`, ... any role listed under
  **themes**, plus `text` = body text; roles follow `/theme`) or a literal `#RRGGBB`, `default`, `black`, `white`,
  `red`, `green`, `yellow`, `blue`, `grey`. Glyphs must fit their width (1 cell unless noted). Regions and properties:
  - `notice` (the `✓ ! x` status lines): `okMark` `warnMark` `errorMark` (1-2 cells), `ok` `warn` `error` `info` colours.
  - `header` (session card): `mark` (1-2 cells, `┃`), `accent`, `show`.
  - `userMessage` (your echoed message): `bar` (`▎`), `barFg`, `fg`.
  - `agentHeader` (`▸ Agent ───`): `mark` (1-2 cells), `rule` (repeated), `fg`, `ruleFg`.
  - `answer`: `lead` (the `●` before each answer block), `leadFg`.
  - `reasoning`: `fg`, `italic` (true/false). Also applies in the classic console.
  - `toast`: `mark` (`◆`), `markFg`, `fg`.
  - `divider` (side-pane divider): `glyph`, `fg`, `activeFg` (while dragging). `scrollbar`: `thumb`, `track`, `thumbFg`, `trackFg`.
  - `toolCard` (tool calls, results, expanded cards, live panels): `dot` (running/done), `ok`, `error`, `result` (`⎿`),
    `rail`, `pendingFg`, `okFg`, `errorFg`, `labelFg`, `railFg`, `bg` (card fill; off with content backgrounds off).
  - `diff`: `rail`, `railFg`, `addFg`, `delFg`, `ctxFg`, `addBg`, `delBg`, `hunkBg`, `gutterFg`.
  - `delegation` (delegations, sub-agent rows): `arrow` (`→`), `branch` (`└`), `mark` (`▸`), `done`, `failed`, `fg`.
  - `thinking` (the working line and live sub-agent status): `fg`. Frames and quips stay in `thinking` at the top level.
  - `footer`: `separator` (1-3 cells, between chips; clickable chips stay aligned), `separatorFg`, `rule` (the lines above
    the footer and the compose box), `ruleFg`, `ruleShow` (false hides both lines).
  - `input` (compose box): `prompt` (1-2 cells), `promptFg`, `continuation`, `continuationFg`, `placeholder` (text, max
    60 cells, may be empty), `placeholderFg`, `fg`, `cursorFg`, `cursorBg`, `rail`, `railFg` (the rail shown with
    `inputHighlight`).
  - `diff` also: `corner` (`╭`), `end` (`╰`), `line` (`─`) for the card's top and bottom border.
  - `palette` (slash palette and the `/skill`, `/resume`, `/agent`, `@` and tools completions): `selected` (`›`),
    `selectedFg`, `itemFg`, `titleMark` (`┃`), `titleFg`, `up` / `down` (the `↑ N more` markers).
  - `modal` (ask_user / confirm / select / text prompts): `title` (`▸`), `titleFg`, `selected`, `selectedFg`,
    `checked` (`◉`), `checkedFg`, `unchecked` (`○`).
  - `panel` (rules under picker and panel headers, docked panel tabs): `rule`, `ruleFg`, `tabFg`, `tabActiveFg`,
    `focusFg` / `focusBg` (the focused tab).
  - `block` (titled command-output blocks, e.g. /status, /cost, /daemon): `rail` (`│`), `railFg`, `titleFg`.
    Output longer than 12 rows (or the screen) opens in a scrollable full-screen panel (`panel.*` chrome;
    j/k, PgUp/PgDn, `/` search, Esc/q close) and leaves a one-line note in the transcript.
  - `splash`: `show` (false = one version line instead of the banner card), `art` (1-12 lines, at most 80 cells each,
    replacing the MUX/SWARM art), `artFg`, `accentFg` (the built-in robot).
  **Layout (shape) keys.** These change the shape of the live band, not just its colours. `/layout <shape>` sets
  `compose.shape` in your `console.tui` (it wins over extensions; `/layout standard` removes it), so a shape mixes with
  any extension's other styles. In extensions, prefer theme roles (`accent`, `muted`, `border`, ...) over hex colours
  so the look follows every `/theme`:
  - `compose.shape`: `classic` (default: rule, footer, rule, `›` input), `rule` (one rule, input under it), `bracket`
    (rules above and below the input), `box` (a frame around the input), `bubble` (a rounded outline all the way
    round the input; with `footer.position: border` the footer is the last row inside it), `smooth` (a filled field
    with round end caps), `rail` (a filled field with one coloured left rail), `bare` (just the prompt). With a shape,
    the stock footer and input rules are not drawn; the shape draws its own chrome.
  - `compose.border` (`box`): `square` (default, sharp corners), `rounded`, `double`, `thick`, `ascii`. `bubble` is
    always rounded. `compose.padX` 0-3 (default
    1), `compose.padY` 0-1, `compose.gap` 0-2 blank rows above the footer and compose block. Colours/glyphs:
    `compose.borderFg`, `compose.rule`, `compose.fill` (smooth/rail), `compose.capLeft`/`capRight`, `compose.rail`,
    `compose.railFg`.
  - `footer.position`: `above` (default), `below` the input, `hidden`, or `border`: inside the top edge of a `box`,
    `rule` or `bracket` shape (`╭─ ultra · 45% ──── model ─╮`), or the last row inside a `bubble`; other shapes fall
    back to `above`.
  - `footer.style`: `dot` (default), `pipe`, `space`, `powerline` (needs a Nerd/powerline font), `ascii`.
    `footer.strip: true` fills the row with `footer.bg`; `footer.fill: rule` draws a line between the left and right
    groups. Split the footer into groups with `||` in `footer.template`: `"{mode} {context} || {model} {effort}"`
    puts model and effort at the right edge (a single `|` stays plain text). Clickable chips follow their text.
  - `header.template`: the session card text with `{agent}`, `{model}`, `{provider}`, `{tools}`; `header.frame:
    box` draws a rounded card around it.
  - `userMessage.style`: `bar` (default: accent bar before your message) or `bubble` (your message in a rounded
    outline that hugs the text, in `userMessage.barFg`).
  - `splash.frame`: `rounded` (default), `square`, `double`, `thick`, `ascii`, `none`; `splash.borderFg`;
    `splash.padY` 0-2 (default 1); `splash.align`: `left` (default) or `center` (the panel hugs its content and
    is centered); `splash.columns`: `all` (logo, getting-started, recent; default), `logo`, `logo,recent`,
    `logo,getting-started`, `getting-started,logo`, `recent,logo` (choose and order the wide-screen columns;
    a single column keeps the compact one-panel look at any width).
  - `splash.layout` (replaces the stock arrangement and `splash.columns`): 1-8 row strings; a row holds 1-3 cells
    split by `|`, a cell is a stack of section names split by spaces. Sections: `logo` (the stock art + version +
    message + link block), `art`, `title` (`splash.title`, default `MUX-SWARM`, in `splash.titleFg`), `version`,
    `message` (the random quote/tip), `hint` (`/agent to start ...`), `link`, `getting-started`, `commands`, `tips`,
    `recent`, `text` (`splash.text`: 1-12 theme lines in `splash.textFg`), `gap` (a blank row). Cells in a row share
    the width (art keeps its width, text cells take the rest); a row too wide for the terminal stacks its cells.
    `splash.divider` `rule` (default; lines between rows and cells) or `gap`. `splash.width` 1-100 = panel width in
    percent of the terminal (0 = default: full width, or hug the content when centered). `splash.contentAlign`
    `left`/`center` centers each section in its cell. `splash.borderTitle` puts text in the top border
    (`╭─ mux 0.16.0 ───╮`). `{version}` and `{cwd}` work in `title`, `borderTitle` and `text`.
  - Section styles (any layout, including the stock one): `splash.headings` `plain` (default) / `rule` (`── Title ──`)
    / `strip` (filled with `splash.headingBg`) / `none`, in `splash.headingFg`; `splash.bullet` (one cell, e.g. `•`)
    leads list rows, in `splash.bulletFg`; `splash.recentCount` 0-10 (default 5; 0 hides recent sessions).
    Example, a two-row grid:
    ```jsonc
    "splash": { "layout": ["art | title version message", "commands | recent"], "width": 90, "align": "center",
                "headings": "rule", "bullet": "›", "borderTitle": "mux {version}" }
    ```
  - Full-screen views (`/extensions`, the model/settings/agent/theme pickers, history, extension panels opened
    full screen): `panel.header` `plain` (default) / `strip` (filled with `panel.headerBg`) / `rule` (title set in a
    `panel.rule` run), `panel.headerAlign` `left`/`center`; `panel.selectedStyle` `chevron` (default) / `bar`
    (`panel.bar`) / `fill` (`panel.selectedBg` behind the row); `panel.hints` `plain`/`strip` (`panel.hintBg`);
    `panel.frame` `none` (default) / `rounded` / `square` / `double` / `thick` / `ascii` (`panel.frameFg`) draws a box
    around the whole view (skipped below 24x8). Clicks keep working in every style.
  - `toolCard.style`: `bullet` (default), `gutter` (one prefix glyph per tool line, `toolCard.gutter`, default `┊`)
    or `box` (side rails `toolCard.boxLeft`/`boxRight`).
  Example, an editor-style box with the status line in its border:
  ```jsonc
  "footer": { "template": "{mode} {context} || {model} {effort}" },
  "tui": { "regions": {
    "compose": { "shape": "box", "border": "rounded", "padX": 1 },
    "footer":  { "position": "border", "style": "pipe" }
  } }
  ```
  `"glyphs": "ascii"` (next to `regions`) swaps every built-in glyph for plain ASCII (`+ * > | - #`) for terminals or
  fonts without box drawing; region keys still override single glyphs on top. Default `unicode`.
  Your own `console.tui` in config.json (same schema, see TUI color themes) wins over every extension.
  A complete example that restyles every region (save as `Extensions/ember/extension.json`):
  ```json
  {
    "name": "ember",
    "version": "0.1.0",
    "description": "Warm theme that restyles every TUI region (demo)",
    "themes": [
      {
        "name": "ember",
        "base": "gruvbox",
        "accent": "#FF8C42",
        "banner": "#FF8C42",
        "agent": "#F4C27A"
      }
    ],
    "tui": {
      "regions": {
        "notice": {
          "okMark": "✔",
          "warnMark": "▲",
          "errorMark": "✖",
          "info": "muted"
        },
        "header": {
          "mark": "◆",
          "accent": "accent"
        },
        "userMessage": {
          "bar": "┃",
          "barFg": "#FF8C42"
        },
        "agentHeader": {
          "mark": "◆",
          "rule": "┄",
          "fg": "agent",
          "ruleFg": "border"
        },
        "answer": {
          "lead": "•",
          "leadFg": "accent"
        },
        "reasoning": {
          "fg": "#8A7F72",
          "italic": true
        },
        "toast": {
          "mark": "◆",
          "markFg": "accent"
        },
        "divider": {
          "glyph": "┃",
          "activeFg": "#FF8C42"
        },
        "scrollbar": {
          "thumb": "┃",
          "track": "│",
          "thumbFg": "#FF8C42"
        },
        "toolCard": {
          "dot": "◆",
          "ok": "✔",
          "error": "✖",
          "result": "╰",
          "rail": "┃",
          "labelFg": "#F4C27A",
          "railFg": "#5C4A3A"
        },
        "diff": {
          "rail": "┃",
          "corner": "┏",
          "end": "┗",
          "line": "━"
        },
        "delegation": {
          "arrow": "↪",
          "mark": "◆",
          "done": "✔"
        },
        "thinking": {
          "fg": "#FFB86B"
        },
        "footer": {
          "separator": "│",
          "separatorFg": "#5C4A3A",
          "rule": "━",
          "ruleFg": "#5C4A3A"
        },
        "input": {
          "prompt": "▸",
          "promptFg": "#FF8C42",
          "continuation": "┃",
          "placeholder": "what are we building?",
          "cursorBg": "#FF8C42"
        },
        "palette": {
          "selected": "▸",
          "titleMark": "◆",
          "selectedFg": "#FF8C42"
        },
        "modal": {
          "title": "◆",
          "selected": "▸",
          "checked": "◆",
          "unchecked": "◇"
        },
        "panel": {
          "rule": "━",
          "ruleFg": "#5C4A3A",
          "focusBg": "#FF8C42"
        },
        "splash": {
          "art": [
            "  ▄▄▄ ▄   ▄ ▄▄▄  ▄▄▄ ▄▄▄",
            "  █▄▄ █▀▄▀█ █▄▄█ █▄▄ █▄▄▀",
            "  █▄▄ █   █ █▄▄█ █▄▄ █  █",
            "  ember  -  a warm Mux"
          ],
          "artFg": "#FF8C42"
        }
      }
    }
  }
  ```
  Every key is checked: an unknown region or property, a wrong type, a colour the TUI cannot draw or a glyph of
  the wrong width skips that extension's whole `tui` block (the error is listed in `/extensions`; the rest of the
  extension still loads). Several extensions merge per property in name order (later wins; each overridden
  region shows as a conflict). Restyling applies to new output, like `/theme`. `--stdio`, `--serve` and ACP ignore it.
- `/refresh` (or `r` in the `/extensions` panel, or `/extensions reload`) re-reads every manifest from
  disk, restarts the trusted processes and re-applies `console.theme`; problems and conflicts are
  recomputed, so a fixed manifest clears its warning on reload. Agent tools change from the next session.
  Mux reads the manifests of the instance you run: edit `<install>/Extensions/<name>/` of that install
  (the panel's details pane shows the exact folder).
- **`/extensions`** opens a panel (TUI; works at the menu and inside a session) listing every installed
  extension with badges (`●` on, `○` disabled, `▶` process, `⇄` conflict, `!` problems) and the selected
  one's contributions, process state, conflicts and problems. Keys (each also a clickable button; click
  selects, double click toggles, the wheel scrolls the list or the details): Space/Enter enable or disable,
  `t` trust/untrust its process (trusting takes a second `t`), `n` new, `e` edit `extension.json` (`$VISUAL`/`$EDITOR` in
  this console, else the OS default app), `o` open the folder, `r` reload, `/` search, Esc close.
  Changes save to config.json and apply at once; new agent tools arrive with the next session.
- When two extensions set the same thing (a theme name, spinner, quips, splash, footer template, markdown
  style), the later name wins and the panel shows the conflict on both. Disable the winner to use the
  other: `"extensions": { "disabled": ["glow"] }`. A disabled extension is listed but contributes nothing.
  A command name taken by an earlier extension stays with that one.
- `/extensions new [name]` scaffolds `<install>/Extensions/<name>` from the parts you pick (theme, spinner
  + quips, footer chip, slash command, agent tool, interceptor; process parts get a working `main.py`
  that answers every message), offers to trust it and reloads. It then offers to have the agent build it
  out: inside a session the brief becomes your next message; at the menu it starts an `/agent` session
  with the brief as the opening goal. The brief points the agent at this section and ## Event Hooks.
- Outside the TUI, `/extensions` prints the same information as text; change it with
  `/extensions enable|disable|trust|untrust <name>`, `/extensions new [name]` and `/extensions reload`.
- The web UI keeps its own built-in theme set and also lists extension themes (served by
  `GET /api/themes`, mapped onto its colors) in the theme picker, as `ext-<name>`.

### Customizing the web UI (`web` block)

When a user asks to restyle the web UI (fonts, colors, chat bubbles, composer, reading width,
light/dark), build an extension with a `web` block. It is static content only: CSS, fonts and
images, served from the extension folder. Extension JavaScript is never served.

```jsonc
// <install>/Extensions/paper/extension.json
{
  "name": "paper",
  "web": {
    "themes": [ {
      "id": "paper",                       // a-z, 0-9, '-' (max 32); web id "ext-paper"
      "name": "Paper",
      "variants": {                        // optional; omit for one mode and use "vars" instead
        "light": { "vars": { "--bg": "#faf8f2", "--text": "#1f1d1a", "--accent": "#b4532a" }, "tui": "paper-light" },
        "dark":  { "vars": { "--bg": "#22201d", "--text": "#f1ede4", "--accent": "#d9784f" } }
      },
      "defaultVariant": "system",          // light | dark | system (follows the OS)
      "css": ["paper.css"],                // applied only while this theme is active
      "fonts": [ { "family": "Literata", "src": "fonts/literata.woff2", "weight": "200 900" } ]
    } ],
    "css": [],                             // optional: applied whenever the extension is enabled
    "assets": "web/"                       // folder served at /ext/paper/ (default web/)
  }
}
```

```css
/* web/paper.css: override tokens, target data-mux hooks. No !important needed. */
:root {
  --mux-font-prose: "Literata", Georgia, serif;
  --mux-prose-size: 16px;
  --mux-prose-leading: 1.65;
  --mux-content-max: 720px;
  --mux-bubble-radius: 18px;
  --mux-composer-radius: 20px;
  --mux-composer-bg: var(--bg);
  --mux-header-border: transparent;
}
[data-mux="msg-user"] .bubble { box-shadow: none; }
```

- Paths are relative to `assets` and must stay inside it (no `..`, absolute paths or symlinks out).
  Types: css, woff2, woff, ttf, otf, svg, png, webp, json. Limits: CSS 256 KB per file, fonts 2 MB,
  8 MB per extension. Bad entries are skipped with a warning in `/extensions`; startup never fails.
- Picking a web theme never changes `console.theme`; `tui` is metadata only.
- Same id in two extensions: the later name wins (shown as a conflict). A web theme also replaces a
  TUI-mapped theme with the same `ext-` id.
- `/extensions reload` (or `/refresh`) re-reads manifests and sends `ui_themes_changed` on `/ws`;
  open web UIs re-fetch `/api/themes` and swap the CSS without a page reload.
- `/share` guests only receive these theme assets; nothing else is exposed.

**Layers.** The web app orders its CSS as `@layer mux.base, mux.strong, mux.theme, mux.ext, mux.user`.
Extension CSS loads into `mux.ext`, so it beats the base styles and the theme colors without
`!important`; the user's own Settings (font size, custom colors) in `mux.user` still win.

**Tokens** (defaults reproduce the stock look; the short vars `--bg --text --muted --accent --border
--bg-card --bg-input --font-sans --font-mono` still work):

| Token | Controls |
|---|---|
| `--mux-font-ui` / `--mux-font-prose` / `--mux-font-code` | UI chrome / agent and user message text / code |
| `--mux-prose-size` / `--mux-prose-leading` | Agent message font size / line height |
| `--mux-content-max` | Reading column width (default `100%`; e.g. `720px` centers a column) |
| `--mux-radius` | Agent message block corner radius |
| `--mux-bubble-bg` / `--mux-bubble-fg` / `--mux-bubble-radius` | User message bubble |
| `--mux-composer-bg` / `--mux-composer-shadow` / `--mux-composer-radius` | Input composer |
| `--mux-surface` / `--mux-surface-raised` / `--mux-code-bg` | Panels and sidebar / raised controls / code blocks |
| `--mux-header-bg` / `--mux-header-border` | Top bar |
| `--mux-tint-rgb` | Overlay color (as `r, g, b`) for hovers, tracks and separators; white on dark schemes, black on light |
| `--muted-rgb` | `--muted` as `r, g, b` for translucent neutral chips and scrollbars |
| `--mux-inset-bg` / `--mux-surface-solid` / `--mux-scrim` | Inset inputs and code chips / opaque menus (command palette) / modal backdrops |
| `--mux-reasoning-fg` / `--mux-on-accent` | Reasoning text / text on accent-filled buttons |

Scheme-aware defaults live under `:root[data-mux-scheme="light"]`, so a light theme only has to set the core colors.

**File chips.** File paths in agent replies, tool output and inline code (absolute Windows/UNC/POSIX paths,
`file://` URLs, and sandbox-relative paths inside backticks) become chips once `GET /api/file/meta` confirms the file
exists under the sandbox, sessions folder or `filesystem.allowedPaths`. A chip shows the file name (full path in its
tooltip). Click opens it in the preview panel; the hover actions download it or copy its full path. In the desktop app an extra
action opens the file with its default app (`muxHost.openPath`; executables and shortcuts are refused). Image paths also render an
inline thumbnail. Turn both off with Settings > File chips and image previews.

**Workspace.** Files opened from chips collect as tabs in the preview panel. The dock button pins the panel to the
right edge (drag its left edge to resize; the chat column makes room); the eye button turns on **Follow agent**,
which opens each file a write/edit/move/download tool reports, so you can watch what the agent is producing. Both
choices persist per browser. The floating panel stays the default.

**Sessions sidebar and composer chips.** A left sidebar lists sessions (newest first, grouped by day with counts, Older
collapsed; titled by their first message, up to two lines, with the time and message count; pins shared with the Sessions
tab is gone; the rail is the one sessions list; the resumed session is highlighted). Click opens a read-only
**viewer** over the transcript (the saved conversation, with Resume / Copy ID / Download; Esc closes); Resume or a
double-click sends `/resume <id>`, and the page then redraws that session's last 200 messages above a "Resumed" divider
(client-side from the session file; the engine already restores the context). Hover pins or copies the ID, **+ New** sends
`/agent`. Drag its right edge to resize (220-520px). It starts open on wide screens and remembers your choice (toggle next
to the wordmark). In an agent session the composer shows an **Effort** chip (sends `/effort <level>`) once the session has
run a turn, a **context gauge** (tokens until auto-compact, from `/api/status`) with a **Compact now** action, and an
**Actions** menu (Compact, Undo, Retry, Copy last reply, Wipe with a confirm). The chevron at the left of the controls
collapses the composer to a slim bar (`/` or Ctrl+Shift+Space reopens it; it also reopens when the agent asks a question).

**Default theme: Broadsheet.** New installs (and Settings > Reset) open in Broadsheet: a 760px centered reading column,
the bundled Newsreader serif for all text (`--font-serif`; change it under Settings > Typography > Reading font; code,
diffs and tool output stay monospace), square 3px corners and hairline rules, and a stacked composer (full-width input,
controls below), with Paper (light) and Ink (dark) variants that follow the system scheme. Every other built-in preset
uses the same layout with its own palette (Matrix keeps monospace type): the rules key on `html[data-mux-style="broadsheet"]`,
which the web UI sets for built-ins. **Classic** (id `zinc`, so a saved choice keeps working) and extension themes keep the
original layout (`data-mux-style="classic"`). A theme already picked in the browser is kept.

**Hooks** (stable contract; class names are not): `data-mux="header | header-actions | transcript |
msg-user | msg-agent | prose | tool | event | thinking | composer | composer-input | composer-actions |
splash | splash-title | splash-tip | sidebar | titlebar-slot"`. The root element carries
`data-mux-theme="<id>"`, `data-mux-scheme="light|dark"` and, inside a desktop shell, `data-mux-host`.

### Extension processes

An extension can run a long-lived process in any language. Processes run with your user
permissions, so each one must be listed in `extensions.trusted` before Mux starts it.

```jsonc
// extension.json
{
  "name": "notify",
  "process": { "command": "python", "args": ["-u", "main.py"] }, // no shell; relative paths resolve in the extension folder
  "events": ["turn_end", "tool_call"]                            // lifecycle events to receive ("*" = all)
}
// config.json
"extensions": { "enabled": true, "trusted": ["notify"] }
```

**Limit an extension to some agents** with `extensions.agents` (only your config can do this):

```jsonc
"extensions": { "trusted": ["concierge-gate"], "agents": { "concierge-gate": ["ConciergeAgent"] } }
```

A scoped extension's process still runs once, but it only intercepts tool calls made by those agents
(including their delegated sub-agents), only receives their events plus events not tied to any agent
(`mcp_server_connected`, `theme_changed`, ...), and its tools are offered only to them, whatever
Swarm.json or the tool's own `agents` grant says. A call whose agent is unknown still goes through its
interceptors, so a scoped gate never silently skips one. Commands, panels and footer chips stay global.
Extensions not listed apply to every agent; `/extensions` shows the scope.

Mux and the process exchange newline-delimited JSON-RPC 2.0 over stdin/stdout:

- First message: notification `initialize` with `{ protocolVersion: 1, muxVersion, name, dir, dataDir }`
  (`dir` = the extension folder as the process sees it; `dataDir` = its writable folder when sandboxed, else absent).
- Each subscribed event: notification `event` whose params are the hook payload (`event`, `timestamp`,
  plus `agent`, `tool`, `args`, `text`, `summary`, `goalId` as set). The event names and the fields each
  one carries are listed in the table under ## Event Hooks.
- Requests from Mux carry an `id`; reply with `{ "jsonrpc": "2.0", "id": <same>, "result": ... }`.
- stdout must carry only JSON-RPC lines, one per line, encoded as UTF-8 (Mux writes UTF-8 without a BOM
  to stdin). On Windows, Python defaults pipes to the ANSI code page: call
  `sys.stdout.reconfigure(encoding="utf-8")` (or send ASCII-escaped JSON, Python's `json.dumps` default)
  or non-ASCII text such as a moon glyph fails with `UnicodeEncodeError`. stderr is captured;
  `/extensions` shows its last lines.
- **Tools**: a process can serve tools to the agent (`tools` key, below). Mux sends a request
  `tool/call` with `{ name, arguments, agent, parentAgent, flowId }` (the caller; see below); reply `{ "result": { "content": "<text for the model>" } }`,
  or a JSON-RPC `error` object (`{ "code": 1, "message": "..." }`). Errors, a missed timeout
  (`timeoutSeconds`, default 60, max 600) or an exited process are returned to the model as
  `[extension <name>] tool failed: <reason>`; they never stop the turn.
- Events are queued and dropped (never blocking Mux) if the process falls behind. On `/refresh` or
  exit, Mux closes stdin, waits up to 2s, then stops the process. A process that exits is not restarted
  until the next `/refresh`.
- Messages from the process that are not responses are notifications Mux understands
  (`footer/set`, `command/output`, below) or requests to Mux (Extension host API, below). Other
  notifications are ignored.

### Extension commands

A trusted process can add slash commands. They work in `/agent`, `/swarm`, `/pswarm` and at the
menu, on every interface (TUI, classic console, `--serve`, `--stdio`, ACP), and show in the palette.

```jsonc
// extension.json
"commands": [ { "name": "stats", "description": "Repo stats", "timeoutSeconds": 120 },
              { "name": "board", "description": "Open the board", "immediate": true },
              { "name": "contacts", "description": "Manage contacts", "usage": "<add|list|rm> [name]",
                "subcommands": [ { "name": "add", "description": "Add a contact" },
                                 { "name": "list", "description": "List contacts" } ] } ]
```

- `name`: `a-z`, `0-9`, `-`, starting with a letter (max 32). Built-in commands (and their aliases)
  can never be taken; if two extensions declare the same command the first by name keeps it.
- `usage` (optional, max 60 chars) is shown after the description in the palette and `/api/commands`.
  `subcommands` (optional, max 24; names like command names) each get their own palette row
  (`/contacts add`), so they filter and Tab-complete like built-in subcommands. They are descriptive
  only: `/contacts add Ann` still sends one `command/run` with `args: "add Ann"`.
- `/stats some args` sends a `command/run` request with `{ name, args, cwd, mode, agent }` (`mode` is the
  running mode: `agent`, `swarm`, `pswarm`, `interactive` at the menu, ...; `agent` is the session's lead
  agent, omitted at the menu).
- While it runs the process may send `command/output` notifications (`{ "text": "..." }`), shown live.
  Esc cancels the wait and Mux sends `$/cancelRequest` (`{ id }`). `timeoutSeconds`: default 60, max 3600.
- Reply `result`: `{ "output": "...", "prompt": "...", "open": { "id", "title", "view" } }`, all optional;
  `{}` shows nothing. `output` is shown to you; `prompt` is sent to the agent as if you typed it (at the
  menu, where there is no agent, it is shown); `open` opens a panel exactly like `ui/open` (below).
  A JSON-RPC `error` is shown as a warning.
- `immediate: true` runs the command at once even while the agent is working: in the TUI type it in the
  steer box (Ctrl+N), on the web or `--stdio` just send it. `command/run` then also carries
  `immediate: true`. It never starts a turn itself (a returned `prompt` is queued as the next input) and
  only its `timeoutSeconds` ends it (Esc keeps cancelling the turn). Default `false`: a command typed
  mid-turn waits for the turn to end.
- `/share` guests cannot run extension commands.

### Extension host API

A trusted process can also send requests TO Mux (with an `id`; Mux replies with the same id, `result`
or a JSON-RPC `error`). Requests are answered while a command, tool or event is in progress too.

| Method | Params | Result |
|---|---|---|
| `session/info` | none | `{ mode, inSession, agent, model, sessionId, cwd, surface, muxVersion, context: { tokens, window, percent } }` |
| `ui/toast` | `{ text }` | `{}`: a short notice (TUI: above the footer for 4s; web: a toast; classic: an info line) |
| `ui/log` | `{ text }` | `{}`: a muted line in the transcript; the model does not read it |
| `ui/open` | `{ id, title?, view, placement?, columns?, rows? }` | `{}`: opens (or replaces) panel `id` (see Extension panels) |
| `ui/update` | `{ id, view }` | `{}`: replaces the tree of open panel `id` |
| `ui/close` | `{ id }` | `{ closed }`: closes panel `id` |
| `prompt/submit` | `{ text }` | `{ queued: true }`: sent to the agent as the next user turn |
| `command/run` | `{ line }` | `{ queued: true }`: runs a slash command as if typed |
| `status/set` | `{ key, text? }` | `{ changed }`: keyed status text in the footer (chip `<name>.<key>`; empty text removes it). Same as the `footer/set` notification, with an answer |

- `surface` is `tui`, `classic`, `web` (`--serve`), `stdio` or `acp`. `mode` is the running mode
  (`agent`, `swarm`, `pswarm`, `interactive` at the menu, ...).
- `prompt/submit` and `command/run` queue the line as input: it runs when the session is idle (after the
  current turn), and in the TUI only while your draft is empty. The transcript shows
  `sent by extension <name>`. Not available in the classic renderer or over ACP.
- `prompt/submit` needs a running session (`/agent`, `/swarm`, `/pswarm`), max 64,000 characters, and
  cannot start with `/` (use `command/run`) or `!`.
- `command/run` takes one line: any extension command, or one of these built-ins: `/compact`,
  `/prune`, `/handoff`, `/tokens`, `/context`, `/cost`, `/diff`, `/status`, `/help`, `/retry`, `/undo`,
  `/redo`, `/effort`, `/max`, `/tag`, `/theme`, `/verbose`, `/subagentview`, `/daemonview`, `/plan`,
  `/ultra`, `/giga`, `/tools`, `/skills`, `/sessions`, `/model`, `/limits`, `/doctor`, `/review`,
  `/clear`, `/shortcuts`. Anything else (trust, sandbox, config, providers, sharing, exit) is refused.
- Errors: `-32601` unknown method, `-32602` invalid params, `1` refused (the message says why).

### Extension panels

A trusted process can open full-screen panels that Mux draws from an element tree the process sends
(`ui/open`, above). Mux renders the cached tree on every paint and never calls the process to draw;
send `ui/update` with a new tree whenever its content changes. Panels are shown in the TUI from the idle
prompt (while the draft is empty; one at a time, oldest first) and in the web UI (`--serve`, and the
desktop app) as a drawer on the right with a tab per open panel. Where nothing can draw them (classic
renderer, `--stdio`, ACP) `ui/open` returns an error, so fall back to `ui/log` or command output.
Web clients get `ext_panel` events (`op`: `open` | `update` | `close`, plus `extension`, `id`, `title`,
`view`), `GET /api/ext/panels` lists the open ones and `POST /api/ext/panels/event`
(`{ extension, id, key, type, value }`) delivers a user action.

```jsonc
{ "jsonrpc": "2.0", "id": 7, "method": "ui/open", "params": { "id": "notes", "title": "Notes", "view":
  { "type": "box", "children": [
      { "type": "input", "key": "new", "placeholder": "add a note" },
      { "type": "list", "key": "notes", "items": [ { "value": "1", "label": "buy milk", "detail": "today" } ] },
      { "type": "box", "dir": "row", "gap": 2, "children": [
          { "type": "button", "key": "clear", "label": "Clear", "hotkey": "c" },
          { "type": "progress", "value": 0.5, "label": "synced" } ] } ] } } }
```

| Element | Props |
|---|---|
| `box` | `dir` (`col` default, `row` = children side by side), `gap` (0-8), `border` (`none`, `single`, `round`), `children` |
| `text` | `text` (may contain newlines), `color`, `bold`, `dim` |
| `button` | `key` (required), `label`, `hotkey` (one letter or digit) |
| `input` | `key` (required), `placeholder`, `value` |
| `list` | `key` (required), `items` (`[{ value, label?, detail? }]`), `selected` (an item value) |
| `markdown` | `text` (rendered like agent replies) |
| `code` | `text` (monospace, no wrapping changes) |
| `progress` | `value` (0-1), `label` |

- `color` is a theme role: `text`, `muted`, `accent`, `success`, `warning`, `error`, `info`.
- Keys: letters, digits, `-`, `_` (max 64), unique within a tree. Panel ids follow the same rule; at
  most 8 open panels per extension.
- Unknown elements or props, a bad value, more than 2,000 elements, 100,000 characters of text or 32
  nested elements fail the request with an error naming the problem; the panel keeps its last tree.
- User actions arrive as notifications `ui/event` `{ id, key, type, value }`: `press` (button),
  `submit` (input; `value` = the text, the field then clears), `select` (list; `value` = the item value),
  `close` (the user pressed Esc; `key` is null). A panel the extension closes sends no `close` event.
- Keys: Tab / Shift+Tab or Up/Down move focus (Up/Down move inside a focused list), Enter
  activates, a button's hotkey presses it (outside an input), PgUp/PgDn and the wheel scroll, Esc closes.
  Mouse clicks press buttons and select list rows. Typed text, focus and list position survive `ui/update`.
- Panels close when the process exits or is stopped (`/refresh`).
- `placement` (default `screen`): `screen` = the full-screen view above; `side` = docked to the right of the
  transcript (`columns` wide, default 48, at most 45% of the terminal and always leaving 60 columns for the
  transcript); `band` = a strip above the prompt (`rows` tall, default 6, at most a quarter of the screen;
  a `col` box's `gap` rows are dropped there);
  `auto` = `side` from 144 terminal columns, else `band`. Docked panels stay up while you type and while
  the agent works, and redraw on every `ui/update`. Side panes need the frame renderer (`renderEngine:
  frame`); the inline renderer and narrow terminals show them as a band. Several docked panels share the
  spot with a tab per panel. In the web UI `side` docks the drawer beside the chat (it overlays on phones),
  `band` is a strip above the composer, and `auto` picks `side` from 1100px wide. Web events carry `placement`.
- Docked panels never take keys on their own: F6 moves the keyboard into them (F6 / Shift+F6 cycle, past
  the last one returns to the prompt), and so does clicking anywhere in a panel or on its tab (a tab click
  also switches to that panel). While focused, keys work as in the full-screen view; Esc, or a click
  outside the panel, returns to the prompt and the panel stays open (no `close` event). This works at
  the idle prompt and mid-turn (Esc then does not cancel the turn).
- In the TUI the side pane's divider is also the transcript scrollbar. Drag the divider (or the pane's first
  column) to resize the pane, and drag a band's top rule to resize the band (up to half the screen); the
  sizes hold for the session.

### Extension footer chips

A trusted process can show live text in the TUI footer by sending a notification:
`{ "jsonrpc": "2.0", "method": "footer/set", "params": { "id": "branch", "text": "main *" } }`.

- The chip id is `{extension}.{id}` (`id`: `a-z`, `0-9`, `-`, `_`, max 32). Text is one line of
  plain text, cut at 40 characters; empty text removes the chip.
- Mux caches the text and reads it when the footer paints; it never calls the process to draw.
- Without a footer template, chips show after the built-in ones and are among the first dropped on
  narrow terminals. A template can place them: `"{mode} {git.branch} {context}"`.
- A process's chips disappear when it exits or is stopped (`/refresh`).

**Sandboxing.** When a sandbox backend is configured (`sandbox.backend` other than `host`), every
extension process runs inside it, so it gets the same confinement as the agent's shell:

- The extension folder is read-only. The process works in its own writable folder,
  `<local data>/Mux-Swarm/extensions/<name>` (`%LOCALAPPDATA%` on Windows, `~/.local/share` elsewhere),
  passed as `dataDir` in `initialize`.
- Wrapper backends (`bwrap`, `firejail`, `sandbox-exec`) wrap the process. Container and microVM
  backends (`docker`, `docker-container`, `podman`, `gvisor`, ...) start one sandbox per extension: the
  data folder at `/work`, the extension folder at `/ext` (read-only), plus the usual allowed-path mounts.
  The process then runs in Linux, so the image needs its runtime (e.g. python). The sandbox is removed
  when the process stops. `custom` needs `sandbox.replStdio: true`.
- If the sandbox cannot be used, the process is not started (never a silent host fallback) and
  `/extensions` says why. `/extensions` also shows where each process runs.
- Exempt a trusted extension with `"extensions": { "unsandboxed": ["name"] }`. Only your config can do
  this; a manifest cannot opt out. With `sandbox.backend: host` nothing changes.
### Extension tools

```jsonc
// extension.json (tools)
"tools": [
  { "name": "send", "description": "Send a desktop notification.", "timeoutSeconds": 10,
    "agents": ["*"],                          // optional: "*" = every agent, or names like ["CodeAgent"]
    "parameters": { "type": "object", "properties": { "text": { "type": "string" } }, "required": ["text"] } }
]
```

- Tool names: `a-z`, `0-9`, `_`, starting with a letter (max 48). The model sees `{extension}_{name}`
  (e.g. `notify_send`), so an agent gets an extension's tools by listing the extension name in its
  `mcpServers` (exactly like an MCP server) or by a `toolPatterns` entry such as `notify_*`.
- `agents` grants the tool without touching Swarm.json: `["*"]` gives it to every agent (single agent,
  orchestrator, specialists, teams), a list of names gives it to those agents only. Omit it to rely on
  Swarm.json alone.
- Tools exist only while the trusted process runs. A session fixes its tool list when it starts, so new
  or changed tools appear in sessions started after `/refresh` (same as MCP servers).

### Tool-call interceptors

A trusted process can gate tool calls for every agent, team and daemon run:

```jsonc
"intercepts": [
  { "tool": "execute_command_async", "on": "before", "onTimeout": "deny" },        // name, or prefix ending in *
  { "tool": "repl_shell_exec", "on": "before", "timeoutMs": 2000, "onTimeout": "deny" },
  { "tool": "*", "on": "after" }                                                  // on: before | after | both
]
```

- `tool/before` request, params `{ tool, arguments, agent, parentAgent, flowId }`. Reply `result`:
  `{ "decision": "allow" }`, `{ "decision": "allow", "arguments": { ... } }` (replaces the arguments),
  or `{ "decision": "deny", "reason": "..." }` (the tool does not run; the model sees
  `[BLOCKED by extension <name>] <reason>`).
- `tool/after` request, params `{ tool, arguments, result, agent, parentAgent, flowId }`. Reply
  `result: { "result": "<text>" }` to replace what the model sees (e.g. redact secrets); any other reply
  keeps the original.
- **Caller**: `agent` is the agent whose turn made the call (the lead, `Orchestrator`, or a sub-agent's
  name); `parentAgent` and `flowId` identify the delegation or background job it runs in (as in hook
  events). Each is omitted when unknown, e.g. a tool run outside any agent turn. Use them for per-agent
  rules (only gate `ConciergeAgent`, allow everyone else); parallel sub-agents each get their own.
- No answer within `timeoutMs` (default 2000, max 30000), an `error` reply, or a dead process applies
  `onTimeout`: `allow` (default) or `deny`. Interceptors from several extensions run in name order.
- Match the real tool names: the native shell tools are `execute_command_async` and `repl_shell_exec`
  (MCP tools are `{server}_{tool}`). `/extensions` warns about patterns that match no loaded tool.

## Session Sharing (`/share`, `/join`)

Share a live session with someone on the same network. Guests see exactly what your TUI draws, in real time.

**Host**
- `/share` asks where to listen: **This machine only** (loopback, the default) or **All interfaces** (same-network
  guests). `/share --local` / `/share --lan` skip the prompt. The join link looks like
  `http://host:port/s/<room>#<secret>`; send it over a channel you trust.
- Every join asks you **Deny / Watch / Watch + type** (Deny is the default; no answer within 2 minutes = Deny).
- `/share status`, `/share control <name|#id|all> on|off`, `/share kick <name|#id>`, `/share stop`.
- **Ctrl+]** takes typing away from every guest instantly, at the prompt or mid-turn. Your own keyboard is never locked.
- Footer: `sharing · N watching · M typing`.

**Guest**
- `/join <link>` inside Mux, or `mux-swarm --join "<link>"` (cold path: no config, provider, or setup needed).
- The guest screen shows the host's frame at the host's size; resize your terminal or font to fit.
- **Ctrl+]** opens the local menu: `q` leave, `r` redraw. Ctrl+] is never sent to the host.

**Guest typing (only when the host grants it)**
- Guest keys are limited to editing keys (text, Enter, Backspace, Delete, Left/Right, Home/End, Tab, Ctrl+A/E/K/U/W).
  Host hotkeys, history, clipboard, NAV, mouse and terminal replies are dropped **on the host**.
- Guest keys go to a separate queue that only the host's idle prompt reads. No modal, picker, tool-permission prompt,
  or join approval ever receives a guest key; anything typed while the agent is working is dropped.
- A paste never submits on its own. Only a lone Enter submits.
- A line a guest typed into may only be a prompt or one of `/help /shortcuts /status /tokens /context /cost /diff
  /compact /retry /undo /redo`. `!shell`, `/exit`, `/share`, `/join`, config/provider/clipboard and all other commands
  are refused with a notice naming the guest, even if the host presses Enter. Accepted guest prompts are marked
  `↳ typed by <name>`.

**Security**
- The link is the credential: the room id routes the request, and the 32-byte secret stays in the URL fragment
  (browsers never send it) and is only used for key derivation. A new link is made on every `/share`; `/share stop`
  invalidates it. The secret is masked in everything sent to guests.
- Handshake: P-256 ECDH + HKDF-SHA256 with HMAC-SHA256 mutual proofs over the full transcript (host proves first);
  fresh keys per session. Channel: AES-256-GCM with per-direction counter nonces; tampered, replayed or reordered frames
  drop the connection.
- The guest filters host output to text, colour and cursor control; OSC (incl. clipboard writes), DCS, terminal queries
  and mode toggles are dropped.
- Limits: 4 guests, 4 pending handshakes, 10 s handshake timeout, per-IP ban after repeated failed proofs; slow guests
  are disconnected rather than stalling the host.
- Same-network only in this release (direct connect; no relay/NAT traversal).

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

Binds to loopback (`127.0.0.1`) by default, so it is reachable only from the host machine. Set `serveAddress` to `0.0.0.0` to expose it on LAN/Tailscale (`http://<host-ip>:<port>`); pair that with `serve.auth` and your own perimeter.

Features: streaming responses, markdown rendering, plan mode prompts, live diffs panel, theme engine (17 presets plus extension web themes, see Customizing the web UI), file browser, drag-drop upload, voice input, auto-reconnect. Single static HTML file, zero dependencies.

Combine with `--daemon` for background triggers alongside the web UI. Combine with `--register` for persistence across reboots.

- `POST /api/voice/transcribe`: raw audio body (`audio/wav`; `webm`/`ogg`/`mpeg` need ffmpeg on PATH, max 25 MB / 5 min) -> `{ text, ms, model }` via the local whisper.cpp assets from `/voice`. Non-speech annotations are filtered and silence returns `""`; `429` when a transcription is already running, `503` when the assets are not installed. Same `serve.auth` gate as `/api`.

### In-app editor & auth

The web UI includes a bundled **Monaco** code editor (offline, syntax highlighting
for ~90 languages, theme-aware). Right-click a sandbox file → **Open in editor**.
Saving requires `serve.editable: true` (see [serve](#serve)); otherwise the editor
is read-only.

Optional app-level auth is configured via `serve.auth` (see [serve](#serve)). The
web app prompts for the token, stores it in `sessionStorage`, and attaches it to all
API/WS calls. App auth complements (does not replace) an nginx perimeter.

### Desktop app (Mux-Swarm Desktop)

A native window for the same web UI: one Tauri v2 shell for Windows (WebView2), macOS (WKWebView) and Linux
(WebKitGTK). It uses your existing engine install (`muxExe` in its config, then `MUX_EXE`, the per-OS install
dir, then `PATH`); if none is found the loading screen offers **Install Mux-Swarm**. It attaches to a serve
already answering on its port (default 6723), otherwise it spawns `MuxSwarm --serve <port>` and stops that engine
when the window closes. Remote mode (`url`) opens a hosted Mux instead.

- Installers ship with the releases: `Mux-Swarm-Desktop_<ver>_x64-setup.exe` / `.msi` (Windows, per-user,
  installs WebView2 if missing), `.dmg` (macOS universal), `.AppImage` / `.deb` (Linux, needs webkit2gtk 4.1).
- The builds are not code-signed yet. Trust them once on first open: Windows SmartScreen **More info > Run
  anyway**; macOS **System Settings > Privacy & Security > Open Anyway** (macOS 15+); Linux needs nothing.
- Native extras through `window.muxHost`: window controls in the header, microphone capture (sent to
  `/api/voice/transcribe`), dark/light window chrome, notifications, links in your browser, **Open with
  default app** on file chips. The loading screen uses the colors of the last theme the web UI reported
  (`theme.json` in the app config dir; delete it to reset).
- Shell config: `<app config dir>/config.json` (`muxExe`, `port`, `url`, `trustRemoteOrigin`, `extraArgs`,
  `keepServerOnClose`, `startupTimeoutSecs`). Bridge contract for other shells: `docs/desktop.md`.

## Subscription Auth (CLIProxy sidecar)

Besides plain API-key providers, Mux can authenticate with subscription accounts
(Claude Max/Pro, ChatGPT/Codex, Kimi, etc.) through a bundled **CLIProxyAPI** sidecar -
a small MIT loopback proxy that holds the OAuth tokens and exposes a normal
OpenAI-compatible endpoint Mux talks to unchanged.

- `/login [provider]` - run the provider's OAuth flow (browser opens automatically).
  `/login <provider> headless` (also offered as a choice, default over SSH / without a display) prints the
  auth URL on its own line instead, to open in a browser on any machine: press `c` to copy it (OSC 52, plus
  the local clipboard when not over SSH), `Esc` to cancel.
  Providers: `claude`, `codex`, `codex-device` (headless device-code), `kimi`, `xai`,
  `antigravity`. The first successful login auto-registers a single `cliproxy` provider
  (endpoint `http://127.0.0.1:<port>/v1`, bearer via the `MUX_CLIPROXY_KEY` env var);
  the proxy then routes by model id, so later logins add nothing.
- `/ping [provider]` - ensure the sidecar is up and report per-provider login readiness.
- `/proxy status` - pinned version, binary presence, running state + endpoint, auth state.
- `/proxy update` - install the LATEST upstream CLIProxyAPI release side-by-side
  (`cliproxy/<ver>/`), record it in `cliproxy/active-version.txt`, and cold-restart a running
  sidecar onto it (including an orphan left by an earlier Mux). Newer releases support newer
  models. Trust model: the download is verified against that release's own `checksums.txt`
  (integrity in transit), not against hashes compiled into Mux. The override only applies while it
  is newer than the build's pin: a later Mux whose pinned version is the same or newer takes precedence.
- `/proxy update pinned` - drop the override and reinstall this build's pinned version, verified
  against the SHA256 table compiled into Mux.
- `/proxy restart` - cold-restart the sidecar (re-reads the auth dir, reclaims the port).
- `MUX_CLIPROXY_HOME` (env) - relocates the whole proxy root (binaries, config, keys, auth); used by
  `--selftest proxy` so tests never touch the live sidecar.

The sidecar is downloaded on first use into `%LOCALAPPDATA%/Mux-Swarm/cliproxy/<ver>/`
(SHA256-verified), runs detached so it survives Mux exit, and is re-adopted by port on the
next launch (no respawn). After login, activate the provider with `/provider`, set the
agent model to a real id (e.g. `claude-opus-4-6`, `gpt-5-codex`), and chat.

## ACP Transport (Zed Agent Client Protocol)

`mux-swarm --acp` exposes Mux as an ACP agent over stdio (JSON-RPC 2.0, newline-delimited;
stdout is pure protocol, logs go to stderr). This lets ACP-capable editors/clients (Zed,
GitHub Copilot CLI agent panel, the Intelligent Terminal) drive a Mux single-agent session.

- Supports: `initialize`, `session/new`, `session/list` (thread history: id = Sessions
  folder name, absolute cwd from an `acp.muxcwd` sidecar, title, updatedAt), `session/load` (full replay incl. reasoning + tool calls ->
  context resume), `session/prompt` (text + resource_link + embedded text), `session/cancel`,
  `session/set_mode`, `session/resume`, model selection (both the canonical
  `configOptions` and the Zed `models` / `session/set_model` mechanisms), `logout`.
- Streams: `agent_message_chunk`, `agent_thought_chunk` (reasoning), `tool_call` +
  `tool_call_update`, `plan`, `usage_update`; stop reasons `end_turn` / `cancelled`.
  Tool calls are announced when invoked (readable title, `rawInput`, `locations`), keyed by
  the provider call id, finished `completed`/`failed`; edits carry a real old/new `diff`;
  calls left open at turn end or cancel are closed as `failed` before the prompt response.
- Stdio NDJSON: the single-agent `tool_call` event carries `callId` + `args` (JSON string)
  and `tool_result` carries `callId` + `isError` (additive fields).
- `ask_user` under ACP: confirm/select/multi_select -> `session/request_permission` on the
  same tool call; free-text asks tell the model to ask in its reply (never reads stdin).
- Filesystem + terminal execution and tool permission gating deliberately stay inside Mux's
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

### Language servers (`lsp`)

Off by default. Point Mux at any language server that speaks LSP over stdio; there is no built-in
server list, so any language works the same way:

```jsonc
"lsp": {
  "enabled": true,
  "servers": {
    "python": { "command": "basedpyright-langserver", "args": ["--stdio"], "extensions": [".py"],
                "rootMarkers": ["pyproject.toml", "setup.py", ".git"] },
    "csharp": { "command": "csharp-ls", "extensions": [".cs"], "rootMarkers": ["*.sln", "*.csproj"] },
    "zig":    { "command": "zls", "extensions": [".zig"], "rootMarkers": ["build.zig"] }
  }
}
```

- Per server: `command` (PATH name or full path; on Windows `.exe`/`.cmd`/`.bat` are tried), `args`,
  `env`, `extensions` (required), `languageIds` (extension -> LSP language id when it is not the
  extension itself, e.g. `{ ".cs": "csharp" }`), `rootMarkers` (names or `*.ext` patterns searched upward
  from the file; none found = the workspace root), `initializationOptions`, `settings` (answers
  `workspace/configuration`), `startupTimeoutMs` (default 30000), `disabled`. Top level: `idleMinutes`
  (stop idle servers, default 10, 0 = never).
- A server starts the first time a file it covers is used, once per (server, project root). One that
  fails to start is not retried until `/refresh`, which also restarts servers with the new config.
  Mux only ever stops servers it started.
- Files are synced from disk (full text, increasing version) before every question, so edits made by
  any tool or by you are seen. Diagnostics that do not arrive in time are reported as **pending**,
  never as "no problems".
- Agents get the native **`lsp`** tool by listing `"Lsp"` in `mcpServers` (Swarm.json), like
  `Filesystem` and `Shell`; it only exists while `lsp.enabled` is on with a server configured.
  Read-only actions: `diagnostics`, `definition`, `references`, `hover`, `symbols` (a file's outline, or
  a workspace search with `query`), `status`. Code is addressed by a 1-based `line` plus the `symbol`
  text on it (`name#2` = its second occurrence on the line); Mux finds the column. Results are
  `path:line:col` with the source line, errors first, capped (50 diagnostics, 50 references, 200 symbols).
- **Diagnostics after edits** (`lsp.diagnosticsOnWrite`, default true while `lsp` is on): when a tool
  call changes files a configured server covers, an `[LSP]` block with their errors is appended to that
  tool's result, whichever tool made the change: the file tools, `execute_command_async`,
  `repl_shell_exec` or an MCP server (changes are seen by the same file watcher that drives
  `file_changed`, which LSP starts in any mode). Errors only, up to 5 files and 20 errors each, plus
  errors the edit caused in other open files; `no errors in ...` when clean; `diagnostics pending` when the
  server has not finished checking within `lsp.diagnosticsWaitMs` (default 1500). A change that lands
  while no tool call is running (a background job) is reported on the next tool result. When several
  agents have calls running at once, each of their results may report the same change.

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

On an interactive start with hooks configured, Mux warns and asks "Proceed with hooks enabled?" (no = hooks muted for
the session; `/hooks on` re-enables). To skip the warning and prompt and keep hooks enabled, set in config.json:

```json
"console": { "suppressActionWarnings": true }
```

Default `false`. It only affects startup warnings; in-session prompts (security approvals, ask_user, delete/overwrite
confirms) still show.

`/createhook` (or `/hooks create`) asks for the id, script, events, filters and mode, saves the entry, and, for a new
script, starts a normal `/agent` session (your model and settings) whose first message is a brief with those choices.

- `when.event` is one event name or a list: `"event": ["turn_end", "task_complete"]` fires once per occurrence of any listed event. A single event still round-trips as a plain string.
- `args` (optional string array): `command` becomes the executable and each entry is passed verbatim, so paths with spaces need no quoting: `"command": "python", "args": ["C:/My Hooks/notify.py"]`. Without `args`, `command` is split once at the first space (legacy; a quoted path with spaces in the executable position breaks).
- Hook stdout/stderr are captured, not inherited (stdout is the protocol under `--stdio`/`--acp`, and would draw over the TUI). Each line is surfaced as a muted/debug line `[Hook:<id>] ...` (`[Hook:<id>:stderr] ...`).
- A crashed persistent hook is restarted on its next event; repeated crashes back off 1s, 2s, 4s ... up to 60s (events for it are skipped meanwhile). Staying up 60s resets the backoff.
- Payload: `tool_call` carries `args` (call arguments as JSON), `tool_result` carries `tool` and the result text in `summary`, and every event carries `goalId` when the run has one (`--goal-id`, or the id a continuous run generates).

Every event carries `event` and `timestamp`, plus `goalId` when the run has one; the other fields
depend on the event (absent = not set). These are the exact names emitted, for hooks
(`when.event`) and extension processes (`events` in the manifest, delivered as `event` notifications
with this payload as `params`):

| Event | When it fires | Fields |
|-------|---------------|--------|
| `runtime_ready` | Runtime finished booting | `text` |
| `session_start` | A mode was entered | `agent`, `summary` (`agent`/`stateless`/`swarm`/`pswarm`), `text` (opening goal, if any); `/agent`: `data.model`, `data.resumed` |
| `session_end` | A session exited, on every path (`/qc`, Esc/Ctrl+C, error) | `agent`, `summary` (`complete`/`interrupted`/`error`) |
| `user_input` | A goal or message was received | `agent`, `text` |
| `agent_turn_start` | An agent begins a turn | `agent` |
| `text_chunk` | A streamed piece of the answer | `agent`, `text` |
| `thinking_chunk` | A streamed piece of reasoning | `agent`, `text` |
| `tool_call` | A tool is invoked | `agent`, `tool`, `args` (arguments as JSON) |
| `tool_result` | A tool returned | `agent`, `tool`, `summary` (result text) |
| `tool_denied` | An extension interceptor blocked a call (after its `tool_call`; the tool never ran) | `summary` = tool; `data.tool`, `data.by` (extension), `data.reason` |
| `turn_end` | An agent finished one model round-trip (several per user turn) | `agent`, `summary` (first 500 chars of the answer) |
| `turn_complete` | A whole user turn / goal ended, once, on every path | `agent`, `summary` = `data.outcome`: `ok`/`interrupted`/`timeout`/`failed`; `data.durationMs`, `data.toolCalls` (`/agent`), `data.error` |
| `task_complete` | An agent signalled the task is done (or failed/partial) | `agent`, `summary`; `data.status` (`success`/`failure`/`partial`) |
| `delegation` | Work was delegated to a specialist | `agent` (delegating agent), `summary` (task, truncated), `flowId`; `data.to` |
| `delegation_end` | That delegation finished, on every path | `agent` (delegating agent), `summary` = `data.status`, `flowId` (same as its `delegation`); `data.to`, `data.status` (`success`/`failure`/`partial`/`cancelled`/`error`), `data.durationMs`, `data.attempt` |
| `job_start` | A background job (`/bg`, background delegation) started | `agent`, `summary` = id; `data.id`, `data.agent` |
| `job_end` | That job finished | `agent`, `summary` = `data.status` (`done`/`failed`/`cancelled`); `data.id`, `data.agent`, `data.durationMs` |
| `command_executed` | A slash command was entered (before it runs), at the menu or in a session | `agent` (session lead), `summary` = command; `data.command`, `data.scope` (`menu`/`agent`/`swarm`/`pswarm`), `data.args` (omitted for `/set` `/config` `/login` `/provider` `/join` `/share` `/setkey` `/apikey`) |
| `compaction` | Context compaction (`/agent`) started, finished or failed | `agent`, `summary` = `data.phase` (`start`/`done`/`failed`); `data.kind` (`auto`/`manual`/`mid_turn`/`resume`), `data.beforeTokens`, `data.afterTokens` + `data.durationMs` (done) |
| `mcp_server_connected` | An MCP server connected (startup and `/refresh`) | `summary` = server; `data.server`, `data.transport` (`stdio`/`http`), `data.toolCount` |
| `mcp_server_failed` | An MCP server failed to connect | `summary` = server; `data.server`, `data.error` (max 500 chars) |
| `model_changed` | A model was changed (`/setmodel`, model picker) | `agent` (slot label), `summary` = new model; `data.from`, `data.to`, `data.slot` (picker) |
| `provider_changed` | `/provider` switched the provider | `summary` = new provider; `data.from`, `data.to` (names only, never keys) |
| `effort_changed` | Reasoning effort changed (`/effort`, `/max`, Shift+Tab) | `summary` = new effort; `data.from`, `data.to` |
| `theme_changed` | `/theme` applied a theme | `summary` = new theme; `data.from`, `data.to` |
| `extension_process_exited` | A trusted extension process exited on its own (not on `/refresh` or exit) | `summary` = extension; `data.extension`, `data.exitCode` |
| `daemon_start` | The daemon started | `summary` (`<n> triggers`) |
| `daemon_stop` | The daemon stopped | (none) |
| `daemon_trigger` | A trigger fired a goal | `agent`, `summary` (`cron:<id>`/`watch:<id>`/`webhook:<id>`), `text` (goal) |
| `daemon_status` | A status check changed state | `summary` (`recovered:`/`unhealthy:`/`restarted:`/`restart_failed:<id>`), `text` |
| `daemon_bridge` | A bridge process started or exited | `summary` (`started:`/`exited:<id>`), `text` |

**Correlation.** `agent` is the agent whose turn produced the event; events emitted without one during
an agent's turn get that agent. Events from a delegated sub-agent or a background job carry `flowId` (the delegation's
id, e.g. `d7`, or the job id, e.g. `bg2`) and `parentAgent` (the agent that delegated), so events from
parallel sub-agents can be told apart; `delegation` and `delegation_end` carry the same `flowId`.
`data` holds event-specific fields (ids, names, counts, durations), absent when unset.

**What hooks and extensions can see.** `tool_call.args` and `tool_result.summary` carry the full tool
arguments and results, unredacted (shell commands, file contents, anything the agent passes a tool).
Only configure hooks and trust extensions you would trust with that. Lifecycle events added since
(`command_executed`, `model_changed`, ...) carry names, ids and counts only.

`*` (extensions) matches every event. `text_chunk` and `thinking_chunk` fire per streamed piece, so
subscribe to them only when you need streaming.

Daemon-specific hook events emitted automatically:
- `daemon_start` -- fired when daemon starts with trigger count
- `daemon_stop` -- fired on daemon shutdown
- `daemon_trigger` -- fired when a watch or cron trigger activates (summary includes trigger type and id)
- `daemon_status` -- fired on health check state changes (recovered, unhealthy, restarted, restart_failed)
- `daemon_bridge` -- fired on bridge process start and exit

## Security Recommendations

- Keep `allowedPaths` minimal and purpose-specific
- Enable `--mcp-strict` (default is non-strict since v0.14.1) so startup fails if required integrations are unavailable
- Use environment variables for all credentials, never put secrets in config files
- Scope MCP servers narrowly per agent role via `mcpServers` in swarm.json
- Route execution-heavy tasks through Docker when possible (`/dockerexec`)
- Review hook commands before confirming on startup (hooks run with user permissions); `console.suppressActionWarnings` skips that check, so only set it once you trust your hooks
- Scope daemon watch paths narrowly
- Use `--register` from an elevated terminal only after validating daemon and hook config
- For maximum isolation, run mux-swarm inside a Docker container with only necessary volumes mounted