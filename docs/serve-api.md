# Web UI & Serve API Reference

The `--serve` flag starts an embedded web interface and an HTTP/WebSocket API alongside the
normal agent runtime. This page covers the web UI, the `/api` route surface, the `/ws` NDJSON
protocol, OS service registration, scoped instances, and user identity.

---

## 1. Web UI (`--serve`)

MuxSwarm initializes as usual (config, providers, MCP servers, skills), then starts a Kestrel
HTTP server that bridges the browser to the agent loop over a WebSocket.

```bash
mux-swarm --serve           # default port 6723
mux-swarm --serve 8080      # custom port
```

The browser connects via WebSocket and receives the same NDJSON event stream that `--stdio`
emits. User input flows back through the socket to the agent's input loop. No proxy, no
subprocess, no second process. The web UI is a single `index.html` served from
`Runtime/mux-web-app/`.

Features:

- Streaming agent responses with markdown rendering
- Interactive plan mode prompts (confirm, select, text, multi-select)
- Live Diffs panel with syntax-highlighted tool results in real time
- Theme engine with presets (Zinc, Light, Ocean, Matrix) and custom color pickers
- File browser sidebar for sandbox and session directories
- File upload via drag-drop, file picker, or clipboard paste (Ctrl+V)
- Tool call activity with friendly action descriptions
- Loading skeleton, animated toasts, hover timestamps, character counter
- Cancel active agent turns via Stop button or Escape key
- Auto-reconnect on mobile with manual reconnect button
- Accessible on LAN and Tailscale (binds to all interfaces)
- Voice input via browser speech-to-text for hands-free interaction
- **Native config editor** (Monaco): edit `Config.json` / `Swarm.json` in-browser with
  server-side JSON validation, gated by `serve.configExposed` (default off)
- **One-click self-update** and native server restart from the Settings panel
- Zero dependencies: single static HTML file, no build step, no npm

The terminal continues to show the splash screen and MCP initialization progress while the
browser receives only agent interaction events. Combine with `--watchdog` for process-level
resilience or `--register` to persist as an OS service that survives reboots.

---

## 2. API Surface

All routes are served from the same Kestrel host as the web UI (`ServeMode.cs`).

| Route | Purpose | Gating |
|-------|---------|--------|
| `/ws` | NDJSON WebSocket: full event stream + user input | `serve.auth` bearer (when enabled) |
| `/api/health` | Health probe | `serve.auth` |
| `/api/status` | Runtime status | `serve.auth` |
| `/api/agents` | List configured agents | `serve.auth` |
| `/api/sessions` | List session metadata | `serve.auth` |
| `/api/sessions/{id}` | Single session metadata | `serve.auth` |
| `/api/commands` | Available slash commands | `serve.auth` |
| `/api/config` | Runtime config view | `serve.auth` + `serve.configExposed` |
| `/api/config-files/{which}` | Read/edit `Config.json` / `Swarm.json` (Monaco editor backend) | `serve.auth` + `serve.configExposed` |
| `/api/skills` | List installed skills | `serve.auth` |
| `/api/list/{type}[/{path}]` | List files (sandbox/session trees) | `serve.auth` |
| `/api/read/{type}/{path}` | Read a file | `serve.auth` |
| `/api/save/{type}/{path}` | Save a file | `serve.auth` + `serve.editable` |
| `/api/download/{type}/{path}` | Download a file | `serve.auth` |
| `/api/upload` | Upload a file | `serve.auth` |
| `/api/fs` | Filesystem CRUD operations | `serve.auth` + `serve.editable` for writes |
| `/api/update` | `POST`: read-only self-update plan (what would change) | `serve.auth` |
| `/api/restart` | Restart the server process | `serve.auth` |
| `/api/shutdown` | Shut down the server process | `serve.auth` |
| `/api/hook/{id}` | `POST`: inbound webhook, fires the matching daemon trigger | Per-trigger HMAC/bearer (see below) |

### Auth (`serve.auth`)

Opt-in bearer auth for `/api` and `/ws`, configured in `config.json`:

```json
{
  "serve": {
    "editable": false,
    "configExposed": false,
    "auth": { "enabled": true, "token": "...", "scheme": "Bearer" },
    "editor": { "autoFetch": true, "version": "..." }
  }
}
```

- `auth.enabled` (default off): when on, every `/api` route and the `/ws` upgrade require the
  bearer token.
- `editable` (default off): gates all write operations through the editor and `/api/save` /
  `/api/fs` writes.
- `configExposed` (default off): gates config visibility and the in-browser config editor
  (`/api/config`, `/api/config-files/{which}`).

**Exception:** `/api/hook/{id}` is excluded from the bearer middleware. Inbound webhooks
authenticate per-trigger instead, via HMAC (`X-Hub-Signature-256`) or a per-trigger bearer
secret, plus a `payloadLimit` size cap. See [Hooks, Webhooks & the Daemon](hooks.md) for the
inbound-webhook trust model.

