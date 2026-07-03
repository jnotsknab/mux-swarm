# Hooks, Webhooks & the Daemon — Background & Event Integration

Mux-Swarm has four related mechanisms for reacting to events and running work outside the
interactive loop. They share plumbing but serve different purposes:

| Mechanism | Direction | Trigger | Effect | Config |
|-----------|-----------|---------|--------|--------|
| **Hooks** | internal | a lifecycle event fires | run an external command | `swarm.json` `hooks[]` |
| **Outbound webhooks** | Mux → world | a stream event fires | POST a signed JSON envelope to a URL | `swarm.json` `webhooks[]` |
| **Inbound webhooks** | world → Mux | an external HTTP POST | fire an agent goal | `config.json` `daemon.triggers[]` (type `webhook`) |
| **Daemon** | internal | cron / file-watch / status / bridge / webhook | fire goals, supervise processes | `config.json` `daemon` |

> **New to webhooks?** A webhook is just an ordinary HTTP POST where the roles are swapped:
> instead of *you* calling an API, the other system calls a URL you gave it the moment something
> happens. **Outbound** = Mux is the caller. **Inbound** = Mux is the receiver.

---

## 1. Event Hooks

Hooks run an external command in response to a runtime lifecycle event. Configure them in
`swarm.json` under `hooks[]`; each fires when its `when` clause matches.

```json
{
  "hooks": [
    { "id": "notify-slack", "mode": "async", "command": "python scripts/notify.py",
      "when": { "event": "task_complete" } },
    { "id": "voice-out", "mode": "async", "persistent": true, "command": "python scripts/tts.py",
      "when": { "event": "text_chunk" } },
    { "id": "audit-tools", "mode": "blocking", "command": "bash scripts/audit.sh", "timeoutSeconds": 10,
      "when": { "event": "tool_call", "agent": "CodeAgent" } }
  ]
}
```

The hook process receives the full event as **one JSON line on stdin** (camelCase: `event`,
`agent`, `tool`, `text`, `args`, `summary`, `goalId`, `timestamp`).

**Dispatch modes**
- `async` — fire and continue immediately (default).
- `blocking` — wait for the process to exit, up to `timeoutSeconds` (default 30).
- `persistent: true` — one long-lived process fed NDJSON events on stdin for the whole session
  (ideal for stateful consumers: TTS, live dashboards).

**Matching** — filter by `event` (required), plus optional `agent` and `tool`.

Hooks execute arbitrary commands with your user permissions, so on startup the runtime prompts for
confirmation before enabling them. Toggle live with `/hooks on|off`; scaffold one with
`/createhook`. Hooks fire in **all** transports (TUI, serve, stdio) — they are independent of the
console output mode.

### Lifecycle events (what `hooks[].when.event` can match)

| Event | When it fires |
|-------|---------------|
| `runtime_ready` | Runtime finished booting |
| `session_start` | A mode was entered (agent/stateless/swarm/pswarm) |
| `session_end` | Session exited (completed or interrupted) |
| `user_input` | A goal / message was received |
| `agent_turn_start` | An agent begins a turn |
| `text_chunk` | A streaming token of the agent's answer |
| `thinking_chunk` | A streaming token of reasoning |
| `tool_call` | A tool is invoked |
| `tool_result` | A tool returned |
| `turn_end` | An agent turn completed |
| `task_complete` | An agent signalled the task is done |
| `delegation` | The orchestrator delegated to a specialist (multi/parallel) |
| `daemon_start` / `daemon_stop` | The daemon started / stopped |
| `daemon_trigger` | A daemon trigger fired a goal |
| `daemon_status` | A status-check trigger ran |
| `daemon_bridge` | A bridge process event |

---

## 2. Outbound Webhooks (Mux → external)

An outbound webhook makes Mux **POST a signed JSON envelope to an external URL** whenever a matching
event fires — Slack/Discord pings, CI chaining, observability sinks, cross-instance fan-out — with no
per-platform integration code. Configure in `swarm.json` under `webhooks[]`.

