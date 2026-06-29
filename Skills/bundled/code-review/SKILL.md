---
name: code-review
description: Structured diff and PR code review with a prioritized checklist. Use when reviewing staged changes, pull requests, or any code diff before merge.
requires_bins: [git]
---

## When to use

- Before merging a PR or feature branch
- Reviewing staged changes before committing
- Auditing a specific file or range of commits
- Any time you need a structured, consistent review output

## Step 1 — Capture the diff

```bash
# Unstaged working-tree changes
git diff

# Staged (about to be committed)
git diff --staged

# Branch vs main
git diff main...HEAD

# Specific file
git diff HEAD -- path/to/file.py

# Between two commits
git diff abc123 def456

# Stat summary first (scope the blast radius)
git diff --stat main...HEAD
```

## Step 2 — Understand context

```bash
# Recent commit messages on this branch
git log --oneline main..HEAD

# Who last touched a file
git log --follow -p -- path/to/file.py

# Full file for context around a hunk
git show HEAD:path/to/file.py
```

## Step 3 — Review checklist

Work through each category. Flag findings as **P0/P1/P2** (below).

### Correctness
- [ ] Logic matches the stated intent (read the PR description / commit message)
- [ ] Edge cases handled: empty input, zero, null/None, boundary values
- [ ] Off-by-one errors in loops/slices
- [ ] Concurrency: shared state mutated without locks; race conditions
- [ ] Error paths tested, not just happy path
- [ ] Return values checked where they carry meaning

### Security
- [ ] **Injection**: SQL, shell, HTML/JS — user input never concatenated raw into queries/commands
- [ ] **Auth/authz**: new endpoints/functions have appropriate permission checks
- [ ] **Secrets**: no API keys, passwords, tokens committed; no new hardcoded credentials
- [ ] **Deserialization**: untrusted input not passed to `pickle`, `yaml.load`, `eval`, etc.
- [ ] **Path traversal**: file paths from user input sanitized/resolved before use
- [ ] **Dependency**: new packages pinned to a specific version; not yanked/known-vulnerable

### Performance
- [ ] No N+1 query patterns introduced (DB calls inside loops)
- [ ] Expensive operations (network, disk, regex compile) outside hot paths / cached
- [ ] Memory: large collections not held longer than needed
- [ ] Indexes exist for new query filters

### Tests
- [ ] New behavior has tests; changed behavior has updated tests
- [ ] Tests assert outcomes, not implementation details
- [ ] No tests deleted without explanation
- [ ] Test coverage of error/edge paths, not only happy path

### Style & readability
- [ ] Names are clear and consistent with the codebase conventions
- [ ] Functions/methods stay focused (single responsibility)
- [ ] No dead code (commented-out blocks, unused imports/variables)
- [ ] Complex logic has a short explanatory comment where intent isn't obvious

### Breaking changes & API compat
- [ ] Public API signatures unchanged (or versioned/deprecated correctly)
- [ ] Config/schema changes are backward-compatible or migration provided
- [ ] Serialized formats (JSON, protobuf, DB schema) not silently changed
- [ ] Callers/consumers of changed interfaces updated

### Error handling
- [ ] Exceptions caught at the right level; not swallowed silently
- [ ] Error messages are actionable (include relevant context)
- [ ] Resources (files, connections, locks) released in all paths (`finally`/`with`/`defer`)

## Step 4 — Output format

Produce a prioritized findings list. **Read-only: propose changes, do not edit files directly.**

```
## Review: <branch or description>

### Summary
<2-3 sentence overview of what the diff does and overall risk level>

### Findings

**P0 — Must fix before merge**
- [security-scan.py:42] SQL query built with f-string from user input → SQL injection.
  Suggest: use parameterized query `cursor.execute(sql, (user_val,))`.

**P1 — Should fix**
- [api/orders.py:118] New `/admin/orders` endpoint missing auth check.
- [utils/parser.py:67] `yaml.load(data)` → use `yaml.safe_load(data)`.

**P2 — Nice to have / nit**
- [models/user.py:34] Variable `tmp` is unclear; rename to `normalized_email`.
- No test for the empty-input case in `parse_csv`.

### Verdict
[ ] Approve  [ ] Request changes  [ ] Needs discussion
```

## Priority definitions

| Level | Meaning |
|---|---|
| **P0** | Blocks merge: security vulnerability, data loss, crash in prod |
| **P1** | Should fix: correctness bug, missing auth, missing tests for new behavior |
| **P2** | Improve before or after merge: style, naming, coverage gaps |

## Useful one-liners

```bash
# Files changed (overview before reading hunks)
git diff --name-only main...HEAD

# Lines added/removed per file
git diff --numstat main...HEAD

# Search diff for a pattern (e.g. secret-like strings)
git diff main...HEAD | grep -i "password\|secret\|token\|api_key"

# Check for new TODO/FIXME introduced
git diff main...HEAD | grep "^+" | grep -i "todo\|fixme\|hack\|xxx"
```
