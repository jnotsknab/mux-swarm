# Mux Docker Communication Protocol

This document describes the communication protocol for interacting with Mux Swarm CLI in Docker containers.

## Overview

The protocol is based on:
1. **Docker SDK** — Python docker library for container management
2. **docker exec** — Command execution inside running containers
3. **Container logs** — Streaming output via docker logs API
4. **Sentinel markers** — Unique identifiers for message boundaries

## Container Lifecycle

```
┌─────────────┐                    ┌─────────────┐
│   Host      │                    │  Docker     │
│   Process   │                    │  Container  │
└──────┬──────┘                    └──────┬──────┘
       │                                  │
       │  1. Create container             │
       │  docker.containers.run()         │
       │ ──────────────────────────────►  │
       │                                  │
       │  2. Wait for MCP bootstrap       │
       │  (10-15 seconds in container)    │
       │  ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ► │
       │                                  │
       │  3. Enter chat interface         │
       │  docker exec ... /agent          │
       │ ──────────────────────────────►  │
       │                                  │
       │  4. Ready indicator              │
       │  ◄────────────────────────────── │
       │                                  │
       │  5. Send message + sentinel      │
       │  docker exec ... message         │
       │ ──────────────────────────────►  │
       │                                  │
       │  6. Response + sentinel echo     │
       │  ◄────────────────────────────── │
       │                                  │
       │  7. Exit chat                    │
       │  docker exec ... /qc             │
       │ ──────────────────────────────►  │
       │                                  │
       │  8. Stop & remove container      │
       │  container.stop() / remove()     │
       │ ──────────────────────────────►  │
       │                                  │
```

## Docker SDK Operations

### Container Management

```python
import docker

client = docker.from_env()

# Create and start container
container = client.containers.run(
    image="mux-swarm:latest",
    name="mux-session-1",
    detach=True,
    stdin_open=True,
    tty=True,
    volumes={
        "/host/sandbox": {"bind": "/app/sandbox", "mode": "rw"},
        "/host/configs": {"bind": "/app/configs", "mode": "rw"}
    },
    environment={
        "ANTHROPIC_API_KEY": "sk-...",
        "MUX_LOG_LEVEL": "DEBUG"
    },
    cpu_quota=200000,  # 2 CPUs
    mem_limit="4g",     # 4GB RAM
    network="bridge",
    auto_remove=True
)

# Stop container
container.stop(timeout=5)

# Force kill
container.kill()

# Remove container (if not auto_remove)
container.remove()
```

### Command Execution

```python
# Execute command in running container
exit_code, output = container.exec_run(
    cmd="sh -c 'mux-swarm --message \"Hello\"'",
    timeout=30
)

# Stream output
for line in container.logs(stream=True, follow=True):
    print(line.decode('utf-8'))
```

## Communication Methods

### Method 1: docker exec

Direct command execution via `docker exec`:

```python
# Send command
exit_code, output = container.exec_run(
    cmd=f"sh -c 'echo \"{message}\" | mux-swarm'",
    timeout=timeout
)

# Parse response
response = output.decode('utf-8')
```

**Pros:**
- Simple, synchronous API
- No need for sentinel markers (timeout-based)
- Works with any container configuration

**Cons:**
- Each command creates new process
- Overhead of exec setup
- Harder to handle long-running responses

### Method 2: Container Logs Streaming

Stream container stdout via logs API:

```python
# Start container with command
container = client.containers.run(
    image="mux-swarm:latest",
    command="mux-swarm --interactive",
    detach=True
)

# Stream logs
for line in container.logs(stream=True, follow=True):
    line = line.decode('utf-8')
    # Process line
    if sentinel_marker in line:
        # Response complete
        break
```

**Pros:**
- Single long-running process
- Efficient for multiple messages
- Natural fit for interactive sessions

**Cons:**
- Requires sentinel markers
- More complex state management
- Need background thread for reading

### Method 3: Named Pipes / Unix Sockets

Advanced method using volume-mounted sockets:

```python
# Mount socket directory
container = client.containers.run(
    image="mux-swarm:latest",
    volumes={
        "/tmp/mux-sockets": {"bind": "/sockets", "mode": "rw"}
    }
)

# Host writes to socket
# Container reads from socket
# Bidirectional communication via socket
```

**Pros:**
- True bidirectional communication
- Low latency
- Efficient for high-throughput

**Cons:**
- Complex setup
- Platform-specific (Unix sockets on Linux/Mac, named pipes on Windows)
- Requires container-side socket handler

## Sentinel Protocol

To reliably detect response boundaries when streaming logs:

