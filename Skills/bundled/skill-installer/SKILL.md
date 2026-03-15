---
name: skill-installer
description: Install bundled skills into local skills directory from curated lists or GitHub repos. Use when a user asks to list installable skills, install a curated skill, or install a skill from another repo (including private repos).
metadata:
  short-description: Install skills from openai/skills, VoltAgent/awesome-openclaw-skills, or other repos
---

# Skill Installer

Helps install skills. By default these are from:
- https://github.com/openai/skills/tree/main/skills/.curated (primary curated list)
- https://github.com/VoltAgent/awesome-openclaw-skills (additional skill collection)

Users can also provide other GitHub repo locations. Experimental skills from openai/skills live in https://github.com/openai/skills/tree/main/skills/.experimental and can be installed the same way.

## Allowed Skill Sources

The following GitHub repos are valid sources for installing skills:
1. **openai/skills** - Primary skill repository (curated and experimental)
2. **VoltAgent/awesome-openclaw-skills** - Additional OpenClaw skills collection
3. **Other repos** - Users can specify any public or private GitHub repo

## Communication

When listing skills, output approximately as follows, depending on the context of the user's request:
"""
Skills from {repo}:
1. skill-1
2. skill-2 (already installed)
3. ...
Which ones would you like installed?
"""

After installing a skill, tell the user: "The skill has been installed to the local skills directory."

## Local Skills Directory

Skills must be installed to:
```
{{paths.skills}}/bundled/<skill-name>\
```

This is the bundled skills directory read by the agent system. Each skill lives in its own subdirectory containing a `SKILL.md` file.

**CRITICAL: Mux-Swarm Python Execution Standards**
When installing or modifying any skill that uses Python, you MUST ensure that the `requires_bins` array in the YAML frontmatter includes `uv` alongside `python`. The Mux-Swarm Python REPL relies heavily on `uv` for dependency management and virtual environment creation.
Example: `requires_bins: [uv]`

The openai/skills repo is already cloned to the NAS sandbox at:
```
{{paths.sandbox}}/openai-skills
```

Use this as the source when installing from openai/skills — no re-clone needed.

## Installation Methods

### Option 1: Copy from NAS (openai/skills — preferred)

Since openai/skills is already cloned to the NAS, copy directly:

```powershell
# Curated skills source
{{paths.sandbox}}/openai-skills\skills\.curated\<skill-name>

# Copy to local bundled skills
Copy-Item -Recurse "{{paths.sandbox}}/openai-skills\skills\.curated\<skill-name>" "{{paths.skills}}/bundled/<skill-name>"
```

### Option 2: Clone via git-container (other repos or fresh clone)

Git operations should run using the host system shell appropriate for {{os}}.

- Windows → PowerShell with `git`
- macOS → bash/zsh with `git`
- Linux → bash with `git`


```powershell
# Clone to NAS first
git clone https://github.com/VoltAgent/awesome-openclaw-skills.git /nas/awesome-openclaw-skills

# Then copy specific skill to local bundled directory via Filesystem MCP
```

### Option 3: Sparse checkout (single skill from large repo)

```powershell
bash -c "
  git clone --depth 1 --filter=blob:none --sparse https://github.com/VoltAgent/awesome-openclaw-skills.git /nas/temp-skill &&
  cd /nas/temp-skill &&
  git sparse-checkout set skills/<skill-name>
"
# Then copy to {{paths.skills}}/bundled/<skill-name>
```

## Behavior and Options

- Defaults to copying from NAS clone for openai/skills (already available)
- Falls back to git sparse checkout if source not available locally
- Aborts if destination skill directory already exists (unless overwrite is requested)
- Multiple skills can be installed in one run
- Options: `--ref <ref>` (default `main`), `--overwrite`

## Notes

- Curated listing: `https://api.github.com/repos/openai/skills/contents/skills/.curated`
- VoltAgent listing: `https://api.github.com/repos/VoltAgent/awesome-openclaw-skills/contents/skills`
- Private repos: use existing git credentials or `GITHUB_TOKEN`/`GH_TOKEN` env var
- Git fallback tries HTTPS first, then SSH
- Already installed skills are those present in `{{paths.skills}}\bundled\`
