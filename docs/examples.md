# Examples & Demos

Real-world examples of Mux-Swarm in action. Each section includes a video walkthrough and the exact command or prompt used.

---

## Parallel Swarm — AI Industry Intelligence Sweep

Eight concurrent agent tasks researching OpenAI, Anthropic, Google DeepMind, Meta AI, Mistral, Cohere, xAI, and Stability AI — producing individual company briefs and a landscape summary.

**Prompt:**
```
Conduct a deep intelligence sweep across the AI industry. Research the latest developments from OpenAI, Anthropic, Google DeepMind, Meta AI, Mistral, Cohere, xAI, and Stability AI. For each company, cover recent product launches, technical breakthroughs, partnerships, hiring signals, and strategic direction. Produce individual company briefs and a final landscape summary identifying the three most significant shifts this quarter. Save everything to the sandbox.
```


---

## Multi-Agent Swarm — Research & Report

The full swarm tackling a multi-step research objective with delegation across specialized agents.

**Prompt:**
```
Research the current state of WebAssembly adoption in server-side applications. Cover the major runtimes, language support, performance benchmarks, and production use cases. Produce a comprehensive report and save it to the sandbox.
```



---

## Single Agent — Quick Task

A single agent handling a focused, self-contained task.

**Prompt:**
```
List everything in my sandbox, summarize what you find, and suggest how to reorganize it.
```



---

## CLI One-Liner — Goal-Driven Execution

Running a goal directly from the command line with no interactive session.

```bash
mux-swarm --goal "Create a markdown cheat sheet for Docker covering containers, images, volumes, and networking. Save it to the sandbox."
```



---

## Continuous Mode — Autonomous Loop

A long-running autonomous loop that monitors and reports on a topic over time.

```bash
mux-swarm --continuous --goal "Monitor AI industry news and maintain a rolling weekly summary in the sandbox" --goal-id news-loop --min-delay 43200
```


