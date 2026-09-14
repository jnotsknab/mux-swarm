# Workflow Engine

This page describes static JSON command workflows. Use them for prescribed, repeatable execution; dynamic scripted workflows are described in the [command reference](cli.md).

## What a workflow is

A static command workflow is a sequence piped through the runtime, exactly as a human would type it, but reproducible and shareable. This format is a list of strings, not a dependency graph; it does not describe or limit the separate taskboard/team coordination capabilities.

A single workflow file can:

- Transition between agent mode, swarm mode, and parallel swarm mode
- Chain REPL operations with persistent state
- Orchestrate multi-step pipelines across different execution models

The runtime handles all state transitions, tool loading, and cleanup. When the workflow completes, control returns to the keyboard.

## File format

A workflow file is a JSON object with a `name` and a `steps` array. Each step is a string: either a slash command (mode switches, session control) or a goal/prompt for the currently active mode.

```json
{
  "name": "Research and Report",
  "steps": [
    "/agent",
    "Search for the latest developments in quantum computing and summarize your findings",
    "/qc",
    "/swarm",
    "Take the research from the previous agent session and produce a formatted report",
    "/qm",
    "/pswarm",
    "Cross-reference the report against three independent sources for accuracy",
    "/qm"
  ]
}
```

In the example above, `/agent`, `/swarm`, and `/pswarm` switch execution styles, the plain-text steps are executed as goals in whichever interface is active, and `/qc` / `/qm` close out each session before the next begins. This is not a capability progression: the main `/agent` interface can coordinate specialist work when enabled; the example deliberately selects serial and concurrent-batch coordinators for particular phases. A workflow fixes the outer sequence, whereas a swarm coordinator decides its specialist assignments adaptively.

## Running a workflow

From the CLI, pass the file with `--workflow` (or the `--wf` alias):

```bash
mux-swarm --workflow ./workflows/research-pipeline.json
```

Mid-session, use the `/workflow` command:

```
> /workflow ./workflows/research-pipeline.json
```

For these static JSON files, steps are supplied in order, top to bottom. The command sequence is repeatable; model responses and tool outcomes are not guaranteed deterministic. `/workflow` retains its existing menu/session handoff behavior.

---
[Back to docs index](README.md) | [Main README](../README.md)
