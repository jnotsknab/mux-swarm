---
name: mux-guide
description: Authoritative user guide + reference map for Mux-Swarm itself (v0.15.0). Use when the user asks how Mux works, how to configure it, what a command/flag/config-key does, how modes/teams/sandbox/auth/ACP/delegation/daemon/webhooks/session sharing work, or how to troubleshoot Mux. Points to exact sections in the bundled DOCS.md instead of dumping it.
---

# Mux-Swarm Guide (v0.15.0)

This skill is the map to Mux-Swarm's own documentation. The full reference is the bundled
**`DOCS.md`** at `{{paths.context}}/DOCS.md`. DOCS.md is large - DO NOT read it whole. Instead,
look up the ONE relevant section by name (the `## Heading`s below are stable anchors) using a
ranged read or a grep:

- Read just a section: open `{{paths.context}}/DOCS.md`, search for the `## <Section>` heading,
  then read until the next `## `.
- Grep a key/term: `rg -n "subAgentSummaryMode" "{{paths.context}}/DOCS.md"` (or `findstr /n` on Windows),
  then read a small window around the hit.

Always answer from the specific section rather than guessing. If DOCS.md and this skill disagree,
DOCS.md wins (it ships with the build).

## Current version

- **Mux-Swarm v0.15.0.** If `/status` or the splash reports a different version, trust the runtime
  and tell the user this guide may be slightly behind.

## DOCS.md section map (grep these exact headings)

| Topic | DOCS.md section |
|---|---|
| Where files live (config, context, sessions, skills) | `## File Locations` |
| config.json: every block | `## Config.json Structure` |
| MCP server entries + `mcpConnectTimeoutSeconds` + native tools | `### mcpServers` |
| LLM providers (api-key + endpoint) | `### llmProviders` |
| Filesystem allowed paths + security mode | `### filesystem` |
| Telemetry (OTLP + local JSONL sink) | `### telemetry` |
| Web UI bind address + auth | `### serve` |
| Self-update, `--update`, restart | `## Self-Update & Lifecycle` |
| Execution sandbox (docker/podman/gvisor/kata/bwrap, network allowlist) | `## Execution Sandbox` |
| Daemon triggers (watch/cron/status/bridge/webhook), trigger fields | `## Daemon Triggers`, `### Common Trigger Fields` |
| Telegram/Discord/Signal bridges | `## Bridge Setup` |
| swarm.json: agents, orchestrator, compaction, teams, model options | `## Swarm.json Structure` |
| executionLimits (budgets, timeouts, summary mode, retention) | `### executionLimits` |
| compactionAgent + autoCompactTokenThreshold | `### compactionAgent` |
| Reasoning effort + provider params | `### reasoning`, `### additionalParams` |
| CLI launch flags (incl. `--join`, `--selftest`) | `## CLI Flags` |
| Slash commands (modes/session/sharing/config/system) | `## Interactive Commands` |
| Live session sharing: `/share`, `/join`, guest typing, security | `## Session Sharing` |
| Teams, TaskBoard, peer self-claim, mailbox | `## Teams & TaskBoard` |
| Deep memory (reflection agent) | `## Deep Memory (reflectionAgent)` |
| Cost/token breakdown | `## Cost breakdown - `/cost all` / `/tokens all`` |
| Task auto-decomposition / `/taskgraph` | `## Task auto-decomposition` |
| TUI themes | `## TUI color themes` |
| OS service registration | `## OS Service Registration` |
| Web UI + Monaco editor + auth | `## Web UI` |
| Subscription login via CLIProxy sidecar | `## Subscription Auth (CLIProxy sidecar)` |
| ACP (Zed Agent Client Protocol) transport | `## ACP Transport (Zed Agent Client Protocol)` |
| Native tools + size-tiered delegation + read_delegation | `## Native Tools & Size-Tiered Delegation` |
| Workflow engine (deterministic pipelines) | `## Workflow Engine` |
| Giga mode | `## Giga Mode` |
| Event hooks, outbound + inbound webhooks | `## Event Hooks` |
| Security recommendations | `## Security Recommendations` |

## What changed since v0.12.1 (headline)

- **v0.15.0: live session sharing.** `/share [--local|--lan]` streams the session to guests the host approves
  (Deny / Watch / Watch + type); `/join <link>` or `mux-swarm --join "<link>"` to watch. End-to-end encrypted;
  the link's `#secret` is the credential. Guest typing is host-enforced (editing keys only, separate queue read only by
  the idle prompt, allowlisted commands, `Ctrl+]` revokes). Same-network only.
- **v0.15.0: webhooks return results.** `POST /api/hook/{id}` returns `202 {deliveryId}`; set `callbackUrl` on the
  trigger to receive `{id, deliveryId, status: ok|error, result, error}` (signed with `secret`). Webhook runs are
  stateless; default webhook cooldown is 0 (a set cooldown answers `429` + `Retry-After`); `payloadLimit` default 65536.
  One process serves many webhooks (agent-mode triggers concurrent, swarm/pswarm one at a time, deliveries per
  trigger in order); scale with more processes.
