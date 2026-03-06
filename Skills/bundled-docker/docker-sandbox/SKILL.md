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

Docker cannot reliably mount:

-   UNC paths
-   Network drives
-   Non-local mounts
-   External volumes

So we use a **local-then-copy pattern**.

------------------------------------------------------------------------

# LOCAL-THEN-COPY (OS Native)

------------------------------------------------------------------------

# WINDOWS (PowerShell)

## 1️⃣ Create local temp output

``` powershell
$LocalOutput = Join-Path $env:TEMP "container-output"
New-Item -ItemType Directory -Force -Path $LocalOutput | Out-Null
```

------------------------------------------------------------------------

## 2️⃣ Run container

``` powershell
docker run --rm `
  -v "${LocalOutput}:/output" `
  -v "{{paths.skills}}:/workspace" `
  python-runtime-image `
  python /workspace/script.py
```

------------------------------------------------------------------------

## 3️⃣ Copy to sandbox

``` powershell
Copy-Item -Path (Join-Path $LocalOutput "*") `
          -Destination "{{paths.sandbox}}" `
          -Recurse -Force
```

------------------------------------------------------------------------

## 4️⃣ Cleanup

``` powershell
Remove-Item -Recurse -Force $LocalOutput
```

------------------------------------------------------------------------

# macOS / LINUX (Bash)

## 1️⃣ Create local temp output

``` bash
LocalOutput="$(mktemp -d)/container-output"
mkdir -p "$LocalOutput"
```

------------------------------------------------------------------------

## 2️⃣ Run container

``` bash
docker run --rm \
  -v "$LocalOutput:/output" \
  -v "{{paths.skills}}:/workspace" \
  python-runtime-image \
  python /workspace/script.py
```

------------------------------------------------------------------------

## 3️⃣ Copy to sandbox

``` bash
cp -R "$LocalOutput"/. "{{paths.sandbox}}"
```

------------------------------------------------------------------------

## 4️⃣ Cleanup

``` bash
rm -rf "$LocalOutput"
```

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
docker run --rm \
  -v "$LocalOutput:/output" \
  -v "{{paths.skills}}:/workspace" \
  python-runtime-image \
  bash -c "pip install some-package && python /workspace/script.py"
```

(On Windows, use PowerShell multiline format.)

------------------------------------------------------------------------

# Rules

-   Python, Node, git, shell scripts MUST run in Docker
-   Never mount UNC/network paths directly into Docker
-   Always use local-then-copy for sandbox output
-   Always use `--rm`
-   Always write to `/output/` inside container
-   Always clean up temp directory
-   Never use `--privileged`
-   Never mount entire drives
-   Do not use Docker for plain file writes
-   If script fails → fix and retry (do not rerun unchanged)

------------------------------------------------------------------------

# Platform Summary

  Component   Windows         macOS      Linux
  ----------- --------------- ---------- ----------
  Shell       PowerShell      bash/zsh   bash
  Temp Dir    `$env:TEMP`     `mktemp`   `mktemp`
  Copy        `Copy-Item`     `cp -R`    `cp -R`
  Cleanup     `Remove-Item`   `rm -rf`   `rm -rf`
  Docker      Same            Same       Same
