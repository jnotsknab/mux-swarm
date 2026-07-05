<a id="readme-top"></a>

<div align="center">

<img alt="mux-swarm" src="docs/assets/logo.svg" width="120">

<h1>Mux-Swarm</h1>
<p><b>An operating system for your agents.</b></p>
<p>A self-contained, single-binary multi-agent runtime and harness: specialist agents, native tools, sandboxed execution, deep memory, a web UI, and a scheduling daemon. You bring a model; Mux-Swarm brings the rest. One static binary, nothing to install. Cross-platform: Windows, Linux, and macOS.</p>

[![Build](https://img.shields.io/badge/CI-Depot-blue)](https://depot.dev)
[![.NET](https://img.shields.io/badge/.NET-net10.0-purple)](#)
[![Cross-platform](https://img.shields.io/badge/cross--platform-Windows%20%7C%20Linux%20%7C%20macOS-informational)](#)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](#license)

<a href="#quickstart"><strong>Quickstart »</strong></a>
&nbsp;·&nbsp;
<a href="docs/README.md">Docs</a>
&nbsp;·&nbsp;
<a href="docs/cli.md">Commands</a>
&nbsp;·&nbsp;
<a href="docs/configuration.md">Configuration</a>
&nbsp;·&nbsp;
<a href="docs/examples.md">Examples</a>
&nbsp;·&nbsp;
<a href="docs/architecture.md">Architecture</a>

</div>

---

<img src="docs/assets/banner.svg" alt="mux-swarm banner" width="100%">

## Demo

[https://github.com/user-attachments/assets/3c40809c-93d9-4b8b-b090-736546a6461f](https://github.com/user-attachments/assets/3e817e6b-d339-4016-a386-23b9bfe4b72d)
> **See more in action:** the [Examples and Demos](docs/examples.md) page has video walkthroughs of parallel swarm execution, autonomous runs, and real-world use cases.

## Highlights

**Not just a coding tool.** Mux-Swarm operates your computer the way you would: research, refactor, run pipelines, watch files, hit APIs, drive your editor, and talk to other machines. Anything you can do on your machine, an agent can do here, and with a little configuration, much more.

- **An OS for agents, in one binary.** TUI, web server, scheduling daemon, native tools, and sandbox drivers ship in a single static binary. Built in C#. No Python, no `node_modules`, no venv, nothing to install.
- **Agents that operate the real machine.** Native in-process REPL, shell, and file tools with per-sub-agent scope isolation, no MCP subprocess forked per call.
- **Agents in every gear.** One agent interactively, on-demand delegation with `/ultra`, persistent `/giga` teams with a shared task board, or a [`/swarm`](docs/cli.md) for batch and whole-codebase work.
- **Pick your blast radius.** A pluggable sandbox runs commands bare, in [Docker or Podman, or behind gVisor and Kata microVMs](docs/sandbox.md), switchable per session with `/sandbox` and a deny-by-default network allowlist.
- **Memory that compounds.** [Layered memory](docs/memory.md) (behavioral, factual, knowledge graph, vector) plus an opt-in deep-reflection mode that distills each session and injects it back, mid-turn and across runs.
- **Sign in with the subscriptions you already have.** A bundled proxy signs into Claude, Codex, Kimi, xAI, or Antigravity with `/login`, no API keys in config. Any OpenAI-compatible provider works too.
- **Runs while you sleep.** A built-in [daemon](docs/hooks.md) fires whole multi-agent pipelines on cron, file-watch, status, or webhook triggers, with OS service registration for always-on hosts.
- **A web UI from the same binary.** [`--serve`](docs/serve-api.md) gives live agent streams, a node graph, and session management behind your own auth, plus an HTTP and WebSocket API to [drive it from anything](docs/automation.md).
- **MCP-native, in your editor too.** Attach any [MCP](https://modelcontextprotocol.io/) server to any agent, extend with a hot-reloadable [skills system](docs/configuration.md), and connect from Zed over [ACP](docs/acp.md).
## Quickstart

**Prerequisites:** an OpenAI-compatible LLM provider (or subscription sign-in via `/login`), [Node/npx](https://nodejs.org/) for Node-based [MCP](https://modelcontextprotocol.io/) servers, and [uv/uvx](https://docs.astral.sh/uv/) for Python-based MCP servers. See the [Install guide](docs/install.md) for the full matrix and caveats.

**Install (Linux / macOS):**
```bash
curl -fsSL https://www.muxswarm.dev/install.sh | bash
```

**Install (Windows, PowerShell):**
```powershell
irm https://www.muxswarm.dev/install.ps1 | iex
```

**First run:**
```bash
mux-swarm                 # interactive TUI
mux-swarm --serve         # embedded web UI at http://localhost:6723
mux-swarm --goal "Summarize the shareholder data in my sandbox and save a report"
```

On first launch the `/setup` wizard walks you through configuration. The easiest path is subscription sign-in with `/login`. New here? Follow the [Getting Started tutorial](docs/getting-started.md).

## About

**Mux-Swarm** is a configurable agentic operating system that runs alongside your OS. It is not an agentic chat interface, it is an execution environment for AI agents, with process management, crash recovery, multi-tenant isolation, layered memory, and a workflow engine.

Out of the box it ships a general-purpose swarm of specialized agents (research, coding, analysis, automation, system operations) coordinated by an orchestrator that delegates work, manages results, and executes multi-step objectives. The real versatility comes from the [configuration-driven architecture](docs/configuration.md): define custom swarms, agent roles, prompts, MCP servers, skills, and execution policies entirely through config files. Swap providers, redesign topologies, or adapt the runtime for anything from personal workflows to enterprise pipelines, all without modifying code.

## Documentation

Full documentation lives in [`docs/`](docs/README.md).

**Get started**
- [Install](docs/install.md) - install script, build from source, prerequisites, service registration
- [Getting Started](docs/getting-started.md) - a short first-run tutorial
- [Setup Guide](docs/setup-guide.md) - full first-time configuration and messaging bridges
- [Examples](docs/examples.md) - mode-by-mode demos

**Reference**
- [CLI and Commands](docs/cli.md) - every CLI flag and slash command
- [Configuration](docs/configuration.md) - full `config.json` and `swarm.json` schema
- [Serve and API](docs/serve-api.md) - web UI, HTTP `/api`, and `/ws` protocol

**Guides and concepts**
- [Workflows](docs/workflows.md) - deterministic workflow files
- [Hooks, Webhooks, and Daemon](docs/hooks.md) - lifecycle hooks and automation triggers
- [Sandbox and Security](docs/sandbox.md) - execution backends and production posture
- [ACP](docs/acp.md) - Zed editor integration
- [Architecture](docs/architecture.md) - orchestration lifecycle and design invariants
- [Memory](docs/memory.md) - layered and deep-memory systems

## Architecture at a glance

An orchestrator receives a goal, delegates scoped subtasks to specialized agents, collects results, and drives multi-step objectives to completion. Agents interact with the world through MCP tools, native Filesystem and Shell/REPL tools, and dynamically loaded skills. State persists across restarts through serializable sessions and a layered memory system. For the full picture, see [Architecture](docs/architecture.md) and [Memory](docs/memory.md).

## Protocols and standards

Mux-Swarm is MCP-native and provider-agnostic. It speaks the [OpenAI-compatible API](https://platform.openai.com/docs/api-reference), the [Model Context Protocol](https://modelcontextprotocol.io/), the [Zed Agent Client Protocol](docs/acp.md), and the [Agent Skills (SKILL.md)](https://docs.anthropic.com/en/docs/claude-code/skills) format. It is built on [.NET 10](https://dotnet.microsoft.com/), [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/ai-extensions), and [Microsoft.Agents.AI](https://github.com/microsoft/Agents-for-net).

## Roadmap

**Up next:** additional platform bridges (Slack, Matrix, Signal), expanded OpenTelemetry coverage.

Recent releases include teams and subscription sign-in, native tools and pluggable sandboxing, the live TUI, the workflow engine, daemon mode, and event hooks. See the [changelog](RELEASE_NOTES_v0.12.0.md) for details.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines, or open an issue to discuss.

## License

Licensed under the GNU General Public License v3.0. See [LICENSE](LICENSE) for details.
