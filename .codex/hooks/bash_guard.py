#!/usr/bin/env python3
"""
Codex PreToolUse Bash guard for Widthdom/CodeIndex.

Tool-specific adapter around the shared guard core in
.agent_harness/command_guard_core.py. Claude Code uses the sibling adapter at
.claude/hooks/bash-guard.py.
"""

from __future__ import annotations

import importlib.util
import json
import os
import re
import subprocess
import sys
from pathlib import Path


def load_core():
    repo_root = Path(__file__).resolve().parents[2]
    core_path = repo_root / ".agent_harness" / "command_guard_core.py"
    spec = importlib.util.spec_from_file_location("agent_harness.command_guard_core", core_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"could not load guard core from {core_path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


core = load_core()


def deny(reason: str) -> None:
    print(
        json.dumps(
            {
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": reason,
                }
            },
            ensure_ascii=False,
        )
    )
    sys.exit(2)


def load_payload() -> dict:
    try:
        return json.load(sys.stdin)
    except Exception as exc:
        deny(f"failed to parse Codex hook input; failing closed: {exc}")


def get_command(payload: dict) -> str:
    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command")
    if not isinstance(command, str):
        deny("Bash command missing from hook input; failing closed")
    return command


def resolve_project_root(cwd: Path) -> Path:
    try:
        proc = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            cwd=str(cwd),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=5,
            check=False,
        )
        if proc.returncode == 0:
            output = (proc.stdout or "").strip()
            if output:
                return Path(output).resolve()
        details = (proc.stderr or proc.stdout or f"exit {proc.returncode}").strip()
        deny(f"could not resolve git project root from {cwd}; failing closed: {details}")
    except Exception as exc:
        deny(f"could not resolve git project root from {cwd}; failing closed: {exc}")


def main() -> None:
    payload = load_payload()
    command = get_command(payload)
    cwd = Path(payload.get("cwd") or os.getcwd()).resolve()
    project_root = resolve_project_root(cwd)

    decision = core.evaluate_bash_command(command, cwd=cwd, project_root=project_root)
    if not decision.allowed:
        deny(decision.reason)

    for script in core.candidate_script_paths(command, cwd):
        if core.should_skip_script_scan(decision, script, project_root):
            continue
        script_decision = core.check_script_file(script, project_root)
        if not script_decision.allowed:
            deny(script_decision.reason)

    if re.search(r"(?i)(^|[\s;&|()`])git\s+commit\b", command):
        commit_decision = core.staged_secret_check(cwd)
        if not commit_decision.allowed:
            deny(commit_decision.reason)

    sys.exit(0)


if __name__ == "__main__":
    main()
