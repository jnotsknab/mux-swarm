# CLI and Command Reference

Complete reference for mux-swarm CLI flags and interactive slash commands (v0.12.1).

## CLI Flags

Launch flags accepted by the `mux-swarm` binary. Any of these can be persisted across launches with `/startargs <args>` (stored in `config.startupArgs`; clear with `/startargs clear`).

| Flag | Description |
|---|---|
| `--help` / `-h` | Print help and exit |
| `--goal <text\|file>` | Explicit goal (also accepted as a bare positional argument) |
| `--goal-id <id>` | Attach a persistent goal/session identifier |
| `--continuous` | Continuous autonomous mode |
| `--parallel` | Parallel swarm (concurrent batch dispatch) |
| `--max-parallelism <n>` | Max concurrent agent tasks (default 4) |
| `--prod` | Prod mode (orchestrator `[[MARKER]]` output) |
| `--stdio` | Machine-readable NDJSON output, no ANSI; suppresses hook prompts |
| `--acp` | Zed Agent Client Protocol server over stdio (JSON-RPC) |
| `--delimiter <str>` | Set the multi-line input delimiter |
| `--model <id>` | CLI model override |
| `--min-delay <secs>` | Minimum delay between continuous loops (default 300) |
| `--persist-interval <s>` | Persist session state every N seconds |
| `--session-retention <n>` | Keep the last N sessions (default 10) |
| `--watchdog` | External watchdog (auto-restart on crash) |
| `--mcp-strict <bool>` | Require all MCP servers to connect (default true) |
| `--docker-exec <bool>` | Route execution through Docker skills |
| `--sandbox [backend]` | Startup sandbox backend override (default argument `docker`); validated and synced to config |
| `--agent <name>` | Pick an agent and boot into a single-agent session (or the agent for a goal/machine run) |
| `--agent-mode` | Boot straight into a single-agent session (pair with `--agent`) |
| `--plan` | Plan mode (approve before executing) |
| `--ultra` / `--ultraplan` | Max-reasoning mode (plan + auto sub-agents per config) |
| `--giga` | Dynamic team/workflow orchestration (parity with `/giga`) |
| `--sub` / `--subagents` | Enable sub-agent delegation |
| `--psub` / `--parasubagents` | Enable parallel sub-agent delegation |
| `--verbose` | Verbose MCP/init logging |
| `--swarm` / `--pswarm` / `--stateless` / `--teams` | Boot straight into that mode |
| `--classic` / `--tui` | Force render mode (classic line renderer vs live TUI) |
| `--clear` | Clear the console at startup |
| `--report [session-id]` | Generate audit report(s) and exit (no id = all sessions) |
| `--provider <name>` | Set the active LLM provider on launch |
| `--cfg <path>` | Override the Config.json path (scoped instance) |
| `--swarmcfg <path>` | Override the Swarm.json path (scoped instance) |
| `--workspace <path>` / `--ws` | Set the @-file workspace root |
| `--workflow <file>` / `--wf` | Load and run a workflow file |
| `--serve [port]` | Embedded web UI (default 6723) |
| `--daemon` | Daemon mode (file watch, cron, status, and webhook triggers from config.json) |
| `--update` | Self-update from the latest GitHub release, then exit |
| `--register` / `--remove` | Register/unregister mux-swarm as an OS service |
| `--relaunch-after` | Internal: post-update re-exec handshake (not user-facing) |

### Goal-Driven Execution

```bash
mux-swarm "<goal>"
mux-swarm <goal.txt>
mux-swarm --goal "<goal>"
mux-swarm --goal <goal.txt>
```

### Single-Agent via CLI

```bash
mux-swarm --agent CodeAgent --goal "<goal>"
mux-swarm --agent WebAgent --goal task.txt --continuous --goal-id overnight --min-delay 600
```

### Continuous Mode

```bash
mux-swarm --continuous --goal "<goal>" --goal-id my-run
mux-swarm --continuous --goal task.txt --goal-id overnight --min-delay 600
```

### Parallel Mode

