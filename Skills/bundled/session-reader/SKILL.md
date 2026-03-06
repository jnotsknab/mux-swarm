---
name: session-reader
description: Summarize Mux session folders into a clean markdown report. Use when you need fast context at task start, want to review tool calls, delegated agents, artifacts, or outcomes across one or more sessions.
---

## Use this skill
- Summarize one or more session directories into markdown in a single command.
- Review tool usage, delegations, artifacts, and outcomes before continuing work.
- Compare multiple sessions by passing all session paths in one call.

## Delegation
Delegate this task to **CodeAgent**. No memory retrieval is needed — session files are the source of truth. This should be fast and require no more than one agent.

## Session types
Sessions are saved by both swarm mode and chat (single agent) mode into the same directory. You can identify the mode by the number of agent files present:

- **Swarm session** — multiple `*_session.json` files, one per agent (e.g. `codeagent_session.json`, `memoryagent_session.json`, etc.)
- **Chat session** — a single `agent_session.json` file only

Both are valid context sources. A chat session with only one file is not incomplete — it reflects a single-agent interaction and should be treated as full context for whatever goal was pursued.

## Reasoning about recency
Session directories are named by timestamp (`yyyy-MM-dd_HH-mm-ss`). Use this to reason about how far back to scan — sort by name descending to find the most recent sessions, or filter by date prefix to find sessions from a specific day or time window.

## Script
The summarize script is located at:
`{{paths.skills}}/bundled/session-reader/scripts/summarize_session.py`

## Setup

```bash
uv venv {{paths.base}}/session-reader-venv
# No extra dependencies needed — script uses stdlib only
```

## Run

Identify session folders in `{{paths.sessions}}`, then pass them along with your allowed paths for artifact detection:

```bash
{{shell}} {{shell.flag}} "python {{paths.skills}}/bundled/session-reader/scripts/summarize_session.py {{paths.sessions}}/<ts1> {{paths.sessions}}/<ts2> --allowed-paths {{paths.sandbox}}"
```

Each session will be clearly delimited in the output with a header and footer so the agent can distinguish where one session ends and the next begins.

## Allowed Paths

The script detects artifacts by matching file paths against your configured allowed paths. Pass them via:

- `--allowed-paths /path/one /path/two` — CLI argument (recommended)
- `QWE_ALLOWED_PATHS` environment variable — colon-separated on Unix, semicolon-separated on Windows

If no paths are provided, artifact detection is disabled and a warning is printed.

## Example — last 3 sessions

```bash
{{shell}} {{shell.flag}} "python {{paths.skills}}/bundled/session-reader/scripts/summarize_session.py {{paths.sessions}}/2026-02-22_23-11-36 {{paths.sessions}}/2026-02-23_08-15-09 {{paths.sessions}}/2026-02-24_14-30-00 --allowed-paths {{paths.sandbox}}"
```