---

## 3. WebSocket Protocol (`/ws`)

The `/ws` endpoint carries newline-delimited JSON (NDJSON), one event object per line: the
exact same stream `--stdio` writes to stdout. Every runtime emission (agent text deltas, tool
calls, tool results, delegation events, daemon activity, completion frames) arrives as a typed
JSON line. User input is sent back over the same socket and is fed into the agent's input loop.

Practical notes:

- The stream is shared: multiple consumers (browser tabs, bridges) see the same events.
- Bridges (Telegram, Discord, Signal) are just WebSocket clients: any script that reads
  `MUX_WS_URL` and connects to `/ws` can serve as a bridge.
- When `serve.auth` is enabled the WebSocket upgrade requires the same bearer token as `/api`.

---

## 4. Daemon Mode (`--daemon`)

The daemon runs background trigger loops (watch, cron, status, bridge, webhook) alongside the
interactive loop and web UI. It is documented in full in
[Hooks, Webhooks & the Daemon](hooks.md); a common serve stack looks like:

```bash
mux-swarm --serve --daemon                        # web UI + daemon triggers
mux-swarm --serve --daemon --watchdog             # temporary always-on stack
mux-swarm --serve --daemon --watchdog --register  # system-level always-on stack
```

Mux-Swarm ships with three bridges under `Runtime/`:

| Bridge | Script | Token Env Var | Additional Env |
|--------|--------|---------------|----------------|
| Telegram | `telegram_bridge.py` | `TELEGRAM_BOT_TOKEN` | `WHISPER_MODEL`, `ALLOWED_CHAT_IDS` |
| Discord | `discord_bridge.py` | `DISCORD_BOT_TOKEN` | `WHISPER_MODEL`, `DISCORD_CHANNEL_ID` |
| Signal | `signal_bridge.py` | `SIGNAL_NUMBER` | `SIGNAL_API_URL`, `WHISPER_MODEL`, `ALLOWED_NUMBERS` |

Bridge triggers accept `command`, `args`, and an optional `env` block. The runtime auto-injects
`MUX_WS_URL` with the correct serve port if not explicitly set. Bot tokens and other secrets
should be set as environment variables in your shell, not in config. All bridges support text
messaging and audio transcription via local Whisper; FFmpeg is resolved automatically via the
`static-ffmpeg` package. Bridge dependencies are managed by the `pyproject.toml` in `Runtime/`.

---

## 5. OS Service Registration (`--register` / `--remove`)

Register mux-swarm as a system service that starts automatically on boot. One command, no
manual file editing.

```bash
# Register (run elevated on Windows)
mux-swarm --register --serve --daemon --watchdog

# Remove
mux-swarm --remove
```

The `--register` flag is stripped from the service definition - only runtime flags (`--serve`,
`--daemon`, `--watchdog`) are forwarded. The binary path and working directory are resolved
automatically from the install location (not from shell aliases).

| Platform | Mechanism | Details |
|----------|-----------|---------|
| **Windows** | Task Scheduler (XML) | Boot trigger with 30s delay, `RestartOnFailure` (60s interval, 999 retries), runs before user login, `WorkingDirectory` set |
| **Linux** | systemd user service | `Restart=always`, `RestartSec=10`, `enable-linger` for headless boot (starts before login) |
| **macOS** | launchd LaunchAgent | `RunAtLoad`, `KeepAlive`, logs to `~/.local/share/Mux-Swarm/Logs/` |

Combined with `--watchdog` (process-level restart) and daemon status triggers (subsystem-level
restart), this creates a three-layer resilience stack: the OS ensures the process starts, the
watchdog ensures it stays running, and the daemon ensures internal subsystems are healthy.

---

## 6. Scoped Instances

The `--cfg` and `--swarmcfg` flags allow fully isolated runtime instances from a single
installation. Each instance resolves its own provider, MCP servers, filesystem boundaries,
storage paths, and user identity from its config files.

```bash
# User-scoped instances
mux-swarm --cfg /path/to/alice/Config.json --swarmcfg /path/to/alice/Swarm.json
mux-swarm --cfg /path/to/bob/Config.json --swarmcfg /path/to/bob/Swarm.json

# Environment-scoped instances
mux-swarm --cfg /etc/mux/production/Config.json --swarmcfg /etc/mux/production/Swarm.json
```

This enables multi-user deployments, per-environment configurations, and integration into
larger systems where each consumer needs an isolated agent runtime.

---

## 7. User Identity (`userInfo`)

An optional `userInfo` block in `config.json` injects user context into every agent's preamble.
Agents receive the user's name, role, and any freeform context - adapting behavior without
prompt changes.

All fields except `name` are optional. The `info` field is freeform and can carry preferences,
domain context, or behavioral directives (e.g. `"Strict compliance mode. All outputs must
reference internal policy docs."`).

---
[Back to docs index](README.md) | [Main README](../README.md)
