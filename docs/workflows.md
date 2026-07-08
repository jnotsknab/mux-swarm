# Workflow Engine

Define deterministic, replayable execution pipelines as JSON files and run them from the CLI or mid-session.

## What a workflow is

A workflow is a sequence of commands piped through the runtime, exactly as a human would type them, but reproducible and shareable. There is no DAG engine, no state machine, and no YAML DSL: a workflow is a list of strings, and the runtime does the rest.

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

In the example above, `/agent`, `/swarm`, and `/pswarm` switch execution modes, the plain-text steps are executed as goals in whichever mode is active, and `/qc` / `/qm` close out each session before the next mode begins.

## Running a workflow

From the CLI, pass the file with `--workflow` (or the `--wf` alias):

```bash
mux-swarm --workflow ./workflows/research-pipeline.json
```

Mid-session, use the `/workflow` command:

```
> /workflow ./workflows/research-pipeline.json
```

Both paths execute the same engine: steps run in order, top to bottom, and the pipeline is fully deterministic and replayable.

---
[Back to docs index](README.md) | [Main README](../README.md)
