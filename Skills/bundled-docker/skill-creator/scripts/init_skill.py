#!/usr/bin/env python3
"""
Skill Initializer - Creates a new skill directory from template.

Usage:
    init_skill.py <skill-name> --path <output-directory> [--resources scripts,references,assets] [--examples]

Examples:
    init_skill.py session-reader --path skills/bundled
    init_skill.py crypto-analysis --path skills/bundled --resources scripts,references
    init_skill.py report-builder --path skills/bundled --resources scripts --examples
"""

import argparse
import re
import sys
from pathlib import Path


MAX_SKILL_NAME_LENGTH = 64
ALLOWED_RESOURCES = {"scripts", "references", "assets"}

SKILL_TEMPLATE = """\
---
name: {skill_name}
description: [TODO: What the skill does and when to use it. Include specific trigger phrases, file types, or task patterns that should activate this skill. This is the primary triggering mechanism — be specific.]
---

# {skill_title}

## Overview

[TODO: 1-2 sentences explaining what this skill enables.]

## When to Use

[TODO: Delete this section if already covered in the description. Only include here if the triggering logic is complex enough to need elaboration.]

## Workflow

[TODO: Step-by-step instructions. Use imperative form. Reference bundled resources explicitly so agents know they exist.]

### Step 1: [Name]
[Description]

### Step 2: [Name]
[Description]

## Example

**Request**: "[Example trigger]"

**Steps**:
1. [Action and which capability handles it]
2. [Action]
3. Return [result]
"""

EXAMPLE_SCRIPT = """\
#!/usr/bin/env python3
\"\"\"
Example helper script for {skill_name}.
Replace with actual implementation or delete if not needed.
\"\"\"


def main():
    print("Running {skill_name} script...")
    # TODO: Implement script logic here


if __name__ == "__main__":
    main()
"""

EXAMPLE_REFERENCE = """\
# Reference: {skill_title}

[TODO: Replace with actual reference content or delete this file.]

Use references/ for documentation that agents should read while working —
API docs, schemas, detailed workflow guides, company policies, etc.

Keep SKILL.md lean by moving detailed reference material here.
Load this file only when the specific reference is needed.
"""

EXAMPLE_ASSET = """\
# Assets Placeholder

[TODO: Replace with actual asset files or delete this directory.]

Use assets/ for files used in output — templates, boilerplate code,
fonts, images, document templates — not loaded into context directly.
"""


def normalize_skill_name(name: str) -> str:
    normalized = name.strip().lower()
    normalized = re.sub(r"[^a-z0-9]+", "-", normalized)
    normalized = normalized.strip("-")
    normalized = re.sub(r"-+", "-", normalized)
    return normalized


def title_case(skill_name: str) -> str:
    return " ".join(word.capitalize() for word in skill_name.split("-"))


def parse_resources(raw: str) -> list[str]:
    if not raw:
        return []
    items = [r.strip() for r in raw.split(",") if r.strip()]
    invalid = sorted(set(items) - ALLOWED_RESOURCES)
    if invalid:
        print(f"[ERROR] Unknown resource type(s): {', '.join(invalid)}")
        print(f"        Allowed: {', '.join(sorted(ALLOWED_RESOURCES))}")
        sys.exit(1)
    seen, deduped = set(), []
    for item in items:
        if item not in seen:
            deduped.append(item)
            seen.add(item)
    return deduped


def init_skill(skill_name: str, path: str, resources: list[str], examples: bool) -> Path | None:
    skill_dir = Path(path).resolve() / skill_name

    if skill_dir.exists():
        print(f"[ERROR] Directory already exists: {skill_dir}")
        return None

    try:
        skill_dir.mkdir(parents=True)
        print(f"[OK] Created: {skill_dir}")
    except Exception as e:
        print(f"[ERROR] Could not create directory: {e}")
        return None

    skill_title = title_case(skill_name)

    # Write SKILL.md
    try:
        (skill_dir / "SKILL.md").write_text(
            SKILL_TEMPLATE.format(skill_name=skill_name, skill_title=skill_title)
        )
        print("[OK] Created SKILL.md")
    except Exception as e:
        print(f"[ERROR] Could not write SKILL.md: {e}")
        return None

    # Create resource directories
    for resource in resources:
        res_dir = skill_dir / resource
        res_dir.mkdir()

        if resource == "scripts" and examples:
            (res_dir / "example.py").write_text(
                EXAMPLE_SCRIPT.format(skill_name=skill_name)
            )
            print(f"[OK] Created scripts/example.py")
        elif resource == "references" and examples:
            (res_dir / "reference.md").write_text(
                EXAMPLE_REFERENCE.format(skill_title=skill_title)
            )
            print(f"[OK] Created references/reference.md")
        elif resource == "assets" and examples:
            (res_dir / "README.md").write_text(EXAMPLE_ASSET)
            print(f"[OK] Created assets/README.md")
        else:
            print(f"[OK] Created {resource}/")

    print(f"\n[OK] Skill '{skill_name}' initialized at {skill_dir}")
    print("\nNext steps:")
    print("  1. Edit SKILL.md — fill in description and body")
    if resources:
        print("  2. Add or customize files in resource directories")
        print("  3. Test any scripts by running them directly")
    print("  4. Validate: python quick_validate.py " + str(skill_dir))
    print("  5. Package: python package_skill.py " + str(skill_dir))

    return skill_dir


def main():
    parser = argparse.ArgumentParser(description="Initialize a new skill directory.")
    parser.add_argument("skill_name", help="Skill name (normalized to hyphen-case)")
    parser.add_argument("--path", required=True, help="Output parent directory")
    parser.add_argument(
        "--resources",
        default="",
        help="Comma-separated: scripts,references,assets",
    )
    parser.add_argument(
        "--examples",
        action="store_true",
        help="Create example placeholder files in resource directories",
    )
    args = parser.parse_args()

    raw = args.skill_name
    name = normalize_skill_name(raw)

    if not name:
        print("[ERROR] Skill name must contain at least one letter or digit.")
        sys.exit(1)
    if len(name) > MAX_SKILL_NAME_LENGTH:
        print(f"[ERROR] Name too long ({len(name)} chars, max {MAX_SKILL_NAME_LENGTH}).")
        sys.exit(1)
    if name != raw:
        print(f"[NOTE] Normalized '{raw}' → '{name}'")

    resources = parse_resources(args.resources)
    if args.examples and not resources:
        print("[ERROR] --examples requires --resources.")
        sys.exit(1)

    print(f"Initializing skill: {name}")
    print(f"  Path: {args.path}")
    if resources:
        print(f"  Resources: {', '.join(resources)}")
        if args.examples:
            print("  Examples: yes")
    print()

    result = init_skill(name, args.path, resources, args.examples)
    sys.exit(0 if result else 1)


if __name__ == "__main__":
    main()
