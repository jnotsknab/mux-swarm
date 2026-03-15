---
name: mux-subprocess
description: Launch and interact with Mux Swarm CLI subprocesses. Use when you need to spawn a separate Mux Swarm instance, communicate with agents programmatically, or run autonomous sessions in the background. Triggers on requests like "launch a mux subprocess", "start a background mux session", "talk to another agent instance", or "run mux swarm as a subprocess".
---

# Mux Subprocess

Launch and manage Mux Swarm CLI instances as subprocesses with full bidirectional communication. Enables spawning independent agent sessions, switching between agents, and orchestrating multi-agent workflows from a parent process.

## Core Concepts

### Subprocess Lifecycle

1. **Spawn** — Launch MuxSwarm.exe with working directory set to install path
2. **Bootstrap** — Wait 5-10 seconds for MCP servers to initialize
3. **Enter Chat** — Send `/agent` to enter the agent chat interface
4. **Interact** — Send messages, receive responses
5. **Swap Agents** — Use `/swap` to list agents, send number to select
6. **Exit** — Send `/qc` to exit chat interface, then terminate process

### Platform Detection

| | Windows | Linux / macOS |
|---|---|---|
| **Binary** | `MuxSwarm.exe` | `MuxSwarm` |
| **Shell** | `powershell.exe` | `bash` |
| **Path separator** | `\` | `/` |

## Critical Requirements

**Working Directory MUST be set to MuxSwarm install folder before launch.** This ensures configs, skills, MCP servers, and all relative paths resolve correctly.

```
# WRONG — paths will not resolve
MuxSwarm.exe

# RIGHT — cd first, then launch
cd C:\Path\To\MuxSwarm && .\MuxSwarm.exe
```

**MCP Bootstrap Delay** — MCP servers take 5-10 seconds to initialize on startup. The subprocess is not ready to accept commands until this completes. The wrapper script handles this automatically.

## Wrapper Script

Use `scripts/mux_subprocess.py` for all subprocess interactions. It handles:
- Platform detection (Windows/Unix)
- Working directory setup
- MCP bootstrap delay
- Bidirectional communication
- Agent swapping
- Graceful shutdown

### Basic Usage

```python
from mux_subprocess import MuxSubprocess

# Launch with explicit paths
mux = MuxSubprocess(
    mux_exe="C:/Users/suspiria/AppData/Local/Mux-Swarm/MuxSwarm.exe",
    cwd="C:/Users/suspiria/AppData/Local/Mux-Swarm"
)

# Or auto-detect from allowed directories
mux = MuxSubprocess.detect()

# Wait for MCP servers to initialize
mux.wait_for_ready(timeout=15)

# Enter agent chat interface
mux.enter_chat()

# Send a message and get response
response = mux.send("What is the capital of France?")
print(response)

# Swap to a different agent
agents = mux.list_agents()  # Returns list of available agents
mux.swap_agent(2)  # Select by index (1-based)

# Exit chat interface
mux.exit_chat()

# Terminate subprocess
mux.terminate()
```

### Context Manager

```python
with MuxSubprocess.detect() as mux:
    mux.wait_for_ready()
    mux.enter_chat()
    response = mux.send("Summarize the files in the sandbox")
    print(response)
    # Automatically exits chat and terminates on context exit
```

### Async Operation

For long-running autonomous sessions:

```python
mux = MuxSubprocess.detect()
mux.wait_for_ready()
mux.enter_chat()

# Start a long task
mux.send_async("Research the history of quantum computing and write a report")

# Check status periodically
while not mux.is_complete():
    time.sleep(30)
    print(f"Still working... last output: {mux.peek_output()}")

result = mux.get_result()
mux.terminate()
```

## Command Reference

| Command | Purpose | Response |
|---|---|---|
| `/agent` | Enter agent chat interface | Ready indicator |
| `/swap` | List available agents | Numbered list |
| `<number>` | Select agent from list | Confirmation |
| `/qc` | Exit chat interface | Returns to CLI prompt |

## Communication Pattern

The wrapper uses a prompt-based protocol:

1. **Send message** — Write to stdin, append unique sentinel marker
2. **Wait for sentinel** — Read stdout until sentinel appears in output
3. **Extract response** — Strip sentinel, return captured output

Sentinel format: `__MUX_SUBPROCESS_SENTINEL_<UUID>__`

This ensures clean message boundaries even when the subprocess outputs multiple lines.

## Error Handling

```python
try:
    mux = MuxSubprocess.detect()
    mux.wait_for_ready(timeout=15)
except TimeoutError:
    print("MCP servers failed to initialize within timeout")
except FileNotFoundError:
    print("MuxSwarm binary not found")
except subprocess.SubprocessError as e:
    print(f"Subprocess error: {e}")
```

## Multiple Subprocesses

You can spawn multiple independent Mux instances:

```python
researcher = MuxSubprocess.detect(name="researcher")
writer = MuxSubprocess.detect(name="writer")

researcher.wait_for_ready()
writer.wait_for_ready()

researcher.enter_chat()
writer.enter_chat()

# Delegate tasks to each
researcher.send_async("Research topic X")
writer.send_async("Draft outline for topic Y")

# Collect results
research_result = researcher.get_result()
writer_result = writer.get_result()
```

## Logging

All subprocess I/O is logged to `{{paths.sandbox}}/mux-subprocess-logs/<name>.log`:

```
2025-01-15 10:30:00 [SPAWN] PID=12345
2025-01-15 10:30:05 [READY] MCP servers initialized
2025-01-15 10:30:06 [SEND] /agent flush
2025-01-15 10:30:07 [RECV] Agent interface ready
2025-01-15 10:30:10 [SEND] What is the capital of France?
2025-01-15 10:30:12 [RECV] The capital of France is Paris.
2025-01-15 10:31:00 [EXIT] /qc
2025-01-15 10:31:01 [TERMINATE] PID=12345
```

## Tuning Output Patterns

The wrapper uses regex patterns to detect ready states and parse responses. These may need adjustment based on actual MuxSwarm output:

- **MCP ready pattern**: `r"(ready|mcp.*started|server.*connected|initialized)"`
- **Agent list pattern**: `r"\s*(\d+)[.)]\s*(.+)"`
- **Chat ready pattern**: `r"(agent.*ready|ready for input|>)"`

If patterns don't match, check the log file at `{{paths.sandbox}}/mux-subprocess-logs/<name>.log` to see actual output and adjust patterns in `scripts/mux_subprocess.py`.

## Limitations

### Agent Swapping (`/swap`)

The `/swap` command uses an interactive terminal prompt (Prompt Toolkit) that **does not work with stdin pipe**. When run non-interactively, it shows:
```
Agent: x Failed to read input in non-interactive mode.
```

**Workarounds:**
1. Use `--interactive` mode (`-i`) to swap agents manually
2. Start MuxSwarm with a specific agent directly (if supported by CLI args)
3. Use a pseudo-terminal (PTY) wrapper like `pexpect` or `winpty`

### Response Filtering

The `send()` method includes a `filter_ui=True` parameter that strips UI chrome (thinking spinners, headers, separators) from responses. Set `filter_ui=False` to get raw output.

## Reference

See `references/protocol.md` for detailed communication protocol specification.
