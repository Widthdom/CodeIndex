from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
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

    def run_hook_with_env(
        self, relative_path: str, payload: str, env: dict[str, str]
    ) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(self.repo_root / relative_path)],
            input=payload,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=10,
            check=False,
            env=env,
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

    def test_codex_bash_guard_secret_check_detects_git_commit_with_global_options(self) -> None:
        with tempfile.TemporaryDirectory(dir=self.repo_root / ".agent_harness") as tmp:
            bin_dir = Path(tmp) / "bin"
            bin_dir.mkdir()
            fake_git = bin_dir / "git"
            fake_git.write_text(
                "#!/bin/sh\n"
                "if [ \"$1\" = \"rev-parse\" ]; then\n"
                f"  printf '%s\\n' '{self.repo_root}'\n"
                "  exit 0\n"
                "fi\n"
                "printf 'unexpected git invocation\\n' >&2\n"
                "exit 1\n",
                encoding="utf-8",
            )
            fake_git.chmod(0o700)

            env = dict(os.environ)
            env["PATH"] = str(bin_dir)
            payload = json.dumps(
                {
                    "cwd": str(self.repo_root),
                    "tool_input": {"command": "git -c user.name=Codex commit -m test"},
                }
            )

            proc = self.run_hook_with_env(".codex/hooks/bash_guard.py", payload, env)

            reason = self.denial_reason(".codex/hooks/bash_guard.py", proc.stdout)
            self.assertIn("gitleaks is unavailable", reason)
