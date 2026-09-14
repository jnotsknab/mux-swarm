# Getting Started

A ten-minute first-run tutorial: install, set up, and start in the main agentic interface. Add specialist assistance or choose a specialized execution style when useful.

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

**Start with `/agent`**, the main agentic interface for development, research, automation, and iterative work. One lead owns the conversation; that does not limit participation to one agent. Depending on the enabled tools and launch settings, the lead can work directly, delegate specialists, coordinate parallel work, or drive teams and workflows. `/swap` chooses the lead for the next `/agent` session.

```
> /agent
> List everything in my sandbox, summarize what you find, and suggest how to reorganize it.
```

The agent scans the sandbox, summarizes each file, and proposes a plan. Keep the conversation going: ask it to execute the reorganization, and it will create directories, move files, and verify the result. Keep the same lead conversation as the work grows; specialist assistance does not require switching to `/swarm` or `/pswarm`.

Type `/qc` to leave the session.

## 4. Optional: serial specialist orchestration

Choose `/swarm` when you specifically want a dedicated coordinator to delegate one specialist task at a time, inspect its result, then choose the next task. It is a serial execution style, not an upgrade from `/agent`: delegation-enabled ultra, giga, and team-lead sessions can match or exceed its capabilities. For dedicated concurrent batches choose `/pswarm`; for a prescribed, repeatable A→B→C sequence choose a workflow rather than assuming the coordinator will follow a fixed schedule.

```
> /swarm
> Write a Python monitoring script that checks disk usage, memory, CPU load, and network connectivity. It should log results to a JSON file, flag anything above 80% utilization, and generate a daily health summary in markdown. Save the script and a sample output to the sandbox.
```

Watch the coordinator route one specialist task at a time—for example, coding followed by review. The actual assignments depend on the goal and configuration, not a fixed agent order. When it finishes, check your sandbox for the artifacts.

## 5. Where to go next

Treat the interface, enabled capabilities, and execution style as separate choices. `/agent` is the primary interface; `/sub`, `/psub`, `/ultra`, and `/giga` configure capabilities/presets for subsequent launches. Team leads add their configured coordination tools. `/swarm` and `/pswarm` are specialty styles, not higher tiers; parallel work does not require `/pswarm`.

| Mode | Command | When to use it |
|------|---------|----------------|
| **Main interface** | `/agent` | A continuing lead conversation for everyday agentic work; start here. |
| **Specialist assistance** | `/sub`, `/psub` | Enable serial or concurrent specialist delegation from the lead. |
| **Ultra** | `/ultra` | Planning and deep reasoning; parallel delegation is enabled when `ultra.autoSubAgents` is configured. Not a separate execution architecture. |
| **Giga** | `/giga` | Ultra plus the agent can spin up named teams and author/run workflows on the fly, all from the interactive loop. |
| **Serial specialty** | `/swarm` | Dedicated coordinator dispatches one specialist task at a time, then evaluates the result. |
| **Concurrent-batch specialty** | `/pswarm` or `--parallel` | Dedicated coordinator dispatches independent specialist tasks in concurrent batches. |
| **CLI Goal** | `mux-swarm --goal "..."` | Fire-and-forget automation, scripts, pipelines. |
| **Continuous** | `--continuous` | Long-running autonomous loops: monitoring, recurring reports. |

Try the web UI (`mux-swarm --serve`, then open `http://localhost:6723`) - every slash command works identically in the browser.

- [Setup Guide](setup-guide.md) - full wizard walkthrough and troubleshooting
- [CLI Reference](cli.md) - every flag and slash command
- [Examples & Demos](examples.md) - video walkthroughs of each mode
- [Hooks, Webhooks & the Daemon](hooks.md) - background and event integration

---
[Back to docs index](README.md) | [Main README](../README.md)
