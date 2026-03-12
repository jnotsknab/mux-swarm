# Setup Guide

First-time setup walkthrough for Mux-Swarm.

## Prerequisites

The only hard requirement is an **LLM provider API key** (any OpenAI-compatible endpoint) set as an environment variable.

Everything else is config-driven. The default `config.json` ships with MCP servers that rely on **Node / npm** (`npx`) and **uvx / uv**, but these are not hard dependencies — they're just what the bundled MCP servers use. Mux-Swarm doesn't care how your MCP servers run. You can swap in any runtime, point to a binary you built yourself, launch a local MCP server you wrote from scratch, or connect to a remote HTTP/SSE endpoint. If it speaks MCP, it works.

The default configuration also includes:

- **BRAVE_API_KEY** environment variable for the Brave Search MCP server (optional but recommended for research quality)

> **Note:** The default config uses the ChromaDB MCP server which has a known issue with Python 3.14. If you're using the defaults, it is recommended that uv / uvx is configured to use a separate Python version (e.g. 3.12). If you're bringing your own vector store MCP, this doesn't apply to you.

## Installation

**Linux / macOS:**
```bash
curl -fsSL https://www.muxswarm.dev/install.sh | bash
```

**Windows (PowerShell):**
```powershell
irm https://www.muxswarm.dev/install.ps1 | iex
```

After installation completes, open a new terminal or reload your shell:
```bash
source ~/.bashrc   # or source ~/.zshrc
```

## First Run & `/setup`

On first launch, Mux-Swarm detects that setup hasn't been completed and automatically starts the setup wizard:

```bash
mux-swarm
```

The wizard walks you through seven steps:

### Step 1 — Dependency Check

The wizard checks for tooling used by the default MCP server configuration: `python`, `node`, `npm`, `npx`, `uv`, and `uvx`. If anything is missing, the wizard will offer to install it for you. If you're using the default config, accepting is the easiest path. If you've swapped in your own MCP servers or runtimes, missing defaults here won't affect you — the check is informational.

### Step 2 — File System Access

Define the paths your agents are allowed to read and write. Provide one or more comma-separated paths, then select which path to use as the agent output sandbox — this is where agents will write files and artifacts.

```
Paths: ~/mux-sandbox
```

### Step 3 — Storage Configuration

Configure where ChromaDB stores persistent vector data (embeddings, indexes) and where the knowledge graph file lives. Press Enter to accept the defaults inside your sandbox.

### Step 4 — Model Endpoint Configuration

Enter your OpenAI-compatible API endpoint and the environment variable name holding your API key:

```
Endpoint: https://openrouter.ai/api/v1
Env var name: OPENROUTER_API_KEY
```

Raw API keys can be pasted directly (not recommended — they're only held in memory for the current session). Leave blank for local endpoints like Ollama.

### Step 5 — User Profile (Optional)

Tell your agents who you are. This helps agents personalize responses and address you by name. All fields are optional — press Enter to skip any, or type `skip` to skip the entire step.

```
Name: John Doe
Role: systems analyst
Timezone: America/Chicago
Locale: en-US
Info: My preferred language of choice for backend is c#
```

### Step 6 — MCP API Keys

The wizard checks that environment variable names are configured for any MCP servers that require API keys (e.g. Brave Search). Press Enter to keep defaults, or provide alternate env var names.

### Step 7 — MCP Server Validation

Each enabled MCP server is validated for connectivity. On success you'll see a summary like:

```
✓ Loaded 9 tools from Memory
✓ Loaded 14 tools from Filesystem
✓ Loaded 1 tools from Fetch
✓ Loaded 13 tools from ChromaDB
✓ Loaded 6 tools from BraveSearchMCP
✓ Loaded 3 tools from PythonReplMCP
```

> **Troubleshooting:** If a server fails to connect and you're running in strict mode (default), Mux-Swarm will exit. You can disable a problematic server by editing `Configs/Config.json` and setting `"enabled": false` on that server, then re-launch. You can also replace any default MCP server with your own — point to a local binary, a custom runtime, or a remote HTTP/SSE endpoint. As long as it implements the MCP protocol, Mux-Swarm will pick it up. Re-run `/setup` at any time to reconfigure.

## Verifying Your Setup

The real test is whether your agents can make LLM calls end to end. Launch a single-agent session and send a simple prompt:

```bash
mux-swarm
> /agent
> Who are you and what can you help me with?
```

The agent should respond with a personalized greeting (using the name and preferences from your profile) and a summary of its capabilities. If you get a coherent response, your provider, API key, MCP tools, and agent config are all wired correctly. Type `/qc` to exit the session.

For a goal-driven run:
```bash
mux-swarm --goal "List the files in my sandbox and summarize what you find"
```

You can also use `/status` for a quick overview of your runtime configuration — active provider, model assignments, tool count, skill count, and session count:

```
╭─Mux-Swarm Status──────────────────────────────────────────────────╮
│   Provider:    default (https://openrouter.ai/api/v1)             │
│   Agent:       MuxAgent                                           │
│   Models:                                                         │
│                Orchestrator -> google/gemini-3.1-pro-preview      │
│                Compaction -> google/gemini-3-flash-preview        │
│                WebAgent -> google/gemini-3.1-pro-preview          │
│                CodeAgent -> google/gemini-3.1-pro-preview         │
│                MemoryAgent -> google/gemini-3-flash-preview       │
│                DataAnalysisAgent -> google/gemini-3.1-pro-preview │
│   Tools:       46                                                 │
│   Skills:      18                                                 │
│   Sessions:    1                                                  │
│   Docker Exec: disabled                                           │
╰───────────────────────────────────────────────────────────────────╯
```

## Next Steps

- Launch the full multi-agent swarm: `/swarm`
- Try parallel dispatch: `/pswarm` or `mux-swarm --parallel --goal "<goal>"`
- Explore [Configuration](../README.md#configuration) to customize providers, agent roles, model routing, and MCP server scoping
- Add custom [Skills](../README.md#skills-skills) to extend agent capabilities
- Use [Scoped Instances](../README.md#scoped-instances) for multi-user or multi-environment deployments
