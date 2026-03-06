#!/usr/bin/env python3
"""Summarize Qwe session JSON files into a markdown report."""

from __future__ import annotations

import argparse
import json
import os
import re
from collections import Counter
from pathlib import Path
from typing import Any, Iterable


def build_patterns(allowed_paths: list[str]) -> list[re.Pattern]:
    """Build artifact detection patterns from allowed paths config."""
    patterns = []
    for path in allowed_paths:
        if path:
            escaped = re.escape(path)
            patterns.append(re.compile(f'{escaped}[^\\s"\',)]+', re.I))
    return patterns


def get_allowed_paths(cli_paths: list[str] | None) -> list[str]:
    """Resolve allowed paths from CLI args or environment variable."""
    if cli_paths:
        return cli_paths
    env_paths = os.environ.get("QWE_ALLOWED_PATHS", "")
    if env_paths:
        return [p.strip() for p in env_paths.split(os.pathsep) if p.strip()]
    return []


WRITE_TOOLS = {
    "filesystem_write_file",
    "filesystem_create_directory",
    "filesystem_move_file",
}


def is_write_tool(name: str) -> bool:
    lower = name.lower()
    return lower in WRITE_TOOLS or "write" in lower or "save" in lower or "create_dir" in lower


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Summarize one or more session directories.")
    parser.add_argument("session_dirs", nargs="+", help="Paths to one or more session directories")
    parser.add_argument(
        "--allowed-paths",
        nargs="*",
        metavar="PATH",
        help="Paths to scan for artifacts (e.g. sandbox path). Falls back to QWE_ALLOWED_PATHS env var.",
    )
    return parser.parse_args()


def load_json(path: Path) -> dict[str, Any] | None:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return None
    if not text.strip():
        return None
    try:
        data = json.loads(text)
    except json.JSONDecodeError:
        return None
    if not isinstance(data, dict) or not data:
        return None
    return data


def extract_strings(value: Any) -> Iterable[str]:
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from extract_strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from extract_strings(item)


def find_artifacts(strings: Iterable[str], patterns: list[re.Pattern]) -> set[str]:
    artifacts: set[str] = set()
    for text in strings:
        for pattern in patterns:
            for match in pattern.findall(text):
                match = match.rstrip(".,;)`'\"")
                if match:
                    artifacts.add(match)
    return artifacts


def parse_agent_name(arguments: Any) -> str | None:
    if isinstance(arguments, dict):
        v = arguments.get("agentName")
        return v if isinstance(v, str) else None
    if isinstance(arguments, str):
        try:
            parsed = json.loads(arguments)
            if isinstance(parsed, dict):
                v = parsed.get("agentName")
                return v if isinstance(v, str) else None
        except json.JSONDecodeError:
            pass
    return None


def parse_signal_outcome(arguments: Any) -> str | None:
    if isinstance(arguments, str):
        try:
            arguments = json.loads(arguments)
        except json.JSONDecodeError:
            return None
    if not isinstance(arguments, dict):
        return None
    status = arguments.get("status", "")
    summary = arguments.get("summary", "")
    artifact_str = arguments.get("artifacts", "")
    parts = [p for p in [status, summary] if p]
    outcome = ": ".join(parts) if parts else None
    if artifact_str and outcome:
        outcome = f"{outcome} (artifacts: {artifact_str})"
    return outcome