```json
{
  "webhooks": [
    {
      "url": "https://hooks.slack.com/services/T000/B000/xxxx",
      "events": ["task_complete", "error"],
      "secret": "optional-hmac-secret",
      "headers": { "X-Env": "prod" }
    }
  ]
}
```

| Field | Meaning |
|-------|---------|
| `url` | Target that receives the POST (required) |
| `events` | Allowlist of event names; `"*"` = everything (required, non-empty) |
| `secret` | Optional HMAC secret → each POST carries `X-Hub-Signature-256: sha256=<hex>` over the body |
| `headers` | Optional static headers (auth tokens, routing) |

The POST body is `{ "event": "<type>", "timestamp": "...", ...fields }`. Delivery is
**fire-and-forget** with bounded retry (3 attempts, exponential backoff); it retries `5xx`/network
errors and gives up on `4xx`. A slow or dead receiver never blocks an agent turn. The subsystem is
**inert until you add a sink** (`webhooks[]` empty ⇒ zero overhead).

Scaffold one with `/createhook` → *Outbound webhook*.

### Which events can I subscribe to?

Outbound webhooks tap the **render/stream** event bus. You may use the same **lifecycle names as
hooks** (they are aliased — see below) or the stream names directly. Practical guidance: subscribe to
**coarse** events (`task_complete`, `error`, `agent_turn_end`, `hook_fired`, `delegation`), not
`stream` — `stream` fires per token and will flood a chat receiver.

**Stream event names (canonical):**

`agent_turn_start`, `agent_turn_end`, `stream`, `stream_end`, `thinking_start`, `thinking_update`,
`thinking_end`, `tool_call`, `tool_result`, `delegation`, `delegation_compacted`, `task_start`,
`task_done`, `task_complete`, `step`, `rule`, `success`, `warning`, `error`, `info`, `debug`,
`hook_fired`, plus UI frames (`banner`, `panel`, `table`, `body`, `markup`, `prompt`, request prompts).

> **`hook_fired`** is the bridge between the two buses: whenever a lifecycle hook matches, Mux also
> emits a `hook_fired` event — so you can drive an outbound webhook off hook activity.

### Hook-name aliases (so you don't have to learn a second vocabulary)

The two event buses named a few of the same moments differently. Outbound `events[]` accepts the
**hook lifecycle name** and resolves it automatically:

| You write (hook name) | Matches (stream event) |
|-----------------------|------------------------|
| `text_chunk` | `stream` |
| `thinking_chunk` | `thinking_start`, `thinking_update`, `thinking_end` |
| `turn_end` | `agent_turn_end` |

Exact-name twins (`tool_call`, `tool_result`, `task_complete`, `delegation`, `agent_turn_start`,
`error`, …) match directly with no alias needed.

> **Transport note:** most stream events are only emitted when Mux is running with `--serve`,
> `--stdio`, or `--acp` (an active output stream). `hook_fired` is broadened to also fire whenever an
> outbound sink is armed. So for outbound webhooks, run Mux with `--serve`.

---

## 3. Inbound Webhooks (external → Mux)

An inbound webhook lets an **external HTTP POST fire an agent goal**. It is a new daemon trigger type
(`webhook`), so it lives in `config.json` under `daemon.triggers[]` and needs the **daemon + serve**
running to receive.

```json
{
  "daemon": {
    "enabled": true,
    "triggers": [
      {
        "id": "ghpr",
        "type": "webhook",
        "goal": "Review this GitHub PR and post findings: {payload}",
        "mode": "agent",
        "agent": "CodeAgent",
        "secret": "${GH_WEBHOOK_SECRET}",
        "payloadLimit": 8192,
        "cooldown": 5
      }
    ]
  }
}
```