- **v0.14.x:** persistent telemetry + `/telemetry` dashboard, mouse support in the TUI (frame engine default),
  `/effort` tiers incl. `xhigh`/`max`/custom, `/proxy update` (latest CLIProxyAPI), `--selftest`, MCP non-strict by
  default (`--mcp-strict` to require all servers), event-driven delegation waits.
- **v0.13.x:** `--serve` binds to `127.0.0.1` by default (`serveAddress` to expose), path-traversal fix in the serve
  file API, Ctrl+N mid-turn steer.

## Quick orientation (high-signal summary)

**What Mux-Swarm is.** A configurable, CLI-first agent runtime. One binary runs interactive
single-agent chat, multi-agent swarms, parallel dispatch, teams, a daemon, a web UI, an ACP agent, and
live session sharing. Behaviour comes from two files: `config.json` (infrastructure: providers, MCP, filesystem,
daemon, serve) and `swarm.json` (agents, orchestrator, compaction, teams, executionLimits).

**Modes** (`/agent` `/stateless` `/sub` `/psub` `/ultra` `/giga` `/teams` `/swarm` `/pswarm`).
`/agent` is the primary interactive interface; delegation-enabled `/ultra`, `/giga` and team leads can match or
exceed the batch modes. `/swarm` (serial specialist coordination) and `/pswarm` (concurrent batches) are
execution styles for handing off a ready plan, not capability upgrades.

**Models & providers.** `/provider` switches the active provider; `/model` and `/setmodel` manage
per-agent models; `/effort` (or Shift+Tab) sets reasoning effort. Subscription accounts (Claude, Codex, Kimi, ...)
log in via the bundled CLIProxy sidecar: `/login <provider>` → browser OAuth → auto-registered `cliproxy` provider.
`/proxy status|update|restart` and `/ping` manage and diagnose it.

**Context & tokens.** Single-agent sessions auto-compact at `autoCompactTokenThreshold`. The docked footer
shows live context usage. `/compact [steering]` compacts now; `/tokens` / `/cost all` show the breakdown;
`/telemetry` opens the usage dashboard.

**Delegation.** A lead delegating work gets size-tiered results: small inline, medium summarized,
large spilled to disk with a `d:Agent#N` pointer the lead reads on demand via `read_delegation`.
Background delegations are awaited with `check_delegations(waitSeconds)`.

**Automation.** `--daemon` runs watch/cron/status/bridge/webhook triggers from `config.json`; `/daemon` and the web
UI add/remove them at runtime. External systems drive Mux via `--stdio`, the serve HTTP/WS API, webhooks (with result
callbacks), or the Python SDK (`muxswarm` on PyPI).

**Sandbox.** `/sandbox [backend]` runs shell/REPL execution inside a container (docker/podman/gvisor/
kata microVM) or OS wrapper, with an optional network allowlist. Default is host execution.

**Self-service repair.** `/doctor` (health check, no model call), `/fix [what is wrong]` (diagnose + ordered
repair steps), `/refresh` (config+MCP+skills), `/reloadskills`, `/setup`.

## Troubleshooting quick reference

- Something is broken and you are not sure what: **`/doctor`**, then **`/fix <describe the problem>`**.
- A tool/MCP server is missing: `/tools` to see what loaded; `/refresh` to reconnect; raise
  `mcpConnectTimeoutSeconds` if a server is slow to start.
- Provider/auth errors: `/provider`, `/proxy status`, `/ping`, or re-`/login`. A model rejected by the proxy may
  need `/proxy update`.
- Model errors: `/model`, `/setmodel`, or `/setup`.
- Skills changed on disk: `/reloadskills`.
- Sandbox exec failing: `/sandbox host` to fall back, or check Docker/daemon is up.
- Webhook not firing: the trigger `id` must match the URL, the daemon must be running (`--daemon`), a set `secret`
  requires `X-Hub-Signature-256` over the raw body, and a `429` means the trigger's cooldown is active.
- Watch trigger not firing: `path` is a directory plus a filename pattern (`dir/*`), not a bare directory.
- Share guest can't connect: `/share --lan` for other machines (default is loopback only); check the firewall allows
  the share port; the full link including `#secret` is required.
- Full reference for any of the above: grep the matching `## Section` in `{{paths.context}}/DOCS.md`.

## When to use this skill

Use it whenever the user asks about Mux-Swarm itself - "how do I...", "what does X do", "why is Y
happening", "where is Z configured". Answer from the relevant DOCS.md section (looked up by the map
above), keep the answer specific to the user's installed config where possible (check `/status`,
`/config`, `/limits`), and point them at the exact command or config key to change.