```
SENTINEL = __MUX_DOCKER_SENTINEL_<UUID>__

# Send
exec: "What is the capital of France?\necho __MUX_DOCKER_SENTINEL_abc123__"

# Receive (in logs)
"The capital of France is Paris.\n__MUX_DOCKER_SENTINEL_abc123__"
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
| `DockerNotFoundError` | Docker not installed/running | Install/start Docker |
| `ImageNotFoundError` | MuxSwarm image not found | Build image from Dockerfile |
| `MCPTimeoutError` | MCP servers didn't init | Increase timeout, check MCP config |
| `AgentNotReadyError` | send() before enter_chat() | Call enter_chat() first |
| `SentinelTimeoutError` | No response within timeout | Agent may be slow, increase timeout |
| `ContainerError` | Container crashed | Check logs, restart container |

## Volume Mounts

### Windows Host

```python
volumes = {
    r"C:\Users\suspiria\AppData\Local\Mux-Swarm": {
        "bind": "/app/mux-swarm",
        "mode": "rw"
    },
    r"\\banknas\Public\Jb\MuxSandboxV0.4.0": {
        "bind": "/app/sandbox",
        "mode": "rw"
    }
}
```

### Linux/macOS Host

```python
volumes = {
    "/home/user/.local/share/mux-swarm": {
        "bind": "/app/mux-swarm",
        "mode": "rw"
    },
    "/mnt/sandbox": {
        "bind": "/app/sandbox",
        "mode": "rw"
    }
}
```

## Resource Limits

### CPU Limits

```python
# 2 CPU cores
cpu_quota = 200000  # microseconds per period
cpu_period = 100000  # period in microseconds

# In container.run():
cpu_quota=200000
```

### Memory Limits

```python
# 4GB RAM
mem_limit = "4g"

# Or in bytes
mem_limit = 4 * 1024 * 1024 * 1024
```

### Combined Limits

```python
container = client.containers.run(
    image="mux-swarm:latest",
    cpu_quota=200000,      # 2 CPUs
    cpu_period=100000,
    mem_limit="4g",        # 4GB
    memswap_limit="4g",    # Disable swap
    name="mux-limited"
)
```

## Networking

### Bridge Network (Default)

```python
# Isolated network, port mapping required for external access
container = client.containers.run(
    image="mux-swarm:latest",
    network="bridge",
    ports={"8000/tcp": 8000}  # If MCP server needs external access
)
```

### Host Network

```python
# Shares host network stack
container = client.containers.run(
    image="mux-swarm:latest",
    network="host"
)
```

### Custom Network

```python
# Create custom network
network = client.networks.create("mux-network", driver="bridge")

# Connect container
container = client.containers.run(
    image="mux-swarm:latest",
    network="mux-network"
)
```

## Threading Model

The wrapper uses two threads:

1. **Main Thread** — Sends commands, waits for responses
2. **Reader Thread** — Continuously reads container logs, queues lines

```
┌──────────────┐     Queue      ┌──────────────┐
│ Main Thread  │ ◄───────────── │Reader Thread │
│              │                │              │
│ - send()     │                │ - logs()     │
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

- Docker Desktop with WSL2 backend recommended
- Path conversion: `C:\path` → `/host/path` in volume mounts
- Named pipes available but rarely used

### Linux

- Native Docker, best performance
- Unix sockets available for advanced communication
- No path conversion needed

### macOS

- Docker Desktop with VirtioFS for better file performance
- Unix sockets available
- Path conversion: `/Users/path` → `/host/path`

## Best Practices

1. **Use volume mounts** — Persist state across container restarts
2. **Set resource limits** — Prevent runaway resource usage
3. **Wait for MCP bootstrap** — Containers take longer to initialize
4. **Use sentinels** — Reliable boundary detection when streaming
5. **Clean shutdown** — Stop gracefully before removing
6. **Log everything** — Debug container issues easily
7. **Auto-remove** — Set `auto_remove=True` for automatic cleanup

## Example Session

```python
from mux_docker import MuxDocker

# Auto-build image if needed, launch container
with MuxDocker.ensure_image(
    dockerfile_path="./assets",
    volumes={
        "/host/sandbox": "/app/sandbox",
        "/host/configs": "/app/configs"
    },
    cpu_limit=2.0,
    memory_limit="4g"
) as mux:
    # Wait for MCP servers
    mux.wait_for_ready(timeout=20)
    
    # Enter chat
    mux.enter_chat()
    
    # Send message
    response = mux.send("List the files in the sandbox")
    print(response)
    
    # List agents
    agents = mux.list_agents()
    print(f"Available: {agents}")
    
    # Swap to different agent
    mux.swap_agent(2)
    
    # Send to new agent
    response = mux.send("Write a haiku about coding")
    print(response)
    
    # Exit and terminate (automatic with context manager)
```

## Dockerfile Best Practices

```dockerfile
# Use slim base image
FROM python:3.12-slim

# Install only necessary system dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    git \
    && rm -rf /var/lib/apt/lists/*

# Use uv for fast dependency installation
RUN curl -LsSf https://astral.sh/uv/install.sh | sh

# Create non-root user (optional, for security)
RUN useradd -m -s /bin/bash mux
USER mux

# Set working directory
WORKDIR /app

# Copy and install application
COPY . .
RUN uv pip install --system -e .

# Default command
CMD ["mux-swarm"]
```
