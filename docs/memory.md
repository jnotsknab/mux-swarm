# Memory System - Layered Architecture & Deep Memory

Mux-Swarm treats memory as a set of specialized layers rather than one monolithic context
window. Compact, always-read markdown layers carry identity and durable facts; heavy stores
(knowledge graph, vector DB) hold dense knowledge reached on demand; the filesystem is ground
truth for artifacts. On top of this, v0.12.1 adds an optional **deep memory** subsystem: a
background reflection agent that distills what happens during sessions and injects the most
relevant reflections back into agent context automatically.

## Layered Memory Architecture

Instead of forcing every agent to carry large historical context, the runtime distributes
knowledge across specialized memory layers, with a dedicated Memory Agent (or the lead agent
itself) managing retrieval and persistence.

| Layer | Role | Where |
|-------|------|-------|
| **BRAIN.md** | Behavioral: agent identity, conventions, standing directives, learned anti-patterns and reflexes | `Context/BRAIN.md` |
| **MEMORY.md** | Factual: active projects, environment, constraints, operator profile | `Context/MEMORY.md` |
| **Knowledge graph** | Structured: entities, relationships, deterministic facts where relationships matter over similarity | Memory MCP server (`knowledgeGraphPath`) |
| **Vector DB (Chroma)** | Semantic: embedding search over prior knowledge without loading full histories | ChromaDB MCP server (`chromaDbPath`) |
| **Filesystem** | Artifacts: deliverables, intermediate outputs, analysis results | Sandbox / allowed paths |
| **In-context working memory** | Compressed sub-agent results reinjected into orchestrator context | Runtime (compaction) |

How the layers cooperate:

- **In-context working memory** - results from delegated agents are compressed and reinjected
  into orchestrator context, keeping token usage bounded during multi-step coordination.
- **Semantic memory (vector retrieval)** - a vector layer enables semantic search over prior
  knowledge, letting agents recall relevant context without loading full histories.
- **Structured knowledge memory (graph)** - a knowledge graph stores entities, relationships,
  and structured facts for deterministic queries where relationships matter more than
  embedding similarity.
- **Filesystem artifact layer** - agents exchange artifacts and intermediate outputs through
  files, turning the filesystem into a lightweight message bus that mitigates hallucination,
  reduces token burn, and prevents context drift.

### The index-card convention

The two markdown layers are PRIMARY: small, always read, injected into agent context at
session start. When content grows dense, the full detail goes into the knowledge graph or
ChromaDB and a one-line stub stays in BRAIN/MEMORY pointing at it (for example
`-> KG:<entity>` or `-> chroma:<collection>/<id>`). Following a stub is a direct lookup on
the named target, never a fresh semantic re-search.

To keep the markdown layers from growing without bound, `config.json` `contextLimits`
supports hard char caps per file (`brainMdCharLimit` / `memoryMdCharLimit`), a cap mode
(`off` | `warn` | `force`), and an opt-in background prune pulse (`prunePulseSeconds`) that
rewrites an over-cap file under the limit (backing it up first).

## Deep Memory (v0.12.1)

Deep memory adds a background **reflection agent** that watches session activity, distills it
into compact reflections, and injects the most relevant ones back into agent context. Working
agents never query a store themselves: the gatherer writes, the injector pushes.

Everything lives in a single auto-pruned store: `Context/reflections.json` (atomic
temp-then-rename writes, in-memory cache, dedup by content with importance upgrade on
duplicates, pruned to `maxReflections` newest).

### Two-pass gatherer

The gatherer is activity-gated: a cheap counter is touched per user turn and per tool result,
and a timer (`pollIntervalSeconds`) is the sole rate limiter. No activity means no LLM call.

1. **Pass 1 - fast distill.** A tool-less pass over the recent session tail produces
   immediate injectable reflections. It may emit a `DIG|<target>` signal when something
   deserves investigation.
2. **Pass 2 - read-only investigator.** Triggered by a DIG signal or a heuristic cue in the
   latest user message (a path, a file, an error, a "where is" question). It gets a strictly
   read-only tool surface (grep, read file, list dir, query store), bounded by
   `maxDigsPerTick`, `digMaxFilesScanned`, `digMaxMatches`, and `digMaxReadChars`. Findings
   become reflections tagged with `dig` provenance.

