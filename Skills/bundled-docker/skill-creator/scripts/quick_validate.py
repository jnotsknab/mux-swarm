#!/usr/bin/env python3
"""
Quick validation for skills — checks SKILL.md frontmatter structure.

Usage:
    python quick_validate.py <skill_directory>
"""

import re
import sys
from pathlib import Path

try:
    import yaml
    HAS_YAML = True
except ImportError:
    HAS_YAML = False


MAX_NAME_LENGTH = 64
MAX_DESCRIPTION_LENGTH = 1024
ALLOWED_FRONTMATTER_KEYS = {"name", "description", "license"}


def validate_skill(skill_path) -> tuple[bool, str]:
    skill_path = Path(skill_path)

    if not skill_path.exists() or not skill_path.is_dir():
        return False, f"Directory not found: {skill_path}"

    skill_md = skill_path / "SKILL.md"
    if not skill_md.exists():
        return False, "SKILL.md not found"

    try:
        content = skill_md.read_text(encoding="utf-8")
    except Exception as e:
        return False, f"Could not read SKILL.md: {e}"

    if not content.startswith("---"):
        return False, "No YAML frontmatter found (must start with ---)"

    match = re.match(r"^---\n(.*?)\n---", content, re.DOTALL)
    if not match:
        return False, "Invalid frontmatter — could not find closing ---"

    frontmatter_text = match.group(1)

    # Parse frontmatter
    if HAS_YAML:
        try:
            fm = yaml.safe_load(frontmatter_text)
        except Exception as e:
            return False, f"Invalid YAML in frontmatter: {e}"
    else:
        # Fallback: simple key:value parser
        fm = {}
        for line in frontmatter_text.splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if ":" in line:
                k, v = line.split(":", 1)
                fm[k.strip()] = v.strip()

    if not isinstance(fm, dict):
        return False, "Frontmatter must be a YAML mapping"

    # Check for unexpected keys
    unexpected = set(fm.keys()) - ALLOWED_FRONTMATTER_KEYS
    if unexpected:
        return False, (
            f"Unexpected frontmatter key(s): {', '.join(sorted(unexpected))}. "
            f"Allowed: {', '.join(sorted(ALLOWED_FRONTMATTER_KEYS))}"
        )

    # Validate name
    if "name" not in fm:
        return False, "Missing 'name' in frontmatter"
    name = str(fm["name"]).strip()
    if not re.match(r"^[a-z0-9-]+$", name):
        return False, f"Name '{name}' must be hyphen-case (lowercase letters, digits, hyphens only)"
    if name.startswith("-") or name.endswith("-") or "--" in name:
        return False, f"Name '{name}' cannot start/end with hyphen or have consecutive hyphens"
    if len(name) > MAX_NAME_LENGTH:
        return False, f"Name too long ({len(name)} chars, max {MAX_NAME_LENGTH})"

    # Validate description
    if "description" not in fm:
        return False, "Missing 'description' in frontmatter"
    desc = str(fm["description"]).strip()
    if not desc:
        return False, "Description cannot be empty"
    if "<" in desc or ">" in desc:
        return False, "Description cannot contain angle brackets"
    if len(desc) > MAX_DESCRIPTION_LENGTH:
        return False, f"Description too long ({len(desc)} chars, max {MAX_DESCRIPTION_LENGTH})"

    # Check resource dirs are actually directories if they exist
    for res in ["scripts", "references", "assets"]:
        res_path = skill_path / res
        if res_path.exists() and not res_path.is_dir():
            return False, f"'{res}' exists but is not a directory"

    return True, f"Valid skill: {name}"


def main():
    if len(sys.argv) != 2:
        print("Usage: python quick_validate.py <skill_directory>")
        sys.exit(1)

    valid, message = validate_skill(sys.argv[1])
    print(message)
    sys.exit(0 if valid else 1)


if __name__ == "__main__":
    main()
