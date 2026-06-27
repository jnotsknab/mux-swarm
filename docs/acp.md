# ACP — Zed Agent Client Protocol Adapter

Mux-Swarm can run as an **ACP agent server** (Zed Industries' Agent Client Protocol) so
editors like [Zed](https://zed.dev) can drive a Mux single-agent session as an external agent.

## Quick start

```bash
MuxSwarm --acp
```

`--acp` runs Mux as a JSON-RPC 2.0 server over **newline-delimited stdio**: it reads one
compact JSON-RPC message per line on `stdin` and writes ACP messages (and only ACP messages)
to `stdout`. Diagnostics go to `stderr`. There is no ANSI / NDJSON on stdout in this mode.

### Zed `agent_servers` manifest

Add this to your Zed `settings.json` (adjust the path / config flags to your install):

```json
{
  "agent_servers": {
    "Mux-Swarm": {
      "command": "C:\\Users\\you\\path\\to\\MuxSwarm.exe",
      "args": [
        "--acp",
        "--cfg", "C:\\Users\\you\\AppData\\Local\\Mux-Swarm\\Configs\\config.json",
        "--swarmcfg", "C:\\Users\\you\\AppData\\Local\\Mux-Swarm\\Configs\\swarm.json"
      ],
      "env": {}
    }
  }
}
```

On Linux/macOS the `command` is the `MuxSwarm` binary and paths use `/`.

> **Why pass `--cfg` / `--swarmcfg`?** ACP runs headless with no first-run setup prompt. Point
> it at an already-configured install so it does not fall into provider onboarding. If those
> files are missing, Mux writes a default `swarm.json` and prints setup guidance — which would
> not reach a stdout-pure ACP client.

## What is implemented

| ACP method / update | Status | Notes |
|---|---|---|
| `initialize` | ✅ | `protocolVersion: 1`; advertises `agentInfo{name:"mux-swarm"}`, `loadSession`. Prompt caps: text + `resource_link` baseline only (no image/audio/embeddedContext). |
| `session/new` | ✅ | One ACP session = one Mux single-agent loop. |
| `session/load` | ✅ | Resolves a persisted session by id, replays its transcript as `user_message_chunk` / `agent_message_chunk` updates, then resumes that context. |
| `session/prompt` | ✅ | Text content blocks (and embedded text / `resource_link` references) flattened into the user turn. |
| `session/cancel` | ✅ | Aborts the in-flight turn; the prompt is answered with `stopReason: "cancelled"`. |
| `session/close` | ✅ | Ends the active session loop. |
| `authenticate` | ✅ (no-op) | No auth required; returns `{}`. |
| `session/update` → `agent_message_chunk` | ✅ | Streamed assistant text, stable `messageId` per turn. |
| `session/update` → `agent_thought_chunk` | ✅ | Streamed reasoning (Mux muted stream). |
| `session/update` → `tool_call` / `tool_call_update` | ✅ | Synthesized pending→completed lifecycle; tool kind mapped; follow-along `locations` (absolute paths from args); edit-tool diffs surfaced as ACP `diff` content. |
| `session/update` → `plan` | ✅ | Mux `step` frames map to single-entry plan snapshots. |
| `session/update` → `usage_update` | ✅ | Live session token count + context size at each turn boundary. |
| `stopReason` | ✅ | `end_turn` on normal completion, `cancelled` on `session/cancel`. |

## Deliberately NOT implemented (by design)

- **`fs/read_text_file`, `fs/write_text_file`, `terminal/*` client-callback EXECUTION.** The
  outbound agent->client request/response plumbing exists (`SendRequestAsync`, with client
  capabilities captured from `initialize`), but Mux performs file and shell work directly
  through its own MCP tool layer rather than routing it back through the ACP client. Re-routing
  would be a large architectural inversion with no functional gain; results are reported as
  `tool_call` / `tool_call_update` updates (with diff content for edits). These callbacks are
  optional + capability-gated in ACP, so this is spec-compliant.
- **`session/request_permission` PROMPTING.** Tool gating is handled by Mux's own config /
  approval model; the adapter has the factory + transport to raise an ACP permission request
  but does not gate Mux tools behind one by default.
- **`session/delete`.** Not advertised (session deletion is managed via Mux's own retention).

## Architecture (how it works)

The adapter (`Utils/Acp/`) is a thin transport shim over the existing single-agent REPL — it
does **not** re-implement the turn engine:

- **`AcpProtocol`** — pure, side-effect-free JSON-RPC envelope + ACP update factories and the
  prompt content-block flattener. Fully unit-tested with no live model.
- **`AcpInputReader`** — a `TextReader` installed as `MuxConsole.InputOverride` (the same seam
  `--serve` uses for its WebSocket input). It feeds prompts to the orchestrator's next-turn
  `ReadInput()`. **Entering that read is the exact turn-completion boundary** at which the
  in-flight `session/prompt` is answered with a `stopReason`.
- **`AcpServer`** — the stdio JSON-RPC loop + session lifecycle. It captures the orchestrator's
  structured event stream via `MuxConsole.AcpSink` and translates each event into a
  `session/update` notification, so stdout carries pure ACP and never the NDJSON wire.

Off the `--acp` path the sink is null and every emit path is byte-identical to a normal run.
