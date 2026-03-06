---
name: git-guide
description: Guide for using git through the git-container Docker container. All git operations must use the git-container for isolation.
---

# Git Guide Skill

Use this skill when performing any git operations (clone, pull, push, commit, etc.).

## Policy: Use git-container for ALL Git Operations

**MANDATORY**: All git operations MUST be executed inside the `git-container` Docker container. Never run git directly on the host system.

## Container Details

| Property | Value |
|----------|-------|
| Containers | `appropriate git container, alpine, ubuntu, debain, etc` |
| Image | `ubuntu:22.04` |
| Git Version | 2.34.1 |
| Status | Running (detached) |

## Usage Examples

### Clone a Repository
```bash
docker exec -it git-container git clone <repo-url> <destination>
```

### Pull Latest Changes
```bash
docker exec -it git-container git -C /path/to/repo pull
```

### Push Changes
```bash
docker exec -it git-container git -C /path/to/repo push
```

### Interactive Shell (for complex operations)
```bash
docker exec -it git-container sh
```

### Check Git Status
```bash
docker exec -it git-container git -C /path/to/repo status
```

## NAS Storage - MANDATORY Destination

**ALL cloned repositories and git artifacts MUST be saved to the NAS SANDBOX:**

```
{{paths.sandbox}}
```

This is the designated storage location for all git operations. Always ensure:
1. Clones are created directly in or copied to the NAS sandbox
2. Working directories are set to paths within the NAS
3. Artifacts (branches, tags, exports) are stored on the NAS

## Workflow

1. Identify the target repository URL
2. Use `docker exec -it git-container git clone <url> <nas-path>`
3. Navigate to the cloned repository
4. Perform git operations as needed
5. Always save outputs to the NAS sandbox

## Common Commands

| Operation | Command |
|-----------|---------|
| Clone | `docker exec -it git-container git clone <url> /nas/path` |
| Pull | `docker exec -it git-container git -C /nas/repo pull` |
| Push | `docker exec -it git-container git -C /nas/repo push` |
| Status | `docker exec -it git-container git -C /nas/repo status` |
| Branch | `docker exec -it git-container git -C /nas/repo branch` |
| Log | `docker exec -it git-container git -C /nas/repo log` |
| Diff | `docker exec -it git-container git -C /nas/repo diff` |

## Container Maintenance

If the container stops, restart it:
```bash
docker start git-container
```

Verify container is running:
```bash
docker ps | findstr git-container
```
