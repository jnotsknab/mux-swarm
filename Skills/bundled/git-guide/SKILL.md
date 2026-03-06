---
name: git-guide
description: Guide for using git directly on the host system via the OS shell tool. Use when performing any git operations — clone, pull, push, commit, branch, etc.
requires_bins: [git]
---

# Git Guide Skill ({{os}})

Use this skill when performing any git operations on the host system.

## Prerequisites

Git must be installed and available on PATH. Verify:
```bash
git --version
```

## Shell Tool

All git commands run via the shell tool appropriate for {{os}}. Discover the available shell tool via your tool list — on Windows this is `Windows_Shell`, on Linux/Mac use the bash MCP or equivalent.

## Common Operations

### Clone a Repository
```bash
git clone <repo-url> <destination>
```

### Check Status
```bash
git -C /path/to/repo status
```

### Pull Latest Changes
```bash
git -C /path/to/repo pull
```

### Stage and Commit
```bash
git -C /path/to/repo add .
git -C /path/to/repo commit -m "commit message"
```

### Push Changes
```bash
git -C /path/to/repo push
```

### Branch Operations
```bash
git -C /path/to/repo branch           # list branches
git -C /path/to/repo checkout -b name # create and switch
git -C /path/to/repo checkout name    # switch branch
```

### Log and Diff
```bash
git -C /path/to/repo log --oneline -10
git -C /path/to/repo diff
```

## Storage

All cloned repositories and git artifacts should be saved to the sandbox:

```
{{paths.sandbox}}
```

Always clone directly into or copy outputs to the sandbox path.

## Common Commands Reference

| Operation | Command |
|-----------|---------|
| Clone | `git clone <url> {{paths.sandbox}}/repo-name` |
| Pull | `git -C <repo-path> pull` |
| Push | `git -C <repo-path> push` |
| Status | `git -C <repo-path> status` |
| Branch | `git -C <repo-path> branch` |
| Log | `git -C <repo-path> log --oneline` |
| Diff | `git -C <repo-path> diff` |

## Error Handling

- `Permission denied` → check sandbox path is in allowed paths config
- `Repository not found` → verify URL and credentials
- `Authentication failed` → ensure SSH key or token is configured on the host
- If a command times out → break into smaller operations or check network connectivity
