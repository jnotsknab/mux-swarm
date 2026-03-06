---
name: docker-admin
description: Build, manage, and configure Docker images and containers. Use when creating new sandbox environments, adding packages to existing images, or managing the Docker image catalog.
---

# Docker Admin Skill (Cross-Platform Native Shell)

Use this skill when you need to create new Docker images, add packages
to existing ones, manage containers, or troubleshoot Docker issues.

This is NOT for running code --- use the `docker-sandbox` skill for
that.

------------------------------------------------------------------------

# Host System: {{os}}

The host OS may be:

-   **Windows** → native PowerShell
-   **macOS** → native bash/zsh
-   **Linux** → native bash

Use the OS-native shell. Do NOT require PowerShell Core on macOS/Linux.

------------------------------------------------------------------------

# NAS Mount Behavior (Platform Aware)

Docker mount behavior differs by platform:

## Windows (Docker Desktop + WSL2)

When mounting NAS paths on Windows, Docker Desktop may incorrectly
resolve mounts when called from PowerShell.

**Rule:**

-   Containers that need NAS access MUST be created via:

```{=html}
<!-- -->
```
    wsl -u root docker run ...

This ensures Docker sees the correct filesystem context.

------------------------------------------------------------------------

## macOS / Linux

On macOS and Linux, Docker mounts work normally.

Use standard:

    docker run ...

No WSL indirection is required.

------------------------------------------------------------------------

# Volume Mount Rules

## When container NEEDS NAS access

### Windows

``` powershell
wsl -u root docker run -d --name <n> -v "{{paths.sandbox}}:/nas" ubuntu:22.04 tail -f /dev/null
wsl -u root docker exec <n> ls -la /nas
```

### macOS / Linux

``` bash
docker run -d --name <n> -v "{{paths.sandbox}}:/nas" ubuntu:22.04 tail -f /dev/null
docker exec <n> ls -la /nas
```

------------------------------------------------------------------------

## When container does NOT need NAS

All platforms may use plain docker:

``` bash
docker run --rm -v "{{paths.skills}}:/workspace" python-runtime-image python /workspace/script.py
```

(Use PowerShell syntax on Windows.)

------------------------------------------------------------------------

# NEVER Use

-   Raw UNC paths in `-v`
-   Mapped drive letters in `-v`
-   Windows-only assumptions on macOS/Linux
-   Plain `docker run` for NAS mounts on Windows

------------------------------------------------------------------------

# Verify NAS Mount

Always verify after container creation.

## Windows

``` powershell
wsl -u root docker exec <n> ls -la /nas
```

If empty:

``` powershell
wsl -u root ls {{paths.sandbox}}
# If needed:
wsl -u root mount {{paths.sandbox}}
```

## macOS / Linux

``` bash
docker exec <n> ls -la /nas
```

------------------------------------------------------------------------

# Creating a New Image

## 1️⃣ Write the Dockerfile

Example:

``` dockerfile
FROM python:3.12-slim

RUN pip install --no-cache-dir \
    package-one \
    package-two \
    package-three

WORKDIR /workspace
```

Key rules:

-   Always use `--no-cache-dir` with pip
-   Prefer `-slim` base images
-   Always set `WORKDIR /workspace`
-   Install system packages BEFORE pip when needed

Example with apt:

``` dockerfile
FROM python:3.12-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    libmagic1 \
    ffmpeg \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir some-package

WORKDIR /workspace
```

------------------------------------------------------------------------

## 2️⃣ Build the Image

All platforms:

``` bash
docker build -t <image-name> <path-to-directory-containing-Dockerfile>
```

Naming convention:

-   Use descriptive names for images
-   lowercase
-   dashes only

------------------------------------------------------------------------

## 3️⃣ Verify the Image

``` bash
docker run --rm <image-name> python -c "import package_one; print('OK')"
```

------------------------------------------------------------------------

# Extending an Existing Image

``` dockerfile
FROM python-runtime-image

RUN pip install --no-cache-dir \
    new-package-one \
    new-package-two

WORKDIR /workspace
```

------------------------------------------------------------------------

# Long-Running Containers

## Windows

``` powershell
wsl -u root docker run -d --name git-container -v "{{paths.sandbox}}:/nas" ubuntu:22.04 tail -f /dev/null
wsl -u root docker exec git-container ls -la /nas
```

## macOS / Linux

``` bash
docker run -d --name git-container -v "{{paths.sandbox}}:/nas" ubuntu:22.04 tail -f /dev/null
docker exec git-container ls -la /nas
```

------------------------------------------------------------------------

# Managing Images

## List images

``` bash
docker images --filter "reference=*" --format "{{.Repository}}:{{.Tag}} ({{.Size}}) Created: {{.CreatedSince}}"
```

## Remove image

``` bash
docker rmi <image-name>
```

## Check installed packages

``` bash
docker run --rm python-runtime-image pip list
docker run --rm node-runtime-image npm list -g
```

## Cleanup

``` bash
docker system prune -f
```

------------------------------------------------------------------------

# Troubleshooting

## Volume mount empty (Windows)

-   Ensure use of `wsl -u root docker run`
-   Verify WSL mount active
-   Never use UNC or mapped drives

## Volume mount empty (macOS/Linux)

-   Verify path exists on host
-   Verify Docker has filesystem permissions

## Build fails on pip install

-   Check package spelling
-   Install required system libraries first

## Image too large

-   Use `-slim` base
-   Chain RUN commands
-   Use `--no-install-recommends`
-   Clean apt lists
-   Use `--no-cache-dir`

------------------------------------------------------------------------

# Rules

-   NAS-mounted containers on Windows MUST use `wsl -u root docker run`
-   macOS/Linux use normal docker run
-   Use clear, descriptive image names
-   Always use `--no-cache-dir` with pip
-   Prefer `-slim` base images
-   Always verify NAS mounts
-   Never store credentials in images
-   Never use `--privileged`
-   Never use UNC or mapped drives in `-v`
-   Report final image name and size after building

------------------------------------------------------------------------

# Platform Summary

  Component      Windows             macOS          Linux
  -------------- ------------------- -------------- --------------
  Shell          PowerShell          bash/zsh       bash
  NAS mount      via WSL             native         native
  Docker run     wsl wrapper (NAS)   native         native
  Verification   wsl docker exec     docker exec    docker exec
  Build          docker build        docker build   docker build