The gatherer never gets write tools. A background actor with write access is a memory
poisoning vector (OWASP ASI06), so the investigator surface is read-only by design.

### Two-tier injection

Reflections reach the model through two complementary tiers:

- **Ephemeral (mid-turn).** `MidTurnReflectionClient`, a delegating chat client wrapping the
  lead model client, inserts newly gathered reflections into the live message list during a
  turn. This gives within-turn reach across tool round-trips: a reflection gathered right
  after a tool result is visible on the very next model call. Ephemeral injections evaporate
  at turn end by design.
- **Durable (cross-turn).** At each turn boundary the orchestrator prepends not-yet-durable
  reflections into the message list before the run starts, so the session thread records them
  and replays them on every future turn. Once injected durably, a reflection is simply part
  of context.

Split dedup makes the tiers safe together: a per-turn ephemeral set and a session-lifetime
durable set mean a reflection can surface live mid-turn and then become durable next turn
with no duplication.

Selection is ranked by a hybrid of semantic similarity (Chroma-backed
`ReflectionSemanticIndex`) and lexical match, weighted by recency and importance, with
`relevanceFloor` as the cutoff and `injectTokenBudget` as a hard cap on the total injected
block (not per reflection).

### Toggles

- `/memory` - status plus subcommands: `/memory deep`, `/memory standard`, `/memory show`,
  `/memory set <key> <value>`.
- `/deep` - shortcut to enable deep mode (`/deep off` to disable).

Both persist the change to `swarm.json`, so a resumed session keeps the mode.

### reflectionAgent configuration (`swarm.json`)

```json
"reflectionAgent": {
  "mode": "deep",
  "model": "your-light-model",
  "injectTokenBudget": 1500,
  "pollIntervalSeconds": 90,
  "relevanceFloor": 0.35,
  "scope": "lead"
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `mode` | `standard` | `standard` (deep memory off, subsystem inert) or `deep` (gatherer + injector active). |
| `model` | (fallback) | Model for the gatherer's distillation call; falls back to the orchestrator/compaction model. A cheap model is recommended. |
| `modelOpts` | `null` | Optional model tuning for the gatherer call (same shape as agent `modelOpts`). |
| `injectTokenBudget` | `1500` | Hard approximate-token cap on the TOTAL injected reflection block; truncated, never overflowed. |
| `pollIntervalSeconds` | `90` | Gatherer wake cadence. On each wake, no activity since the last reflection means no LLM call. |
| `relevanceFloor` | `0.35` | Minimum relevance score (0..1) for a reflection to be injected (anti-noise). |
| `scope` | `lead` | `lead` (lead + orchestrator only) or `all` (sub-agents too). |
| `maxReflections` | `30000` | Store prune ceiling; oldest reflections beyond this are dropped on append. |
| `injectQueryTimeoutMs` | `4000` | Timeout for the injector's semantic Chroma query; on timeout it falls back to lexical ranking. |
| `historyWindow` | `30` | How many recent conversation messages the Pass 1 distill observes. |
| `maxDigsPerTick` | `2` | Max Pass 2 investigations chased per gatherer tick. |
| `digMaxFilesScanned` | `4000` | Pass 2 dig: max files the read-only grep scans. |
| `digMaxMatches` | `40` | Pass 2 dig: max grep matches returned. |
| `digMaxReadChars` | `8000` | Pass 2 dig: max chars returned from a single file read. |

A top-level `memoryMode` key (`"standard"` | `"deep"`) is a convenience alias that overrides
`reflectionAgent.mode` at load time.

### How deep memory fits the layers

The reflection store is a fifth, automatic layer that sits between working context and the
heavy stores. The markdown layers stay curated and human-legible; the graph and vector DB
stay deliberate and stub-addressed; reflections capture the in-between - the operational
lessons, discovered paths, and session facts that would otherwise be lost at compaction - and
recycle them into future turns without anyone having to write them down.

---
[Back to docs index](README.md) | [Main README](../README.md)
