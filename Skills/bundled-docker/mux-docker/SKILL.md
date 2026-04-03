---
name: mux-docker
description: Launch and manage Mux Swarm CLI instances in Docker containers with full bidirectional communication. Enables spawning isolated agent sessions, switching between agents, and orchestrating multi-agent workflows in containerized environments. Use when you need process isolation, reproducible environments, or want to run Mux Swarm in a container. Triggers on requests like "launch mux in docker", "start a containerized mux session", "run mux swarm in a container", or "isolated mux environment".
requires_bins: [uv, docker]
---

# Mux Docker

Launch and manage Mux Swarm CLI instances in Docker containers with full bidirectional communication. Enables spawning independent agent sessions, switching between agents, and orchestrating multi-agent workflows from a parent process with container isolation.

## Core Concepts

### Container Lifecycle

1. **Build** — Create Docker image from Dockerfile with MuxSwarm and dependencies
2. **Spawn** — Launch container with mounted volumes for persistence
3. **Bootstrap** — Wait 5-15 seconds for MCP servers to initialize
4. **Enter Chat** — Send `/agent` to enter the agent chat interface
5. **Interact** — Send messages, receive responses
6. **Swap Agents** — Use `/swap` to list agents, send number to select
7. **Exit** — Send `/qc` to exit chat interface, then stop/remove container

### Benefits Over Subprocess

| Feature | Subprocess | Docker |
|---------|-----------|--------|
| **Isolation** | Shared process space | Full container isolation |
| **Reproducibility** | Environment-dependent | Consistent across hosts |
| **Resource Limits** | OS-level only | cgroups, CPU/memory limits |
| **Networking** | Host network | Isolated network stack |
| **Cleanup** | Manual process cleanup | Automatic container removal |
| **Portability** | OS-specific | Platform-agnostic |

## Critical Requirements

**Docker must be installed and running.** Verify with:
```bash
docker --version
docker ps
```

**MuxSwarm Docker image must be built first.** Use the provided Dockerfile:
```bash
docker build -t mux-swarm:latest /path/to/mux-docker/assets
```

**Volume mounts for persistence.** Container state is ephemeral without volumes:
```bash
-v /host/path:/container/path
```

## Wrapper Script

Use `scripts/mux_docker.py` for all Docker interactions. It handles:
- Image building and management
- Container lifecycle (create, start, stop, remove)
- MCP bootstrap delay
- Bidirectional communication via `docker exec`
- Agent swapping
- Graceful shutdown

### Basic Usage

```python
from mux_docker import MuxDocker

# Launch with explicit image
mux = MuxDocker(
    image="mux-swarm:latest",
    name="mux-session-1"
)

# Or auto-build if image doesn't exist
mux = MuxDocker.ensure_image(
    dockerfile_path="path/to/Dockerfile",
    name="mux-session-1"
)

# Start container
mux.start()

# Wait for MCP servers to initialize
mux.wait_for_ready(timeout=20)

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

# Stop and remove container
mux.terminate()
```

### Context Manager

```python
with MuxDocker.ensure_image() as mux:
    mux.wait_for_ready()
    mux.enter_chat()
    response = mux.send("Summarize the files in the sandbox")
    print(response)
    # Automatically exits chat and removes container on context exit
```

### Async Operation

For long-running autonomous sessions:

```python
mux = MuxDocker.ensure_image()
mux.start()
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

## Docker-Specific Features

### Resource Limits

```python
mux = MuxDocker(
    image="mux-swarm:latest",
    cpu_limit=2.0,      # 2 CPU cores
    memory_limit="4g",  # 4GB RAM
    timeout=300         # 5 minute timeout
)
```

### Volume Mounts

```python
mux = MuxDocker(
    image="mux-swarm:latest",
    volumes={
        "/host/sandbox": "/app/sandbox",
        "/host/configs": "/app/configs",
        "/host/skills": "/app/skills"
    }
)
```

### Network Isolation

```python
# Isolated network (default)
mux = MuxDocker(image="mux-swarm:latest", network="bridge")

# Host network (for MCP servers that need localhost access)
mux = MuxDocker(image="mux-swarm:latest", network="host")
```

### Environment Variables

```python
mux = MuxDocker(
    image="mux-swarm:latest",
    env_vars={
        "ANTHROPIC_API_KEY": "sk-...",
        "OPENAI_API_KEY": "sk-...",
        "MUX_LOG_LEVEL": "DEBUG"
    }
)
```

## Command Reference

| Command | Purpose | Response |
|---|---|---|
| `/agent` | Enter agent chat interface | Ready indicator |
| `/swap` | List available agents | Numbered list |
| `<number>` | Select agent from list | Confirmation |
| `/qc` | Exit chat interface | Returns to CLI prompt |

## Communication Pattern

The wrapper uses `docker exec` for command execution:

1. **Send message** — `docker exec -i <container> mux-send "<message>"`
2. **Wait for response** — Poll container logs or use sentinel markers
3. **Extract response** — Parse output, strip sentinel

Sentinel format: `__MUX_DOCKER_SENTINEL_<UUID>__`

This ensures clean message boundaries even when the container outputs multiple lines.

## Error Handling

```python
try:
    mux = MuxDocker.ensure_image()
    mux.start()
    mux.wait_for_ready(timeout=20)
except DockerNotFoundError:
    print("Docker is not installed or not running")
except ImageNotFoundError:
    print("MuxSwarm Docker image not found")
except MCPTimeoutError:
    print("MCP servers failed to initialize within timeout")
except ContainerError as e:
    print(f"Container error: {e}")
```

## Multiple Containers

You can spawn multiple independent Mux containers:

```python
researcher = MuxDocker(image="mux-swarm:latest", name="researcher")
writer = MuxDocker(image="mux-swarm:latest", name="writer")

researcher.start()
writer.start()

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

# Cleanup
researcher.terminate()
writer.terminate()
```

## Logging

All container I/O is logged to `\\banknas\Public\Jb\MuxSandboxV0.4.0/mux-docker-logs/<name>.log`:

```
2025-01-15 10:30:00 [CREATE] Container=mux-session-1
2025-01-15 10:30:05 [START] Container started
2025-01-15 10:30:10 [READY] MCP servers initialized
2025-01-15 10:30:11 [SEND] /agent flush
2025-01-15 10:30:12 [RECV] Agent interface ready
2025-01-15 10:30:15 [SEND] What is the capital of France?
2025-01-15 10:30:17 [RECV] The capital of France is Paris.
2025-01-15 10:31:00 [EXIT] /qc
2025-01-15 10:31:01 [REMOVE] Container removed
```

## Dockerfile

The bundled Dockerfile (`assets/Dockerfile`) creates a minimal image with:

- Python 3.12 runtime
- MuxSwarm CLI installation
- Required dependencies (uv, MCP servers)
- Default configuration

Build manually:
```bash
docker build -t mux-swarm:latest C:\Users\suspiria\AppData\Local\Mux-Swarm\Skills\bundled\mux-docker\assets
```

## Limitations

### Docker Desktop on Windows

- WSL2 backend recommended for best performance
- File system mounts may be slower than native
- Ensure Docker Desktop is running before launching containers

### Container Overhead

- Container startup adds ~2-5 seconds overhead vs subprocess
- MCP bootstrap may take longer in container (10-15 seconds typical)
- Resource limits may impact agent performance if set too low

### Volume Mounts

- Windows paths must be converted to Docker format: `C:\path` → `/host/path`
- Permissions may require adjustment for mounted volumes
- Sandbox directory must be explicitly mounted for persistence

## Reference

See `references/docker-protocol.md` for detailed Docker communication protocol specification.
