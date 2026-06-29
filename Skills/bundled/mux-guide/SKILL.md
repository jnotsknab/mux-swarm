---
name: mux-guide
description: Authoritative user guide + reference map for Mux-Swarm itself (v0.12.0). Use when the user asks how Mux works, how to configure it, what a command/flag/config-key does, how modes/teams/sandbox/auth/ACP/delegation work, or how to troubleshoot Mux. Points to exact sections in the bundled DOCS.md instead of dumping it.
---

# Mux-Swarm Guide (v0.12.0)

This skill is the map to Mux-Swarm's own documentation. The full reference is the bundled
**`DOCS.md`** at `{{paths.context}}/DOCS.md`. DOCS.md is large — DO NOT read it whole. Instead,
look up the ONE relevant section by name (the `## Heading`s below are stable anchors) using a
ranged read or a grep:

- Read just a section: open `{{paths.context}}/DOCS.md` and search for the `## <Section>` heading,
  then read until the next `## `.
- Grep a key/term: `rg -n "subAgentSummaryMode" "{{paths.context}}/DOCS.md"` (or findstr on Windows),
  then read a small window around the hit.

Always prefer answering from the specific section over guessing. If DOCS.md and this skill disagree,
DOCS.md wins (it ships with the build).

## Current version

- **Mux-Swarm v0.12.0.** If `/status` or the splash reports a different version, trust the runtime
  and tell the user this guide may be slightly behind.

## DOCS.md section map (grep these exact headings)

| Topic | DOCS.md section |
|---|---|
| Where files live (config, context, sessions, skills) | `## File Locations` |
| config.json: every block | `## Config.json Structure` |
| MCP server entries + `mcpConnectTimeoutSeconds` + native tools | `### mcpServers` |
| LLM providers (api-key + endpoint) | `### llmProviders` |
| Filesystem allowed paths + security mode | `### filesystem` |
| Execution sandbox (docker/podman/gvisor/kata/bwrap, network allowlist) | `## Execution Sandbox` |
| Daemon triggers (watch/cron/status/bridge) | `## Daemon Triggers` |
| Telegram/Discord/Signal bridges | `## Bridge Setup` |
| swarm.json: agents, orchestrator, compaction, teams | `## Swarm.json Structure` |
| executionLimits (budgets, timeouts, summary mode, retention) | `### executionLimits` |
| compactionAgent + autoCompactTokenThreshold | `### compactionAgent` |
| CLI launch flags | `## CLI Flags` |
| Slash commands (modes/session/config/system) | `## Interactive Commands` |
| Teams, TaskBoard, mailbox, peer self-claim | `## Teams & TaskBoard` |
| OS service registration | `## OS Service Registration` |
| Web UI + Monaco editor + auth | `## Web UI` |
| Subscription login via CLIProxy sidecar | `## Subscription Auth (CLIProxy sidecar)` |
| ACP (Zed Agent Client Protocol) transport | `## ACP Transport (Zed Agent Client Protocol)` |
| Native tools + size-tiered delegation + read_delegation | `## Native Tools & Size-Tiered Delegation` |
| Workflow engine (deterministic pipelines) | `## Workflow Engine` |
| Giga mode | `## Giga Mode` |
| Event hooks | `## Event Hooks` |
| Security recommendations | `## Security Recommendations` |

## Quick orientation (high-signal summary)

**What Mux-Swarm is.** A configurable, CLI-first agent runtime. One binary runs interactive
single-agent chat, multi-agent swarms, parallel dispatch, teams, a daemon, a web UI, and an ACP
agent. Behaviour comes from two files: `config.json` (infrastructure: providers, MCP, filesystem,
daemon) and `swarm.json` (agents, orchestrator, compaction, teams, executionLimits).

**Modes** (`/swarm` `/pswarm` `/agent` `/stateless` `/sub` `/psub` `/ultra` `/giga` `/teams`).
Single-agent is the default; `/sub`/`/psub` add (parallel) sub-agent delegation; `/ultra` is
deep-reasoning; `/giga` lets the agent spawn teams + author workflows.

**Models & providers.** `/provider` switches the active provider; `/model` and `/setmodel` manage
per-agent models. Subscription accounts (Claude, Codex, Kimi…) log in via the bundled CLIProxy
sidecar: `/login <provider>` → browser OAuth → auto-registered `cliproxy` provider. `/proxy status`
and `/ping` diagnose it.

**Context & tokens.** Single-agent sessions auto-compact at `autoCompactTokenThreshold` (default
200k). The docked footer shows live context usage. `/compact [steering]` compacts now; `/tokens`
shows the breakdown.

**Delegation.** A lead delegating work gets size-tiered results: small inline, medium summarized,
large spilled to disk with a `d:Agent#N` pointer the lead reads on demand via the `read_delegation`
tool. Spilled raw lives under `<sandbox>/delegations/` and is pruned after `delegationRetentionDays`.

**Sandbox.** `/sandbox [backend]` runs shell/REPL execution inside a container (docker/podman/gvisor/
kata microVM) or OS wrapper, with an optional network allowlist. Default is host execution.

**Self-service repair.** `/fix [what is wrong]` snapshots live runtime state and has the model
diagnose + propose ordered repair steps. `/refresh` reloads config+MCP+skills; `/reloadskills`
reloads skills; `/setup` reconfigures.

## Troubleshooting quick reference

- Something is broken and you are not sure what: run **`/fix <describe the problem>`**.
- A tool/MCP server is missing: `/tools` to see what loaded; `/refresh` to reconnect; raise
  `mcpConnectTimeoutSeconds` if a server is slow to start.
- Provider/auth errors: `/provider`, `/proxy status`, `/ping`, or re-`/login`.
- Model errors: `/model`, `/setmodel`, or `/setup`.
- Skills changed on disk: `/reloadskills`.
- Sandbox exec failing: `/sandbox host` to fall back, or check Docker/daemon is up.
- Full reference for any of the above: grep the matching `## Section` in `{{paths.context}}/DOCS.md`.

## When to use this skill

Use it whenever the user asks about Mux-Swarm itself — "how do I…", "what does X do", "why is Y
happening", "where is Z configured". Answer from the relevant DOCS.md section (looked up by the map
above), keep the answer specific to the user's installed config where possible (check `/status`,
`/config`, `/limits`), and point them at the exact command or config key to change.
