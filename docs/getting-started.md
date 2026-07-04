# Getting Started

A ten-minute first-run tutorial: install, set up, talk to one agent, then run your first swarm.

---

## 1. Install

**Linux / macOS:**

```bash
curl -fsSL https://www.muxswarm.dev/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://www.muxswarm.dev/install.ps1 | iex
```

Open a new terminal after the installer finishes. Full options (build from source, service registration, prerequisites) are in the [Installation guide](install.md).

## 2. First launch and `/setup`

```bash
mux-swarm
```

On first launch the setup wizard starts automatically. Accept the defaults for anything you are unsure about - the two answers that matter:

- **File system access:** give it a sandbox path (e.g. `~/mux-sandbox`). This is where agents read and write files.
- **Model endpoint:** if you have a Claude, Codex, Kimi, xAI, or Antigravity subscription, the easy modern path is subscription sign-in - finish the wizard, then run `/login` at the prompt. It opens a browser sign-in and wires everything up through a bundled local sidecar. No API key to paste. (If you prefer an API key, enter any OpenAI-compatible endpoint and the env var holding your key when the wizard asks.)

Verify with `/ping` - it checks provider connectivity end to end.

## 3. Your first agent session

`/agent` gives you a direct, iterative conversation with a single agent - the mode to reach for when you want hands-on back-and-forth (debugging, exploration, refinement). If you like the Claude Code / Cursor workflow, this is your exec mode.

```
> /agent
> List everything in my sandbox, summarize what you find, and suggest how to reorganize it.
```

The agent scans the sandbox, summarizes each file, and proposes a plan. Keep the conversation going: ask it to execute the reorganization, and it will create directories, move files, and verify the result. One agent, multiple turns, no orchestrator overhead.

Type `/qc` to leave the session.

## 4. Your first swarm

`/swarm` hands your goal to an orchestrator that delegates to specialist agents, compacts their results, and drives the goal to completion. Use it for multi-step objectives that span specialties.

```
> /swarm
> Write a Python monitoring script that checks disk usage, memory, CPU load, and network connectivity. It should log results to a JSON file, flag anything above 80% utilization, and generate a daily health summary in markdown. Save the script and a sample output to the sandbox.
```

Watch the orchestrator route the work: CodeAgent writes and tests the script, then a memory agent validates and persists what was learned. Each agent contributes its specialty; the orchestrator manages the handoffs. When it finishes, check your sandbox for the artifacts.

## 5. Where to go next

Mux-Swarm has two families of execution: interactive single-agent modes where you stay in the loop (`/agent`, `/ultra`, `/giga`), and batch swarm modes where you hand off a ready plan (`/swarm`, `/pswarm`).

| Mode | Command | When to use it |
|------|---------|----------------|
| **Single Agent** | `/agent` | Direct conversation with one agent: iterative work, debugging, exploration. The default. |
| **Single Agent + delegation** | `/sub`, `/psub` | Same interactive loop, but the agent can fan work out to (parallel) sub-agents. |
| **Ultra** | `/ultra` | Interactive deep-reasoning: plan discipline, maximum reasoning budget, heavy sub-agent delegation. For hard problems you still want to steer turn by turn. |
| **Giga** | `/giga` | Ultra plus the agent can spin up named teams and author/run workflows on the fly, all from the interactive loop. |
| **Sequential Swarm** | `/swarm` | Hand a ready plan or spec to an orchestrator and let it implement to completion: multi-step builds, whole-codebase refactors. |
| **Parallel Swarm** | `/pswarm` or `--parallel` | Independent subtasks that run simultaneously: research sweeps, batch jobs. |
| **CLI Goal** | `mux-swarm --goal "..."` | Fire-and-forget automation, scripts, pipelines. |
| **Continuous** | `--continuous` | Long-running autonomous loops: monitoring, recurring reports. |

Try the web UI (`mux-swarm --serve`, then open `http://localhost:6723`) - every slash command works identically in the browser.

- [Setup Guide](setup-guide.md) - full wizard walkthrough and troubleshooting
- [CLI Reference](cli.md) - every flag and slash command
- [Examples & Demos](examples.md) - video walkthroughs of each mode
- [Hooks, Webhooks & the Daemon](hooks.md) - background and event integration

---
[Back to docs index](README.md) | [Main README](../README.md)
