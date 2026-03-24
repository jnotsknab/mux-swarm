---
description: Run code in isolated Docker containers with pre-installed dependencies. Use for all tasks requiring any code execution, specific libraries, or file generation
name: docker-sandbox
---

# Docker Sandbox Skill (Native OS Shell)

> This skill defines baseline execution and storage behavior. It applies
> alongside task-specific skills, not instead of them.

Use this skill when you need to **execute code** or generate files that
require specific runtimes or libraries.

Do NOT use Docker just to write files.\
If only writing to an allowed Filesystem MCP path, use Filesystem MCP
directly.

------------------------------------------------------------------------

# Host System: {{os}}

The host OS may be:

-   **Windows** → Use native PowerShell
-   **macOS** → Use native bash/zsh
-   **Linux** → Use native bash

Do NOT require PowerShell Core (`pwsh`) on macOS/Linux.

------------------------------------------------------------------------

# CRITICAL: What Runs Where

## Always use Docker for:

-   Python scripts (`.py`)
-   Node.js scripts (`.js`, `.ts`)
-   Git operations
-   Shell/bash scripts
-   Package managers (`pip`, `npm`, etc.)
-   Any long-running command
-   Any subprocess-spawning execution

## Host shell is ONLY acceptable for:

-   Creating directories
-   Copying files
-   Removing directories
-   Docker CLI commands
-   Environment inspection

If unsure → use Docker.

------------------------------------------------------------------------

# Sandbox Output Rule

All artifacts must end up in:

    {{paths.sandbox}}

------------------------------------------------------------------------

# OUTPUT METHOD: docker cp (Standard)

`docker cp` is the standard method for retrieving output from containers.
It is more reliable than volume mounts, which can fail with UNC paths,
network drives, permission issues, and path translation between host and
container.

The pattern: run a named container (without `--rm`), copy output to
sandbox, then remove the container.

------------------------------------------------------------------------

# WINDOWS (PowerShell)

## 1️⃣ Run container (named, no --rm)

``` powershell
docker run --name mux-task `
  -v "{{paths.skills}}:/workspace" `
  python-runtime-image `
  python /workspace/script.py
```

## 2️⃣ Copy output to sandbox

``` powershell
docker cp mux-task:/output/. "{{paths.sandbox}}"
```

## 3️⃣ Cleanup

``` powershell
docker rm mux-task
```

------------------------------------------------------------------------

# macOS / LINUX (Bash)

## 1️⃣ Run container (named, no --rm)

``` bash
docker run --name mux-task \
  -v "{{paths.skills}}:/workspace" \
  python-runtime-image \
  python /workspace/script.py
```

## 2️⃣ Copy output to sandbox

``` bash
docker cp mux-task:/output/. "{{paths.sandbox}}"
```

## 3️⃣ Cleanup

``` bash
docker rm mux-task
```

------------------------------------------------------------------------

# Why docker cp over volume mounts for output

Volume mounts (`-v host:container`) fail or behave unexpectedly with:

-   UNC paths (`\\server\share`)
-   Network drives
-   Non-local mounts
-   Windows path translation edge cases
-   Permission mismatches between host and container

`docker cp` avoids all of these by copying from the stopped container's
filesystem directly. Input mounts (read-only workspace/scripts) are still
fine as volume mounts since they use known local paths.

------------------------------------------------------------------------

# Discover Available Images (All OS)

``` bash
docker images --filter "reference=*" --format "{{.Repository}}:{{.Tag}} ({{.Size}})"
```

------------------------------------------------------------------------

# Script Output Rules

Inside container:

ALWAYS write to:

``` python
/output/filename.ext
```

NEVER write:

``` python
'{{paths.sandbox}}/file.ext'
'C:\\file.ext'
'Z:\\file.ext'
'\\\\server\\share\\file.ext'
```

Containers are Linux. Host paths do not exist inside them.

------------------------------------------------------------------------

# Example Python Script

``` python
from docx import Document

doc = Document()
doc.add_heading("Monthly Report", 0)
doc.add_paragraph("Generated automatically.")
doc.save("/output/report.docx")

print("Saved report.docx")
```

------------------------------------------------------------------------

# Install Missing Package Inline

Works on all OS:

``` bash
docker run --name mux-task \
  -v "{{paths.skills}}:/workspace" \
  python-runtime-image \
  bash -c "pip install some-package && python /workspace/script.py"
```

Then `docker cp` and `docker rm` as above.

(On Windows, use PowerShell multiline format.)

------------------------------------------------------------------------

# Multiple Output Files

`docker cp` with the `/output/.` source copies the entire directory
contents. No need to specify individual files:

``` bash
docker cp mux-task:/output/. "{{paths.sandbox}}"
```

This copies all files and subdirectories from `/output/` into the sandbox.

------------------------------------------------------------------------

# Rules

-   Python, Node, git, shell scripts MUST run in Docker
-   Use `docker cp` to retrieve output (not volume mounts for output)
-   Use named containers (no `--rm`) so `docker cp` can run after exit
-   Always `docker rm` after copying output
-   Always write to `/output/` inside container
-   Never use `--privileged`
-   Never mount entire drives
-   Do not use Docker for plain file writes
-   If script fails → fix and retry (do not rerun unchanged)
-   Input scripts/workspace can use volume mounts (`-v`) with known local paths

------------------------------------------------------------------------

# Platform Summary

  Component   Windows         macOS      Linux
  ----------- --------------- ---------- ----------
  Shell       PowerShell      bash/zsh   bash
  Copy out    `docker cp`     `docker cp`  `docker cp`
  Cleanup     `docker rm`     `docker rm`  `docker rm`
  Docker      Same            Same       Same