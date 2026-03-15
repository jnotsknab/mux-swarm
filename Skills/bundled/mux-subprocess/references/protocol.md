# Mux Subprocess Communication Protocol

This document describes the communication protocol for interacting with Mux Swarm CLI subprocesses.

## Overview

The protocol is based on:
1. **stdin/stdout pipes** — Text-based command/response
2. **Sentinel markers** — Unique identifiers for message boundaries
3. **Pattern matching** — Regex-based state detection

## Message Flow

```
┌─────────────┐                    ┌─────────────┐
│   Parent    │                    │   MuxSwarm  │
│   Process   │                    │  Subprocess │
└──────┬──────┘                    └──────┬──────┘
       │                                  │
       │  1. Start process                │
       │  cd <cwd> && <mux_exe>           │
       │ ──────────────────────────────►  │
       │                                  │
       │  2. Wait for MCP bootstrap       │
       │  (5-10 seconds)                  │
       │  ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ► │
       │                                  │
       │  3. Enter chat interface         │
       │  "/agent\n"                      │
       │ ──────────────────────────────►  │
       │                                  │
       │  4. Ready indicator              │
       │  ◄────────────────────────────── │
       │                                  │
       │  5. Send message + sentinel      │
       │  "What is X?\necho __SENTINEL__" │
       │ ──────────────────────────────►  │
       │                                  │
       │  6. Response + sentinel echo     │
       │  ◄────────────────────────────── │
       │                                  │
       │  7. Exit chat                    │
       │  "/qc\n"                         │
       │ ──────────────────────────────►  │
       │                                  │
       │  8. Terminate                    │
       │  SIGTERM / SIGKILL               │
       │ ──────────────────────────────►  │
       │                                  │
```

## Command Reference

### Startup Commands

| Command | Purpose | Expected Response |
|---------|---------|-------------------|
| `cd <cwd> && <mux_exe>` | Launch subprocess | Process starts, MCP servers begin loading |
| — | MCP bootstrap | 5-10 second delay, then ready |

### Chat Interface Commands

| Command | Purpose | Expected Response |
|---------|---------|-------------------|
| `/agent` | Enter chat interface | "Agent interface ready" or prompt |
| `/qc` | Exit chat interface | Returns to CLI prompt |
| `/swap` | List available agents | Numbered list, "Enter number:" |
| `<number>` | Select agent from list | "Selected: <agent_name>" |
| `<message>` | Send message to agent | Agent response |

### Sentinel Protocol

To reliably detect response boundaries, use sentinel markers:

```
SENTINEL = __MUX_SUBPROCESS_SENTINEL_<UUID>__

# Send
stdin: "What is the capital of France?\necho __MUX_SUBPROCESS_SENTINEL_abc123__"

# Receive
stdout: "The capital of France is Paris.\n__MUX_SUBPROCESS_SENTINEL_abc123__"
```

The sentinel is:
1. **Unique** — UUID ensures no collision with agent output
2. **Detectable** — Regex pattern matches exactly
3. **Clean** — Stripped from final response

## State Machine

```
┌─────────────┐
│   STOPPED   │
└──────┬──────┘
       │ start()
       ▼
┌─────────────┐
│  BOOTSTRAP  │ ──timeout──► ERROR
└──────┬──────┘
       │ wait_for_ready()
       ▼
┌─────────────┐
│    READY    │
└──────┬──────┘
       │ enter_chat()
       ▼
┌─────────────┐
│  IN_CHAT    │ ◄─── send() ───►
└──────┬──────┘
       │ exit_chat()
       ▼
┌─────────────┐
│    READY    │
└──────┬──────┘
       │ terminate()
       ▼
┌─────────────┐
│   STOPPED   │
└─────────────┘
```

## Error States

| Error | Cause | Recovery |
|-------|-------|----------|
| `FileNotFoundError` | MuxSwarm binary not found | Check path, use detect() |
| `MCPTimeoutError` | MCP servers didn't init | Increase timeout, check MCP config |
| `AgentNotReadyError` | send() before enter_chat() | Call enter_chat() first |
| `SentinelTimeoutError` | No response within timeout | Agent may be slow, increase timeout |
| `SubprocessError` | Process crashed | Check logs, restart |

## Output Patterns

### MCP Bootstrap Indicators

Look for these patterns to detect MCP server initialization:

```
- "MCP server .* started"
- "Connected to .* MCP"
- "Initializing MCP servers"
- "Ready for input"
- ">"
```

### Agent List Pattern

Output from `/swap` command:

```
Available agents:
  1. CodeAgent
  2. ResearchAgent
  3. WriterAgent
Enter number to select:
```

Parse with regex: `r'\s*(\d+)[.\)]\s*(.+)'`

### Response Pattern

Agent responses typically:
- Start after a prompt indicator
- End before the next prompt or sentinel
- May span multiple lines
- Can include code blocks, markdown, etc.

## Threading Model

The wrapper uses two threads:

1. **Main Thread** — Sends commands, waits for responses
2. **Reader Thread** — Continuously reads stdout, queues lines

```
┌──────────────┐     Queue      ┌──────────────┐
│ Main Thread  │ ◄───────────── │Reader Thread │
│              │                │              │
│ - send()     │                │ - readline() │
│ - _read_     │                │ - queue.put()│
│   until()    │                │              │
└──────────────┘                └──────────────┘
```

This ensures:
- No deadlock from blocking reads
- Output is captured even during waits
- Clean shutdown via stop event

## Platform Differences

### Windows

```powershell
# Launch
cd C:\Path\To\MuxSwarm
.\MuxSwarm.exe

# Process termination uses
# SIGTERM (Ctrl+C) or SIGKILL (taskkill /F)
```

### Linux/macOS

```bash
# Launch
cd /path/to/mux-swarm
./MuxSwarm

# Process termination uses
# SIGTERM (kill) or SIGKILL (kill -9)
```

## Best Practices

1. **Always set working directory** — Relative paths depend on it
2. **Wait for MCP bootstrap** — Don't send commands too early
3. **Use sentinels** — Reliable boundary detection
4. **Handle timeouts gracefully** — Agents may be slow
5. **Clean shutdown** — Exit chat before terminate
6. **Log everything** — Debug subprocess issues easily

## Example Session

```python
from mux_subprocess import MuxSubprocess

# Auto-detect and launch
with MuxSubprocess.detect() as mux:
    # Wait for MCP servers
    mux.wait_for_ready(timeout=15)
    
    # Enter chat
    mux.enter_chat()
    
    # Send message
    response = mux.send("List the files in the current directory")
    print(response)
    
    # List agents
    agents = mux.list_agents()
    print(f"Available: {agents}")
    
    # Swap to different agent
    mux.swap_agent(2)  # Select second agent
    
    # Send to new agent
    response = mux.send("Write a haiku about coding")
    print(response)
    
    # Exit and terminate (automatic with context manager)
```
