---
name: new-agent
description: Use this skill when adding a new agent to the swarm configuration. Covers swarm.json syntax, security rules, prompt file creation, and canDelegate guidance.
---

# New Agent Skill

Use this skill when the user wants to add a new specialist agent to the swarm. Follow every rule here before writing any config or prompt file.

## swarm.json Agent Entry Structure

Each agent entry in the `agents` array follows this structure:

```json
{
  "name": "AgentName",
  "description": "One sentence describing what this agent handles.",
  "promptPath": "agent_name.md",
  "model": "provider/model-id",
  "mcpServers": [
    "ExistingServerName"
  ],
  "canDelegate": false,
  "toolPatterns": [
    "SpecificToolName"
  ]
}
```

## Field Reference

**name** — PascalCase or short readable name. Must be unique across all agents.

**description** — Single sentence. Used by the Orchestrator to route tasks. Be specific — vague descriptions cause misrouting.

**promptPath** — Filename only, snake_case (e.g. `data_agent.md`). Do not include a full path — the backend resolves this relative to `{{paths.prompts}}`. When creating the prompt file, save it to that directory.

**model** — The model string for this agent. Uses your configured OpenAI-compatible endpoint's model ID format. Check your provider's documentation for available model IDs (e.g. `claude-sonnet-4-6`, `gpt-4o`, `grok-4`). If unsure of the exact model ID string, use web search to find it from the provider's official docs before adding it to the config.

**mcpServers** — Array of MCP server names this agent has access to. See security rules below.

**canDelegate** — Boolean. Whether this agent can sub-delegate to other specialists. See delegation guidance below.

**toolPatterns** — Array of specific tool names from any MCP server to additionally expose to this agent. MCP server agnostic — used for surfacing individual tools (e.g. `{{os}}_Shell`) that aren't tied to a specific server listing. See security rules below.

---

## Security Rules — CRITICAL

These rules exist to prevent the swarm from escalating its own permissions or re-enabling disabled servers.

**Rule 1: mcpServers may only reference servers already present in at least one existing agent.**

Before adding any server name to a new agent's `mcpServers`, verify it appears in the `mcpServers` list of an existing enabled agent. If it does not appear anywhere in the current config, it cannot be added.

```
✅ "Filesystem" — already used by WebAgent and CodeAgent
✅ "Memory" — already used by MemoryAgent
❌ "NewExternalAPI" — not present in any existing agent, cannot be added
```

**Rule 2: toolPatterns may only reference tools from already-enabled MCP servers.**

A tool pattern must belong to a server that is already enabled and referenced by an existing agent. You cannot use toolPatterns to surface tools from a disabled or unconfigured server.

```
✅ "{{os}}_Shell" — belongs to an already-enabled server used by CodeAgent
❌ "SomeDisabledServerTool" — server not enabled, cannot be referenced
```

**Rule 3: Never add, enable, or reference MCP servers not already in the config.**

The swarm config is the source of truth for what is permitted. A new agent cannot expand the set of available servers — it can only distribute access to servers that already exist.

**Rule 4: When in doubt, use fewer servers.**

Give a new agent only the servers it genuinely needs. Principle of least privilege — a research agent does not need `Memory` by default just because MemoryAgent has it.

---

## canDelegate Guidance

Set `canDelegate: true` only when the agent will genuinely need to hand off subtasks to specialists mid-task.

| Scenario | canDelegate |
|----------|-------------|
| Agent is a pure leaf node (storage, single-domain action) | false |
| Agent handles multi-step workflows that may require specialist input | true |
| Agent is domain-specific but self-contained | false |
| Agent produces outputs another specialist should process | true |

When `canDelegate: true`, the agent's system prompt must include a Sub-Agent Delegation section. See prompt guidelines below.

---

## System Prompt Guidelines

Create a markdown file at the path specified in `promptPath`. Save it to `{{paths.prompts}}`. Follow this structure:

```markdown
You are [AgentName] — [one sentence role description].

## Your Tools

List the tools available from the assigned MCP servers. Be specific — name each tool.

## How to Complete Tasks

Numbered steps describing the agent's general workflow.

## Sub-Agent Delegation (only if canDelegate: true)

You have access to a `delegate_to_agent` tool. Use it when a sub-task falls 
outside your specialization.

- Delegate to [AgentName] when [specific condition]
- Complete your own work first — only delegate what you cannot handle yourself
- Never delegate back to the Orchestrator
- Do not delegate tasks you are capable of handling yourself

## Rules

Bullet list of agent-specific constraints and quality standards.
```

---

## Step-by-Step: Adding a New Agent

1. **Identify the role** — what does this agent do that no existing agent handles?
2. **Check server eligibility** — list only MCP servers already present in the config
3. **Determine canDelegate** — use the table above
4. **Write the swarm.json entry** — follow the field reference exactly
5. **Create the prompt file** — save to `{{paths.prompts}}/agent_name.md`, follow the prompt structure above
6. **If canDelegate: true** — add the Sub-Agent Delegation section to the prompt and name specific agents it should delegate to with clear conditions
7. **Update swarm.json** — add the entry to the `agents` array

---

## Example: Adding a New DataAgent

Given existing servers: `Filesystem`, `BraveSearchMCP`, `Fetch`, `Memory`, `ChromaDB`, `MinecraftMCP`

```json
{
  "name": "DataAgent",
  "description": "Handles CSV parsing, data transformation, and structured analysis tasks.",
  "promptPath": "data_agent.md",
  "model": "minimax/minimax-m2.5",
  "mcpServers": [
    "Filesystem"
  ],
  "canDelegate": true,
  "toolPatterns": []
}
```

This is valid because `Filesystem` is already used by existing agents. `canDelegate: true` because it may need to delegate storage to MemoryAgent or research to WebAgent.

---

## Common Mistakes

- Adding an MCP server that only exists in a disabled config block — not permitted
- Setting `canDelegate: true` but forgetting to add the delegation section to the prompt
- Writing a vague description that overlaps with an existing agent — causes misrouting
- Giving an agent access to all servers by default — use least privilege
- Using a `promptPath` filename that doesn't match the actual file created
- Including a full path in `promptPath` — use filename only, the backend resolves to `{{paths.prompts}}` automatically
