---
name: skill-creator
description: Guide for creating, updating, and packaging skills. Use when a user wants to create a new skill, improve an existing skill, or package a skill for distribution. Triggers on requests like "create a skill", "make a new skill for X", "update this skill", or "package this skill".
---

# Skill Creator

Skills are modular, self-contained packages that give agents specialized knowledge, workflows, and reusable resources. Think of them as onboarding guides for specific domains — they transform a general-purpose agent into a specialist without bloating every agent's system prompt.

## About Skills

### What Skills Provide

1. **Specialized workflows** — Multi-step procedures for specific domains
2. **Tool integrations** — Instructions for working with specific file formats or APIs
3. **Domain expertise** — Environment-specific knowledge, schemas, business logic
4. **Bundled resources** — Scripts, references, and assets for complex or repetitive tasks

### Anatomy of a Skill

```
skill-name/
├── SKILL.md (required)
│   ├── YAML frontmatter — name + description only
│   └── Markdown body — instructions loaded after skill triggers
└── Bundled Resources (optional)
    ├── scripts/      — Executable code (Python/Bash/etc.)
    ├── references/   — Documentation loaded into context as needed
    └── assets/       — Files used in output (templates, fonts, boilerplate)
```

**Frontmatter fields** — only `name` and `description` are valid. Do not add any other fields.

```yaml
---
name: my-skill
description: What the skill does and when to use it. Include specific triggers.
---
```

### Core Design Principles

**Concise is key.** The context window is shared. Only add context the agent doesn't already have. Challenge every section: does this justify its token cost?

**Match freedom to fragility.** High freedom (prose instructions) for flexible tasks. Low freedom (specific scripts) for fragile, error-prone operations that must be consistent.

**Progressive disclosure.** Three loading levels:
1. Metadata (name + description) — always in context, ~100 words
2. SKILL.md body — loaded when skill triggers, keep under 500 lines
3. Bundled resources — loaded only as needed by the agent

**No auxiliary files.** Do not create README.md, CHANGELOG.md, INSTALLATION_GUIDE.md, or similar. The skill contains only what an agent needs to do the job.

---

## Skill Creation Process

### Step 1: Understand the Skill

Clarify concrete examples of how the skill will be used before writing anything:
- What does the user want the skill to handle?
- What would a user say to trigger this skill?
- What output or artifact should the skill produce?

Avoid asking more than 2 questions at once. Iterate until the use case is clear.

### Step 2: Plan Reusable Contents

For each example use case, identify what would be rewritten repeatedly:
- **Scripts** — same code written each time → move to `scripts/`
- **References** — same documentation looked up each time → move to `references/`
- **Assets** — same templates or boilerplate copied each time → move to `assets/`

### Step 3: Initialize the Skill

Run `init_skill.py` to scaffold the directory:

```bash
python scripts/init_skill.py <skill-name> --path <output-directory>
```

This creates the skill directory with a SKILL.md template and example resource directories. Customize or delete the generated examples as needed.

### Step 4: Build Resources First

Start with `scripts/`, `references/`, and `assets/` before writing SKILL.md. If scripts are included, **test them by actually running them** — do not ship untested scripts. A representative sample is sufficient if there are many similar scripts.

Consult these references when needed:
- **Multi-step processes**: See `references/workflows.md`
- **Output format guidance**: See `references/output-patterns.md`

### Step 5: Write SKILL.md

**Frontmatter description** is the primary trigger mechanism. Write it to be both descriptive and specific about when to use the skill. The body is only loaded after triggering — do not put "when to use" guidance in the body.

**Body guidelines:**
- Use imperative/infinitive form
- Prefer examples over explanations
- Keep under 500 lines — move detail to `references/` files
- Reference bundled files explicitly so agents know they exist

### Step 6: Validate and Package

```bash
# Validate only
python scripts/quick_validate.py <path/to/skill>

# Package (validates first, then creates .skill file)
python scripts/package_skill.py <path/to/skill>

# Optional: specify output directory
python scripts/package_skill.py <path/to/skill> ./dist
```

A `.skill` file is a zip archive named after the skill (e.g. `my-skill.skill`). Fix any validation errors before packaging.

### Step 7: Iterate

After real usage, improve based on what the agent struggled with. Update SKILL.md or bundled resources and re-package.

---

## Reference Patterns

See `references/workflows.md` for sequential and conditional workflow patterns.
See `references/output-patterns.md` for template and example output patterns.

---

## Agent Notes

This swarm uses a dynamic agent roster that changes as agents are added or removed from `swarm.json`. Do not hardcode agent names in skills. Skills should describe *what needs to happen* — the orchestrator routes to the appropriate agent based on the current roster. Write skills in terms of capabilities (web research, code generation, file writing, memory storage) rather than specific agent names.
