# Mux Swarm Docker Image
# Provides isolated containerized environment for Mux Swarm CLI

FROM python:3.12-slim

LABEL maintainer="Mux Swarm"
LABEL description="Mux Swarm CLI in Docker container"

# Set environment variables
ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1
ENV UV_SYSTEM_PYTHON=1

# Install system dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    git \
    build-essential \
    && rm -rf /var/lib/apt/lists/*

# Install uv for fast Python package management
RUN curl -LsSf https://astral.sh/uv/install.sh | sh
ENV PATH="/root/.local/bin:$PATH"

# Create app directory
WORKDIR /app

# Create necessary directories
RUN mkdir -p /app/sandbox \
    /app/configs \
    /app/skills \
    /app/sessions \
    /app/logs

# Copy Mux Swarm installation (if building from source)
# This assumes MuxSwarm is built and available in the build context
# Adjust as needed for your deployment method
COPY mux-swarm/ /app/mux-swarm/

# Install Mux Swarm and dependencies
RUN if [ -f /app/mux-swarm/pyproject.toml ]; then \
        cd /app/mux-swarm && uv pip install --system -e .; \
    fi

# Or install from PyPI (uncomment if available)
# RUN uv pip install --system mux-swarm

# Set default working directory
WORKDIR /app

# Default command - start Mux Swarm CLI
CMD ["mux-swarm"]