def summarize_session(session_dir: Path, patterns: list[re.Pattern]) -> str:
    session_files = sorted(session_dir.rglob("*_session.json"))
    if not session_files:
        return f"No session files found in: {session_dir}"

    session_type = "swarm" if len(session_files) > 1 else "chat"
    lines = [
        f"## Session: {session_dir.name}  [{session_type}]",
        "",
    ]

    for session_file in session_files:
        data = load_json(session_file)
        if not data:
            continue

        agent_name = session_file.stem.removesuffix("_session")
        messages = data.get("chatHistoryProviderState", {}).get("messages", [])
        if not isinstance(messages, list):
            continue

        tool_calls: Counter[str] = Counter()
        delegated_agents: set[str] = set()
        artifacts: set[str] = set()
        last_outcome: str | None = None
        last_assistant_text: str | None = None
        last_delegated_to: str | None = None
        last_delegation_idx: int = -1
        last_assistant_text_idx: int = -1
        msg_idx: int = 0
        pending_write_calls: set[str] = set()

        for message in messages:
            if not isinstance(message, dict):
                continue
            role = message.get("role")
            contents = message.get("contents", [])
            if not isinstance(contents, list):
                continue

            if role == "assistant":
                chunks = []
                for c in contents:
                    if isinstance(c, dict) and c.get("$type") == "text":
                        t = c.get("text") or c.get("content")
                        if isinstance(t, str):
                            chunks.append(t)
                if chunks:
                    last_assistant_text = " ".join(chunks).strip()
                    last_assistant_text_idx = msg_idx
            msg_idx += 1

            for content in contents:
                if not isinstance(content, dict):
                    continue
                ctype = content.get("$type")

                if ctype == "functionCall":
                    name = content.get("name", "")
                    call_id = content.get("callId")
                    if isinstance(name, str) and name:
                        tool_calls[name] += 1
                        if name == "delegate_to_agent":
                            agent = parse_agent_name(content.get("arguments"))
                            if agent:
                                delegated_agents.add(agent)
                                last_delegated_to = agent
                                last_delegation_idx = msg_idx
                        if name == "signal_task_complete":
                            outcome = parse_signal_outcome(content.get("arguments"))
                            if outcome:
                                last_outcome = outcome
                        if is_write_tool(name) and call_id:
                            pending_write_calls.add(call_id)

                elif ctype == "functionResult":
                    call_id = content.get("callId")
                    if call_id in pending_write_calls:
                        artifacts.update(find_artifacts(extract_strings(content), patterns))
                        pending_write_calls.discard(call_id)

        if not artifacts and last_outcome and patterns:
            artifacts.update(find_artifacts([last_outcome], patterns))

        if last_outcome:
            outcome = last_outcome
        elif last_delegated_to and last_delegation_idx > last_assistant_text_idx:
            outcome = f"Delegated to: {last_delegated_to}"
        else:
            outcome = last_assistant_text or "No outcome captured."

        lines += [
            f"### Agent: {agent_name}",
            f"- Tool calls: {format_tool_calls(tool_calls)}",
            f"- Delegated agents: {format_set(delegated_agents)}",
            f"- Artifacts: {format_set(artifacts)}",
            f"- Final outcome: {outcome}",
            "",
        ]

    return "\n".join(lines)


def format_tool_calls(counter: Counter[str]) -> str:
    if not counter:
        return "None"
    return ", ".join(
        f"{n} (x{c})" if c > 1 else n for n, c in sorted(counter.items())
    )


def format_set(items: set[str]) -> str:
    return ", ".join(sorted(items)) if items else "None"


def main() -> None:
    args = parse_args()
    allowed_paths = get_allowed_paths(args.allowed_paths)
    patterns = build_patterns(allowed_paths)

    if not patterns:
        print("# Warning: No allowed paths provided — artifact detection disabled.")
        print("# Pass paths via --allowed-paths or QWE_ALLOWED_PATHS env var.")
        print()

    total = len(args.session_dirs)
    print("# Session Summaries")
    print(f"Sessions scanned: {total}")
    print()

    for i, raw_path in enumerate(args.session_dirs, start=1):
        session_dir = Path(raw_path).expanduser()
        print(f"{'=' * 60}")
        print(f"# Session {i} of {total}: {session_dir.name}")
        print(f"{'=' * 60}")
        print()
        if not session_dir.exists():
            print(f"Session directory not found: {session_dir}")
        else:
            print(summarize_session(session_dir, patterns))
        print()

    print(f"{'=' * 60}")
    print("# End of Sessions")
    print(f"{'=' * 60}")


if __name__ == "__main__":
    main()
