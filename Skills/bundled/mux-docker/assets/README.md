# Mux Swarm Docker Image

This directory contains the Dockerfile for building a containerized Mux Swarm CLI environment.

## Building the Image

### Prerequisites

- Docker Desktop installed and running (Windows/macOS)
- Docker Engine (Linux)
- Mux Swarm source code or distribution

### Build Command

```bash
# From this directory
docker build -t mux-swarm:latest .

# Or specify Mux Swarm source location
docker build -t mux-swarm:latest --build-arg MUX_SOURCE=/path/to/mux-swarm .
```

### Build Arguments

| Argument | Default | Description |
|----------|---------|-------------|
| `MUX_SOURCE` | `./mux-swarm` | Path to Mux Swarm source code |

## Running the Container

### Basic Usage

```bash
# Interactive mode
docker run -it --rm mux-swarm:latest

# With volume mounts
docker run -it --rm \
  -v /host/sandbox:/app/sandbox \
  -v /host/configs:/app/configs \
  mux-swarm:latest

# With environment variables
docker run -it --rm \
  -e ANTHROPIC_API_KEY=sk-... \
  -e OPENAI_API_KEY=sk-... \
  mux-swarm:latest
```

### With Resource Limits

```bash
# Limit to 2 CPUs and 4GB RAM
docker run -it --rm \
  --cpus=2 \
  --memory=4g \
  mux-swarm:latest
```

### Network Configuration

```bash
# Host network (for MCP servers that need localhost)
docker run -it --rm --network=host mux-swarm:latest

# Bridge network (default, isolated)
docker run -it --rm --network=bridge mux-swarm:latest
```

## Volume Mounts

### Recommended Mount Points

| Container Path | Purpose | Host Path Example |
|----------------|---------|-------------------|
| `/app/sandbox` | Working directory for agent tasks | `C:\Users\...\MuxSandboxV0.4.0` |
| `/app/configs` | Mux Swarm configuration files | `C:\Users\...\Mux-Swarm\Configs` |
| `/app/skills` | Bundled skills directory | `C:\Users\...\Mux-Swarm\Skills\bundled` |
| `/app/sessions` | Session history | `C:\Users\...\Mux-Swarm\Sessions` |

### Windows Example

```powershell
docker run -it --rm `
  -v C:\Users\suspiria\AppData\Local\Mux-Swarm:/app/mux-swarm `
  -v \\banknas\Public\Jb\MuxSandboxV0.4.0:/app/sandbox `
  mux-swarm:latest
```

### Linux/macOS Example

```bash
docker run -it --rm \
  -v ~/.local/share/mux-swarm:/app/mux-swarm \
  -v /mnt/sandbox:/app/sandbox \
  mux-swarm:latest
```

## Environment Variables

### Required

| Variable | Description |
|----------|-------------|
| `ANTHROPIC_API_KEY` | API key for Claude models |
| `OPENAI_API_KEY` | API key for OpenAI models (if using) |

### Optional

| Variable | Default | Description |
|----------|---------|-------------|
| `MUX_LOG_LEVEL` | `INFO` | Logging level (DEBUG, INFO, WARNING, ERROR) |
| `MUX_CONFIG_PATH` | `/app/configs` | Path to configuration files |
| `MUX_SKILLS_PATH` | `/app/skills` | Path to skills directory |

## Using with Python SDK

```python
from mux_docker import MuxDocker

# Build if needed and run
with MuxDocker.ensure_image(
    dockerfile_path="C:/Users/suspiria/AppData/Local/Mux-Swarm/Skills/bundled/mux-docker/assets",
    volumes={
        r"C:\Users\suspiria\AppData\Local\Mux-Swarm\Configs": "/app/configs",
        r"\\banknas\Public\Jb\MuxSandboxV0.4.0": "/app/sandbox"
    },
    env_vars={
        "ANTHROPIC_API_KEY": "sk-..."
    }
) as mux:
    mux.wait_for_ready()
    mux.enter_chat()
    response = mux.send("Hello from Docker!")
    print(response)
```

## Troubleshooting

### Container Won't Start

```bash
# Check Docker is running
docker ps

# Check logs
docker logs <container-id>

# Run interactively to debug
docker run -it --entrypoint /bin/bash mux-swarm:latest
```

### MCP Servers Not Initializing

```bash
# Increase timeout
docker run -it --rm -e MCP_TIMEOUT=30 mux-swarm:latest

# Check MCP server logs
docker logs <container-id> | grep -i mcp
```

### Permission Issues

```bash
# On Linux, may need to adjust permissions
docker run -it --rm --user $(id -u):$(id -g) mux-swarm:latest
```

### Performance Issues

```bash
# On Windows/macOS, use VirtioFS (Docker Desktop setting)
# Or increase Docker Desktop resources in settings

# Check resource usage
docker stats
```

## Image Size Optimization

The current Dockerfile produces a minimal image. To further reduce size:

```dockerfile
# Use multi-stage build
FROM python:3.12-slim AS builder
# ... build steps ...

FROM python:3.12-slim
COPY --from=builder /app /app
# ... runtime only ...
```

## Security Considerations

1. **API Keys** — Never hardcode API keys in the image. Use environment variables or secrets.
2. **User Permissions** — Consider running as non-root user (add `USER mux` to Dockerfile).
3. **Network Access** — Use bridge network by default, host network only if needed.
4. **Volume Mounts** — Only mount necessary directories with minimal permissions.

## Next Steps

1. Build the image: `docker build -t mux-swarm:latest .`
2. Test the container: `docker run -it --rm mux-swarm:latest`
3. Use with Python SDK: See `scripts/mux_docker.py`
4. Read the protocol: See `references/docker-protocol.md`
