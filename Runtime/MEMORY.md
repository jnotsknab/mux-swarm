# MEMORY.md — Agent System Reference

This file provides baseline context for Mux agents operating within this swarm. It is read at session start and serves as the agent's primary system reference.

## What Is Mux-Swarm

Mux-Swarm is a CLI-native agentic OS for multi-agent orchestration. It manages parallel execution, deterministic workflows, tool-native AI operations, process supervision, crash recovery, scoped isolation, layered memory, and a skills runtime. Built with .NET 10 / C# 14 using Microsoft.Extensions.AI and Microsoft.Agents.AI.

## System Layout

| Component | Location |
|-----------|----------|
| Binary | `mux-swarm` (on PATH after install) |
| Config (infrastructure) | `~/.local/share/Mux-Swarm/Configs/Config.json` |
| Config (topology/roles) | `~/.local/share/Mux-Swarm/Configs/Swarm.json` |
| Agent prompts | `~/.local/share/Mux-Swarm/Configs/Prompts/Agents/*.md` |
| Skills | `~/.local/share/Mux-Swarm/Skills/` |
| Sessions | `~/.local/share/Mux-Swarm/Sessions/` |
| Sandbox (agent output) | User-defined during `/setup` |

## Config Architecture

- **Config.json** — Infrastructure: LLM providers, MCP servers, filesystem allowlist, daemon triggers, telemetry.
- **Swarm.json** — Topology: Agent definitions, model assignments, modelOpts, delegation rules, execution limits, event hooks.
- **Prompts/Agents/*.md** — Behavioral contracts for each agent role.
- **Skills** — Task-specific playbooks loaded at runtime.

## Memory Architecture (4 Layers)

1. **In-context** — Compressed agent results passed between orchestration steps.
2. **Semantic** — ChromaDB vector store for similarity search across past sessions and documents.
3. **Structured** — Knowledge graph (entities, relations, observations) for persistent facts.
4. **Filesystem** — Files as a message bus; agents read/write artifacts to the sandbox path.

Agents should query memory before acting on tasks with prior context. Cross-reference filesystem with vector/structured stores for accuracy.

## Key CLI Flags

| Flag | Purpose |
|------|---------|
| `--goal <file>` | Execute a goal from a file |
| `--agent <name>` | Run a specific agent |
| `--plan` | Generate plan without executing |
| `--provider <name>` | Override LLM provider |
| `--model <name>` | Override model |
| `--continuous` | Run in continuous mode |
| `--parallel` | Enable parallel execution |
| `--serve` | Start embedded web UI |
| `--daemon` | Run in daemon mode with triggers |
| `--register` | Register as OS service |
| `--watchdog` | Enable watchdog supervision |
| `--workflow <file>` | Run a JSON workflow pipeline |
| `--mcp-strict` | Enforce strict MCP scoping |
| `--docker-exec` | Execute in Docker containers |
| `--cfg <path>` | Override Config.json path |
| `--swarmcfg <path>` | Override Swarm.json path |

## Daemon Triggers

Daemon mode (`--daemon`) enables four trigger types defined in Config.json:

- **Watch** — FileSystemWatcher with cooldown; reacts to file changes.
- **Cron** — Standard 5-field cron expressions; time-based execution.
- **Status** — Health checks via HTTP HEAD, process lookup, or TCP connect; optional auto-restart.
- **Bridge** — Long-lived child process supervision with auto-restart (Discord, Signal, Telegram bridges).

Trigger modes: `agent` (single agent), `swarm` (multi-agent), `pwarm` (parallel swarm).

## Security Defaults

- Filesystem allowlist enforcement — agents can only access configured `allowedPaths`.
- Least-privilege MCP scoping per agent.
- Prompt and config-level role separation.
- Session-based provenance tracking.
- Docker execution option for isolation.

**Best practices:**
- Use `--mcp-strict` in production.
- Keep `allowedPaths` minimal and scoped.
- Store API keys as environment variables, never in config files.
- Use `--cfg`/`--swarmcfg` for isolated instances.
- Narrow daemon watch paths to prevent excessive triggers.

## Model Tuning (modelOpts)

Per-agent in Swarm.json:

| Parameter | Range | Description |
|-----------|-------|-------------|
| `temperature` | 0–2 | Randomness |
| `topP` | 0–1 | Nucleus sampling |
| `topK` | integer | Top-K sampling |
| `maxOutputTokens` | integer | Response length cap |
| `frequencyPenalty` | -2 to 2 | Repetition reduction |
| `presencePenalty` | -2 to 2 | Topic diversity |
| `seed` | integer | Reproducibility |
| `reasoning.effort` | none/low/medium/high/extra_high | Chain-of-thought depth |
| `reasoning.output` | none/summary/full | Reasoning visibility |

## Execution Limits

Defined in Swarm.json under `executionLimits`:

| Limit | Default |
|-------|---------|
| `progressEntryBudget` | 1000 |
| `crossAgentContextBudget` | 2000 |
| `progressLogTotalBudget` | 4500 |
| `maxOrchestratorIterations` | 15 |
| `maxSubAgentIterations` | 8 |
| `maxSubTaskRetries` | 4 |
| `maxStuckCount` | 3 |

## Common Operations

```bash
# Interactive mode
mux-swarm

# Execute a goal
mux-swarm --goal goal.md

# Start web UI
mux-swarm --serve

# Daemon mode
mux-swarm --daemon

# Register as system service
mux-swarm --register

# Run workflow
mux-swarm --workflow pipeline.json

# Scoped instance
mux-swarm --cfg ./custom-config.json --swarmcfg ./custom-swarm.json
```

## Interactive Commands (Inside Session)

- `/setup` — Rerun setup wizard
- `/status` — System status
- `/agents` — List active agents
- `/models` — List available models
- `/skills` — List loaded skills
- `/sessions` — List recent sessions
- `/clear` — Clear context
- `/help` — Show all commands

## Agent Guidelines

- Query memory before acting on tasks with prior context.
- Write artifacts to the configured sandbox path.
- Use skills when available — they encode established workflows.
- Report errors with context for recovery across sessions.
- Store durable findings in the knowledge graph and ChromaDB.
- Respect filesystem allowlists — never write outside configured paths.
