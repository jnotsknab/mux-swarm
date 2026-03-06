#!/usr/bin/env python3
"""
Skill Packager — validates and packages a skill into a distributable .skill file.

Usage:
    python package_skill.py <path/to/skill-folder> [output-directory]

Examples:
    python package_skill.py skills/bundled/my-skill
    python package_skill.py skills/bundled/my-skill ./dist
"""

import sys
import zipfile
from pathlib import Path

# Import validator from same directory
sys.path.insert(0, str(Path(__file__).parent))
from quick_validate import validate_skill


def package_skill(skill_path, output_dir=None) -> Path | None:
    skill_path = Path(skill_path).resolve()

    if not skill_path.exists() or not skill_path.is_dir():
        print(f"[ERROR] Skill directory not found: {skill_path}")
        return None

    if not (skill_path / "SKILL.md").exists():
        print(f"[ERROR] SKILL.md not found in {skill_path}")
        return None

    print("Validating...")
    valid, message = validate_skill(skill_path)
    if not valid:
        print(f"[FAIL] {message}")
        print("Fix validation errors before packaging.")
        return None
    print(f"[OK] {message}")

    output_path = Path(output_dir).resolve() if output_dir else skill_path.parent
    output_path.mkdir(parents=True, exist_ok=True)

    skill_file = output_path / f"{skill_path.name}.zip"

    try:
        with zipfile.ZipFile(skill_file, "w", zipfile.ZIP_DEFLATED) as zf:
            for file_path in sorted(skill_path.rglob("*")):
                if file_path.is_file() and "__pycache__" not in file_path.parts and not file_path.name.endswith(".pyc"):
                    arcname = file_path.relative_to(skill_path.parent)
                    zf.write(file_path, arcname)
                    print(f"  + {arcname}")

        print(f"\n[OK] Packaged: {skill_file}")
        return skill_file

    except Exception as e:
        print(f"[ERROR] Failed to create .skill file: {e}")
        return None


def main():
    if len(sys.argv) < 2:
        print("Usage: python package_skill.py <skill-folder> [output-dir]")
        sys.exit(1)

    result = package_skill(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
    sys.exit(0 if result else 1)


if __name__ == "__main__":
    main()