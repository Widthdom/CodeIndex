#!/usr/bin/env python3
"""
Claude Code PreToolUse Bash guard for Widthdom/CodeIndex.

Tool-specific adapter around the shared guard core in
.agent_harness/command_guard_core.py. Codex uses the sibling adapter at
.codex/hooks/bash_guard.py.
"""

from __future__ import annotations

import importlib.util
import json
import os
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


def emit_allow(reason: str) -> None:
    print(
        json.dumps(
            {
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "allow",
                    "permissionDecisionReason": reason,
                }
            },
            ensure_ascii=False,
        )
    )
    sys.exit(0)


def emit_deny(reason: str) -> None:
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
    sys.exit(0)


def load_payload() -> dict:
    try:
        return json.load(sys.stdin)
    except Exception as exc:
        emit_deny(f"failed to parse Claude Code hook input; failing closed: {exc}")


def get_command(payload: dict) -> str:
    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command")
    if not isinstance(command, str):
        emit_deny("Bash command missing from hook input; failing closed")
    return command


def resolve_project_root(cwd: Path) -> Path:
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root:
        return Path(env_root).resolve()

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
    except Exception:
        pass

    return cwd.resolve()


def main() -> None:
    payload = load_payload()
    command = get_command(payload)
    cwd = Path(payload.get("cwd") or os.getcwd()).resolve()
    project_root = resolve_project_root(cwd)

    decision = core.evaluate_bash_command(command, cwd=cwd, project_root=project_root)
    if not decision.allowed:
        emit_deny(decision.reason)

    for script in core.candidate_script_paths(command, cwd):
        if core.should_skip_script_scan(decision, script, project_root):
            continue
        script_decision = core.check_script_file(script, project_root)
        if not script_decision.allowed:
            emit_deny(script_decision.reason)

    if core.command_is_git_commit(command):
        commit_decision = core.staged_secret_check(cwd)
        if not commit_decision.allowed:
            emit_deny(commit_decision.reason)

    emit_allow(decision.reason)


if __name__ == "__main__":
    main()
