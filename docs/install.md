# Installation

How to install Mux-Swarm on Windows, Linux, or macOS: prerequisites, install script, build from source, and registering it as an OS service.

---

## Prerequisites

**You need a way for agents to reach an LLM.** There are two paths; pick one:

1. **Subscription sign-in (no API key required).** As of v0.12.1, Mux-Swarm bundles an auto-managed [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) sidecar. Run `/login` inside Mux-Swarm to sign in to Claude, Codex, Kimi, xAI, or Antigravity through your browser - no keys to paste. Use `/ping` to verify connectivity and `/proxy status|update` to manage the sidecar.
2. **An LLM provider API key** for any [OpenAI-compatible](https://platform.openai.com/docs/api-reference) endpoint, preferably set as an environment variable. Local endpoints (e.g. Ollama) work with no key at all.

**Tooling used by the default MCP server config** (not hard requirements - you can opt out during setup, but any MCP servers referenced in your `swarm.json` need their dependencies available):

- **Node / npm** (`npx`) for Node-based [MCP](https://modelcontextprotocol.io/) servers
- **uvx / uv** for Python-based MCP servers
- **BRAVE_API_KEY** environment variable for the [Brave Search MCP](https://brave.com/search/api/) server, the default web search provider. Recommended (it includes AI-generated summaries alongside results), but optional: disable it in `swarm.json` and the swarm falls back to the Fetch MCP server.

> **ChromaDB caveat:** the default config uses the ChromaDB MCP server, which has a known issue with Python 3.14. Configure uv / uvx to use a separate Python version (e.g. 3.12). If you bring your own vector store MCP, this does not apply.

---

## Install via Script (Recommended)

**Linux / macOS:**

```bash
curl -fsSL https://www.muxswarm.dev/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://www.muxswarm.dev/install.ps1 | iex
```

The installer downloads the latest release, installs the runtime locally, and adds `mux-swarm` to your PATH. After it completes, open a new terminal or reload your shell:

```bash
source ~/.bashrc   # or source ~/.zshrc
```

---

## Build From Source

Requires [Git](https://git-scm.com/) and a [.NET SDK](https://dotnet.microsoft.com/download) compatible with net10.0.

```bash
git clone https://github.com/jnotsknab/mux-swarm.git
cd mux-swarm
dotnet build
```

**Run from source:**

```bash
dotnet run --project MuxSwarm.csproj
```

---

## First Run

On first launch, Mux-Swarm detects that setup has not been completed and starts the `/setup` wizard automatically:

```bash
mux-swarm
```

See the [Setup Guide](setup-guide.md) for the full wizard walkthrough, or [Getting Started](getting-started.md) for a quick first-session tutorial.

Common launch modes:

```bash
mux-swarm                  # interactive TUI
mux-swarm --serve          # web UI at http://localhost:6723 (recommended across devices)
mux-swarm --serve --daemon # web UI + background trigger loops
```

---

## OS Service Registration (`--register` / `--remove`)

Register Mux-Swarm as a system service that starts automatically on boot. One command, no manual file editing:

```bash
# Register (run elevated on Windows)
mux-swarm --register --serve --daemon --watchdog

# Remove
mux-swarm --remove
```

The `--register` flag itself is stripped from the service definition - only runtime flags (`--serve`, `--daemon`, `--watchdog`) are forwarded. The binary path and working directory are resolved automatically from the install location (not from shell aliases).

| Platform | Mechanism | Details |
|----------|-----------|---------|
| **Windows** | Task Scheduler (XML) | Boot trigger with 30s delay, `RestartOnFailure` (60s interval, 999 retries), runs before user login, `WorkingDirectory` set |
| **Linux** | systemd user service | `Restart=always`, `RestartSec=10`, `enable-linger` for headless boot (starts before login) |
| **macOS** | launchd LaunchAgent | `RunAtLoad`, `KeepAlive`, logs to `~/.local/share/Mux-Swarm/Logs/` |

Combined with `--watchdog` (process-level restart) and daemon status triggers (subsystem-level restart), this creates a three-layer resilience stack: the OS restarts the service, the watchdog restarts the process, and the daemon restarts monitored subsystems.

---
[Back to docs index](README.md) | [Main README](../README.md)
