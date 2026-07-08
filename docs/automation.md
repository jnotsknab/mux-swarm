# Automation and Integration

Ways to embed and drive Mux-Swarm from other systems: the CLI, machine-readable output, the HTTP and WebSocket API, webhooks, the daemon, editor integration, messaging bridges, and the Python SDK.

## CLI and scripting

The simplest integration is the CLI. A one-shot goal runs to completion and exits:

```bash
mux-swarm --goal "Summarize the latest sales CSV in my sandbox and write a report"
```

Useful flags for scripted use:

| Flag | Purpose |
|------|---------|
| `--goal <text\|file>` | Run a single goal, then exit. Accepts inline text or a path to a goal file. |
| `--goal-id <id>` | Attach a persistent session id so a later run can resume the same context. |
| `--agent <name>` | Route the goal to a specific agent. |
| `--parallel` | Decompose into independent subtasks and run them concurrently. |
| `--report [session-id]` | Generate an audit report for a session (or all sessions) and exit. |
| `--stdio` | Machine-readable NDJSON output, no ANSI. See below. |

See the [CLI reference](cli.md) for the full flag list and [Workflows](workflows.md) for scripting multi-step pipelines from a single file.

## Machine-readable output (`--stdio`)

`--stdio` switches the process to a newline-delimited JSON (NDJSON) event stream on stdout with no ANSI decoration, suitable for consuming programmatically. Interactive hook prompts are suppressed in this mode.

```bash
echo "Audit the config files in my sandbox" | mux-swarm --stdio --agent CodeAgent
```

Each line is one JSON event (stream chunks, tool calls, tool results, status, completion). Read the stream line by line and parse each line as JSON. An out-of-band `__CANCEL__` line written to stdin requests graceful cancellation of the current turn, which is how piped and embedded integrations stop a run cleanly.

## HTTP and WebSocket API (`--serve`)

`mux-swarm --serve [port]` (default 6723) exposes an embedded web UI plus an HTTP and WebSocket API. This is the richest integration surface for another application:

- `GET /api/health`, `GET /api/status`, `GET /api/agents`, `GET /api/sessions` for state.
- `POST` command and file routes for driving sessions and reading/writing workspace files.
- `/ws` is a bidirectional NDJSON WebSocket carrying the same event stream as `--stdio`, plus user input.
- Optional bearer auth (`serve.auth`) gates `/api` and `/ws`.

See [Serve and API](serve-api.md) for the full route list and the WebSocket protocol.

## Webhooks (inbound and outbound)

Mux-Swarm both receives and emits webhooks:

- **Inbound:** a daemon trigger of `type: "webhook"` is exposed at `POST /api/hook/{id}`. An external system POSTs a payload to start a run. The route supports per-trigger HMAC signature verification or bearer auth and a payload size limit.
- **Outbound:** `webhooks[]` sinks in `swarm.json` POST a signed JSON envelope to your URL when matched events fire (fire-and-forget with bounded retry, GitHub-style HMAC-SHA256 signing when a secret is set).

This makes Mux-Swarm easy to wire into CI systems, chat platforms, or an event bus in both directions. See [Hooks, Webhooks, and Daemon](hooks.md) for setup and event names.

## Daemon and scheduling

`mux-swarm --daemon` runs background trigger loops defined in `config.json`: `cron` (schedule), `watch` (file changes), `status` (HTTP/process/TCP health with auto-restart), `interval`, and `webhook`. Combined with OS service registration (`--register` / `--remove`) this gives you an always-on automation host. See [Hooks, Webhooks, and Daemon](hooks.md).

## Editor integration (ACP)

`mux-swarm --acp` speaks the Zed Agent Client Protocol over stdio (JSON-RPC), letting a compatible editor drive Mux-Swarm as an agent backend with model selection and session management. See [ACP and Editor Integration](acp.md).

## Messaging bridges

Bundled Telegram, Discord, and Signal bridges let you drive a swarm from a chat client, with voice transcription and authorization. Configure them through the [Setup Guide](setup-guide.md).

## Python SDK (coming soon)

A first-party Python SDK (`muxswarm`) is in active development. It provides a typed mapping over the engine's event, config, and tool contracts plus a drop-in client, and auto-downloads the engine binary at runtime. The first release is pending; this section will link to it on publish.

## Example: inbound webhook to outbound notification

A common pattern is: an external event triggers a run, and the result is pushed somewhere else.

1. Define an inbound `webhook` daemon trigger in `config.json` that starts a goal when `POST /api/hook/deploy-review` is called.
2. Define an outbound `webhooks[]` sink in `swarm.json` that posts to your Slack incoming-webhook URL on the completion event.
3. Run `mux-swarm --serve --daemon`.

Now a CI system POSTing to `/api/hook/deploy-review` kicks off an agent run, and the summary is delivered to Slack when it finishes, with no polling on either side.

---
[Back to docs index](README.md) | [Main README](../README.md)
