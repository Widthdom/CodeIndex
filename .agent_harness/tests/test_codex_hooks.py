from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path
from unittest import TestCase


class CodexHookAdapterTests(TestCase):
    @property
    def repo_root(self) -> Path:
        return Path(__file__).resolve().parents[2]

    def run_hook(self, relative_path: str, payload: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(self.repo_root / relative_path)],
            input=payload,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=10,
            check=False,
        )

    def denial_reason(self, relative_path: str, output: str) -> str:
        if not output:
            self.fail(f"{relative_path} did not emit hook JSON")
        data = json.loads(output)
        hook_output = data["hookSpecificOutput"]
        if relative_path.endswith("permission_request_guard.py"):
            return hook_output["decision"]["message"]
        return hook_output["permissionDecisionReason"]

    def test_malformed_payloads_fail_closed_with_parse_diagnostics(self) -> None:
        for hook in (".codex/hooks/bash_guard.py", ".codex/hooks/permission_request_guard.py"):
            with self.subTest(hook=hook):
                proc = self.run_hook(hook, "{")

                self.assertIn("failed to parse Codex hook input; failing closed", self.denial_reason(hook, proc.stdout))

    def test_missing_command_fails_closed_with_command_diagnostics(self) -> None:
        payload = json.dumps({"cwd": str(self.repo_root), "tool_input": {}})

        for hook in (".codex/hooks/bash_guard.py", ".codex/hooks/permission_request_guard.py"):
            with self.subTest(hook=hook):
                proc = self.run_hook(hook, payload)

                self.assertIn("Bash command missing from hook input; failing closed", self.denial_reason(hook, proc.stdout))

    def test_git_root_resolution_failures_fail_closed_with_root_diagnostics(self) -> None:
        missing_cwd = self.repo_root / ".agent_harness" / "__missing_codex_hook_cwd__"
        payload = json.dumps({"cwd": str(missing_cwd), "tool_input": {"command": "echo ok"}})

        for hook in (".codex/hooks/bash_guard.py", ".codex/hooks/permission_request_guard.py"):
            with self.subTest(hook=hook):
                proc = self.run_hook(hook, payload)

                reason = self.denial_reason(hook, proc.stdout)
                self.assertIn("could not resolve git project root", reason)
                self.assertIn("failing closed", reason)