```bash
mux-swarm --parallel --goal "<goal>"
mux-swarm --parallel --continuous --goal "<goal>" --goal-id batch-run
mux-swarm --parallel --max-parallelism 6 --goal task.txt
```

Parallel mode decomposes a goal into independent subtasks and dispatches them concurrently across agents. Use `--max-parallelism` to cap simultaneous agent tasks (default 4). Combines with `--continuous` for recurring parallel batch runs.

## Interactive Commands

Type `/help` at any time for the built-in reference, or `/` in the live TUI for a fuzzy command palette. Commands are scoped: some only work inside a live session, some only at the top-level REPL, and a few work in both.

### Session-native commands

Available inside a live single-agent session.

| Command | Description |
|---|---|
| `/compact [steering]` | Compact live session context; optional steering text guides the summary |
| `/handoff [steering\|path.md]` | Write a cold-resume handoff doc via the active model |
| `/heal [deep] [steering]` / `/reflect` | Review the session for lessons; propose BRAIN/MEMORY (and SKILL) self-heal write-backs |
| `/fix [what is wrong]` | Diagnose and propose repairs for a misbehaving Mux subsystem |
| `/diff` | Working-tree git diff (collapsible) |
| `/doctor` | Health check: providers, MCP, sandbox, proxy (no model call) |
| `/cost` (+ `/cost all`) | Token usage + estimated cost; `all` = per-model matrixed breakdown |
| `/tokens` / `/context` (+ `/tokens all`) | Context/token usage; `all` is an alias of `/cost all` |
| `/init` | Analyze the workspace and scaffold AGENTS.md |
| `/review` | AI review of the working-tree diff (read-only) |
| `/wipe` | Clear session history, keep the session |
| `/undo` | Drop the last exchange |
| `/retry` / `/redo` | Re-run the last turn |
| `/effort` | Cycle reasoning effort (also Shift+Tab) |
| `/tag <text>` | Tag the live session for resume/search |
| `/kanban` (+ add/assign/block/ready/move/remove/peer) | Editable team task board |
| `/background` / `/bg` (+ jobs/cancel) | Run an agent goal in the background; watch via `\` |
| `/detach` | Park the session in the background; re-enter with `/attach` |
| `/voice [auto\|off\|vol <1-10>]` | Local speech-to-text dictation into the compose field (TUI only) |
| `/unhide <agent>` | Restore a hidden sub-agent to the viewport (hide via the `\` Agent View `h` key; `/background` lanes start hidden) |
| `/qc` / `/qm` | Quit the session loop |
| `!<command>` | Run a shell command and add its output to context |

### Both scopes (process-level)

Work inside a session and at the top-level REPL.

| Command | Description |
|---|---|
| `/daemon` / `/da` (on\|off\|jobs\|cron\|watch\|cancel) | Runtime daemon control; bare `cron`/`watch` opens an interactive builder (plain-English cron accepted) |
| `/update` | Self-update from the latest GitHub release (hash-verified; restarts if the binary changed) |

### REPL-only commands

Available at the top-level REPL.

**Mode launch**

| Command | Description |
|---|---|
| `/swarm` | Launch the multi-agent orchestrated swarm loop |
| `/pswarm` | Launch the parallel swarm (concurrent batch dispatch) |
| `/agent` | Launch an interactive single-agent loop |
| `/stateless` | Stateless single-agent loop for one-off tasks |
| `/subagents` (`/sub`) | Enable ephemeral sub-agent delegation inside a single-agent loop |
| `/parasubagents` (`/psub`) | Enable parallel ephemeral sub-agent delegation |
| `/workflow <file>` | Run a deterministic workflow from a JSON file |
| `/teams [name]` | List and launch named teams from swarm.json |
| `/createteam` | Guided wizard to define a team (lead, members, coordination, parallelism) |
| `/createhook [id]` | Guided wizard: scaffold a hook, an outbound webhook, or an inbound webhook |
| `/hooks (on\|off\|create)` | Hooks status / toggle / create |
| `/onboard` | Create or update your operator profile (BRAIN.md + MEMORY.md) |

**Toggles and configuration**

| Command | Description |
|---|---|
| `/plan` | Toggle plan mode (agents present a plan and ask for approval before executing) |
| `/ultra` (`/ultraplan`) | Interactive deep-reasoning mode inside the single-agent loop: plan + maximum reasoning budget + heavy sub-agent delegation |
| `/giga` | Interactive Giga mode: ultra plus the agent can spawn named teams and author/run workflows on the fly |
| `/continuous` (`/cont`) | Toggle continued autonomous execution |
| `/addcontext` | Configure what context each agent is injected with |
| `/maxp` | Max agents running in parallel (default 4) |
| `/setmodel` | Change the model for any agent, orchestrator, or compaction agent |
| `/set <key> <value>` | Edit any config.json or swarm.json key by dotted path (bare `/set` opens a picker) |
| `/showreasoning full\|summary\|none` | Show or hide streamed reasoning text |
| `/config` | Show all configuration settings; every key is `/set`-editable |
| `/newagent` | Guided wizard to create a swarm agent |
| `/editagent` | Edit a swarm agent (model, description, MCP servers, delegation) |
| `/delagent` | Remove a swarm agent from swarm.json (and optionally its prompt file) |
| `/swap` | Swap the active agent for single-agent mode |
| `/verbose` | Toggle TUI tool output between compact and full panels |
| `/subagentview` (`/sav`) | Toggle collapsed/expanded delegated sub-agent output |
| `/daemonview` (`/dv`) | Toggle the daemon output view |
| `/dockerexec` | Toggle Docker execution mode |
| `/sandbox` | View or switch the execution sandbox backend (host/docker/podman/gvisor/kata/...) |
| `/login` | Sign in to a subscription provider via the CLIProxy sidecar OAuth flow |
| `/ping` | Check sidecar + provider login readiness |
| `/proxy status\|update\|restart` | Manage the bundled CLIProxyAPI sidecar |
| `/delimiter` | Toggle the multi-line input delimiter |

**Utilities**

| Command | Description |
|---|---|
| `/classic` | Switch to the classic line-by-line renderer |
| `/tui` | Switch to the live full-screen TUI renderer |
| `/resume` | Resume a previous single-agent session (shows #tags) |
| `/attach [id]` | Re-attach a detached session |
| `/model` | View current model assignments |
| `/provider` | View or switch the active LLM provider |
| `/workspace [path]` | Set or view the @-file workspace root |
| `/limits` | Display current execution limits for orchestration and agents |
| `/tools` | List available MCP tools across enabled servers |
| `/skills` / `/skill` | List available local skills / inspect one |
| `/memory [deep\|standard\|show\|set <k> <v>]` | Toggle deep memory + status and tuning |
| `/deep [off]` | Shortcut to enable (or disable) deep memory mode |
| `/taskgraph on\|off\|status` | Auto task decomposition onto the task board (config block is `decompose`) |
| `/theme [default\|dark\|light\|mono\|solarized\|dracula\|gruvbox]` | Switch the TUI theme |
| `/sessions` | List all saved sessions with type and agent count |
| `/setup` | Run initial setup / reconfigure |
| `/reloadskills` | Refresh the skills directory for mid-process changes |
| `/installskill <name\|owner/repo\|owner/repo/path\|URL>` | Install a skill from the curated registry or GitHub |
| `/refresh` | Full system refresh: config, MCP servers, and skills |
| `/report [id]` | Generate full session audit report(s) |
| `/clear` | Clear the terminal |
| `/status` | View current system status: provider, models, tools, skills, and sessions |
| `/help` | Full command reference |
| `/shortcuts` (`/keys`) | Show keyboard shortcuts |
| `/exit` | Exit the runtime |
| `/startargs` | Persist CLI flags across launches (`/startargs clear` to reset) |
| `/dbg` / `/nodbg` | Enable/disable tool-call output (stdio mode only) |
| `/disabletools` | Disable tool availability |

---
[Back to docs index](README.md) | [Main README](../README.md)