| Field | Meaning |
|-------|---------|
| `id` | Route id → `POST /api/hook/<id>` |
| `type` | `"webhook"` |
| `goal` | Goal template; `{payload}` = request body, plus `{source}` `{timestamp}` `{id}` |
| `mode` / `agent` | Orchestrator (`agent`/`swarm`/`pswarm`) + optional agent override |
| `secret` | HMAC shared secret. When set, a valid `X-Hub-Signature-256` is **required** |
| `payloadLimit` | Max body bytes forwarded into the goal (untrusted input; default 8192) |
| `cooldown` | Minimum seconds between firings |

### How it works

1. A sender (GitHub, Stripe, an alert, another Mux) POSTs to `POST /api/hook/{id}`.
2. Mux verifies the HMAC signature (when a `secret` is set), truncates the body to `payloadLimit`,
   and **immediately returns `202 Accepted`** — it does not wait for the agent (goals are long runs).
3. The payload is queued; a background loop drains it under `cooldown` and fires the goal with
   `{payload}` templated in.

**Response contract:** `202` accepted · `401` bad/missing signature · `404` unknown or non-webhook id.

### Trust — HMAC signatures

Your inbound URL is on the public internet, so Mux must verify a POST really came from your sender and
wasn't tampered with. Both sides hold a shared **secret**; the sender computes
`HMAC-SHA256(secret, raw_body)` and sends it as `X-Hub-Signature-256: sha256=<hex>`. Mux recomputes
it and compares in constant time. This is the exact scheme GitHub and Stripe use, so their webhooks
work against Mux out of the box.

> **Always set a `secret` for anything internet-facing.** With no secret **and** serve auth off, the
> endpoint is open to anyone who knows the URL (a deliberate convenience for trusted local networks).

### Auth interaction

The `/api/hook/*` route is deliberately **excluded from the serve bearer-token gate** (`serve.auth`) —
an external sender can't hold your Mux token; it authenticates with HMAC instead. Every other `/api`
route and `/ws` remain token-gated. When a webhook trigger has no `secret` and serve auth is on, the
route falls back to requiring the bearer token.

Scaffold one with `/createhook` → *Inbound webhook*.

---

## 4. The Daemon (`--daemon`)

The daemon runs background trigger loops that fire goals autonomously, alongside the interactive loop
and web UI. Configure under `config.json` `daemon`. Control it at runtime with `/daemon`
(usable from the top-level menu **and** in-session — it is session-agnostic):

```
/daemon             status (triggers + detached jobs)
/daemon on | off    start / stop
/daemon jobs        list triggers + jobs
/daemon cron        add a cron trigger (bare = interactive builder)
/daemon watch       add a file-watch trigger (bare = interactive builder)
/daemon cancel <id> cancel a runtime trigger
```

**Trigger types**

- **watch** — `FileSystemWatcher` on a path/glob; fires on create/modify with per-file cooldown.
- **cron** — 5-field cron (`min hour day month weekday`); supports `*`, `*/N`, `N-M`, `N,M`.
- **status** — health checks (`http://` HEAD, `process:name`, `tcp:host:port`); optional restart.
- **bridge** — supervises a long-lived child process (Telegram/Discord/Signal bridges).
- **webhook** — inbound HTTP trigger (see §3).

Goal templates support `{file}`, `{filename}`, `{timestamp}`, `{id}` (watch/cron) and
`{payload}`, `{source}` (webhook). Each trigger runs as an independent task; the daemon emits the
hook events `daemon_start`, `daemon_stop`, `daemon_trigger`, `daemon_status`, `daemon_bridge`.

---

## 5. Quick reference

| I want to… | Use | Config file |
|------------|-----|-------------|
| Run a script when an agent finishes | Hook (`task_complete`) | `swarm.json` `hooks[]` |
| Ping Slack/Discord when a goal completes | Outbound webhook | `swarm.json` `webhooks[]` |
| Have GitHub/Stripe fire an agent goal | Inbound webhook | `config.json` `daemon.triggers[]` (webhook) |
| Run a goal on a schedule / file change | Daemon cron / watch | `config.json` `daemon` |

Scaffold any of the first three with **`/createhook`** (it branches by type). Manage the daemon with
**`/daemon`** and hooks with **`/hooks`**.
