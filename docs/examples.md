# Examples & Demos

Real-world examples of Mux-Swarm in action. Each section includes a video walkthrough, the exact prompt used, and guidance on when to reach for each mode.

---

## Parallel Swarm — AI Industry Intelligence Sweep

**Mode:** `/pswarm` or `--parallel`
**Best for:** Large goals that decompose into independent subtasks, research sweeps, batch analysis, documentation sprints, anything where subtasks don't depend on each other.

The orchestrator breaks the goal into eight concurrent research tracks, one per company, and dispatches them simultaneously across agents. Each agent works independently, producing its own report. Once all tracks complete, a final landscape summary is synthesized. What would take a sequential swarm eight rounds finishes in one.

**Prompt:**
```
Conduct a deep intelligence sweep across the AI industry. Research the latest developments from OpenAI, Anthropic, Google DeepMind, Meta AI, Mistral, Cohere, xAI, and Stability AI. For each company, cover recent product launches, technical breakthroughs, partnerships, hiring signals, and strategic direction. Produce individual company briefs and a final landscape summary identifying the three most significant shifts this quarter. Save everything to the sandbox.
```

https://github.com/user-attachments/assets/23077140-ea45-4741-8fe4-09712e0d40a0

---

## Multi-Agent Swarm — Coding & System Analysis

**Mode:** `/swarm` or default CLI goal execution
**Best for:** Multi-step objectives that require coordination between specialists, the orchestrator delegates to the right agent for each phase, compacts results, and drives toward completion.

Here the orchestrator routes the task through CodeAgent for script generation and testing, then hands off to the memory agent for validation and knowledge persistence. Each agent contributes its specialty, and the orchestrator manages the handoffs.

**Prompt:**
```
Write a Python monitoring script that checks disk usage, memory, CPU load, and network connectivity. It should log results to a JSON file, flag anything above 80% utilization, and generate a daily health summary in markdown. Save the script and a sample output to the sandbox.
```

https://github.com/user-attachments/assets/b892f5bd-390f-47ae-affb-8dc2db13ed67

---

## Single Agent — Direct, Iterative, Hands-On

**Mode:** `/agent`
**Best for:** Focused tasks where you want direct back-and-forth with one agent, debugging, exploration, iterative refinement, or when you want full control over the conversation. If you prefer a workflow similar to Claude Code or Cursor, this is your exec mode.

In this session, the agent scanned the sandbox, summarized every file it found, proposed a reorganization plan, then executed the reorganization after confirmation, creating directories, moving files, and verifying the new structure. One agent, multiple turns, no orchestrator overhead.

**Prompt:**
```
List everything in my sandbox, summarize what you find, and suggest how to reorganize it.
```

https://github.com/user-attachments/assets/acf7cdcd-5a3c-47eb-8adb-4520a640d16f

---

## CLI One-Liner — Fire and Forget

**Mode:** `mux-swarm --goal "<goal>"`
**Best for:** Scripting, automation, pipelines, or when you know exactly what you want and don't need an interactive session. Pair with `--agent <name>` to target a specific agent, or let the swarm handle it.

Pass a goal directly from the command line. The swarm picks it up, executes, and exits. No interactive prompt, no session to manage, just output.

```bash
mux-swarm --goal "Create a markdown cheat sheet for Docker covering containers, images, volumes, and networking. Save it to the sandbox."
```

https://github.com/user-attachments/assets/a8a6dc67-b49a-4230-bcb0-45d60267a01b

---

## Choosing the Right Mode

| Mode | Command | When to use it |
|------|---------|----------------|
| **Parallel Swarm** | `/pswarm` · `--parallel` | Independent subtasks that can run simultaneously — research, batch jobs, documentation |
| **Sequential Swarm** | `/swarm` · default | Multi-step goals requiring agent coordination and orchestrator-managed handoffs |
| **Single Agent** | `/agent` | Direct conversation with one agent — iterative work, debugging, exploration |
| **CLI Goal** | `--goal` | Automation, scripts, pipelines — fire and forget |
| **Continuous** | `--continuous` | Long-running autonomous loops — monitoring, recurring reports, scheduled work |
