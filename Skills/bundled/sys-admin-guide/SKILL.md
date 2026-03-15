---
name: sys-admin-guide
description: Use this skill when performing general system administration tasks across Windows, macOS, or Linux, such as configuring OS settings, managing packages/software, interacting with services, or network operations.
requires_bins: [uv]
---

# Cross-Platform System Administration Skill

Use this skill when performing system administration tasks on any operating system.

## Guidelines for System Administration

1. **OS Detection First**:
   - Always verify the operating system environment before executing commands (e.g., using `python -c "import platform; print(platform.system())"` or checking available CLI tools).
   - Tailor your commands strictly to the detected OS (Windows, Darwin for macOS, Linux) and the `{{os}}` injected tokens.

2. **Shell Execution & Abstraction**:
   - **Windows**: Use PowerShell (`Get-Process`, `Get-Service`, etc.). Use `-WhatIf` for dry runs where applicable.
   - **macOS / Linux**: Use standard POSIX commands (`ps`, `top`, `systemctl`, `launchctl`, `grep`). Ensure safe flags are used to avoid unintended consequences (e.g., `rm -i`).
   - Use the provided shell tool appropriate for the environment (e.g., typically injected as `{{shell}}`).

3. **Python as a Universal Bridge**:
   - Whenever possible, leverage Python (`PythonReplMCP`) with built-in modules (`os`, `sys`, `platform`, `shutil`, `socket`) or virtual environments to install cross-platform utilities (`psutil` via `uv pip install psutil`).
   - Python handles system queries and file movements cleanly, preventing many shell syntax mismatches.

4. **Package Management Ecosystems**:
   - **Windows**: Check for `winget` (preferred), `choco`, or `scoop`.
   - **macOS**: Use `brew` (Homebrew).
   - **Linux**: Use `apt` (Debian/Ubuntu), `dnf`/`yum` (RHEL/CentOS), or `pacman` (Arch).

5. **Network Operations**:
   - **Windows**: `Test-NetConnection`, `Get-NetIPConfiguration`.
   - **macOS / Linux**: `ping`, `nc`, `ifconfig`, `ip addr`, `lsof -i`, `netstat`.

6. **Safety & Verification**:
   - Do not execute destructive actions or kill critical processes without explicit confirmation.
   - Always query the state, perform the change, and re-query to validate the change was successful.
