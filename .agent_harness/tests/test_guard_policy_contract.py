from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any
from unittest import TestCase


def load_core():
    root = Path(__file__).resolve().parents[2]
    path = root / ".agent_harness" / "command_guard_core.py"
    spec = importlib.util.spec_from_file_location("command_guard_core", path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"could not load guard core from {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


core = load_core()


class GuardPolicyContractTests(TestCase):
    @property
    def repo_root(self) -> Path:
        return Path(__file__).resolve().parents[2]

    @property
    def contract(self) -> dict[str, Any]:
        path = self.repo_root / ".agent_harness" / "guard_policy_contract.json"
        return json.loads(path.read_text(encoding="utf-8"))

    def hook_payload(self, command: str) -> str:
        return json.dumps({"cwd": str(self.repo_root), "tool_input": {"command": command}})

    def run_hook(
        self, relative_path: str, command_or_payload: str, *, raw_payload: bool = False, env: dict[str, str] | None = None
    ) -> subprocess.CompletedProcess[str]:
        payload = command_or_payload if raw_payload else self.hook_payload(command_or_payload)
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

    def hook_decision(self, relative_path: str, proc: subprocess.CompletedProcess[str]) -> str:
        if not proc.stdout:
            return "allow" if proc.returncode == 0 else "deny"

        data = json.loads(proc.stdout)
        hook_output = data["hookSpecificOutput"]
        if relative_path.endswith("permission_request_guard.py"):
            return "allow" if hook_output["decision"]["behavior"] == "allow" else "deny"
        return "allow" if hook_output["permissionDecision"] == "allow" else "deny"

    def hook_reason(self, relative_path: str, proc: subprocess.CompletedProcess[str]) -> str:
        if not proc.stdout:
            return ""
        data = json.loads(proc.stdout)
        hook_output = data["hookSpecificOutput"]
        if relative_path.endswith("permission_request_guard.py"):
            return hook_output["decision"]["message"]
        return hook_output["permissionDecisionReason"]

    def assert_core_decision(self, command: str, expected: str) -> None:
        decision = core.evaluate_bash_command(command, cwd=self.repo_root, project_root=self.repo_root)
        self.assertEqual(expected == "allow", decision.allowed, decision.reason)

    def test_contract_inventory_paths_exist(self) -> None:
        for name, relative_path in self.contract["surfaces"].items():
            with self.subTest(surface=name):
                self.assertTrue((self.repo_root / relative_path).exists(), relative_path)

    def test_settings_register_expected_guard_surfaces(self) -> None:
        codex_hooks = json.loads((self.repo_root / ".codex" / "hooks.json").read_text(encoding="utf-8"))
        codex_pre = codex_hooks["hooks"]["PreToolUse"][0]
        codex_permission = codex_hooks["hooks"]["PermissionRequest"][0]
        self.assertEqual("^Bash$", codex_pre["matcher"])
        self.assertEqual("^Bash$", codex_permission["matcher"])
        self.assertIn(".codex/hooks/bash_guard.py", codex_pre["hooks"][0]["command"])
        self.assertIn(".codex/hooks/permission_request_guard.py", codex_permission["hooks"][0]["command"])

        claude_settings = json.loads((self.repo_root / ".claude" / "settings.json").read_text(encoding="utf-8"))
        claude_pre = claude_settings["hooks"]["PreToolUse"][0]
        self.assertEqual("Bash", claude_pre["matcher"])
        self.assertIn(".claude/hooks/bash-guard.py", claude_pre["hooks"][0]["command"])
        self.assertIn("Bash(gh *)", claude_settings["permissions"]["allow"])
        self.assertIn("./.claude/hooks/bash-guard.py", claude_settings["sandbox"]["filesystem"]["denyWrite"])

    def test_policy_command_cases_match_core_and_all_bash_adapters(self) -> None:
        adapters = (
            ".codex/hooks/bash_guard.py",
            ".codex/hooks/permission_request_guard.py",
            ".claude/hooks/bash-guard.py",
        )
        env = dict(os.environ)
        env["CLAUDE_PROJECT_DIR"] = str(self.repo_root)

        for case in self.contract["command_cases"]:
            command = case["command"]
            expected = case["expected"]
            with self.subTest(surface="shared_core", case=case["name"]):
                self.assert_core_decision(command, expected)
            for adapter in adapters:
                with self.subTest(surface=adapter, case=case["name"]):
                    proc = self.run_hook(adapter, command, env=env)
                    self.assertEqual(expected, self.hook_decision(adapter, proc), self.hook_reason(adapter, proc))

    def test_pre_tool_adapters_scan_script_contents_consistently(self) -> None:
        adapters = (".codex/hooks/bash_guard.py", ".claude/hooks/bash-guard.py")
        env = dict(os.environ)
        env["CLAUDE_PROJECT_DIR"] = str(self.repo_root)

        with tempfile.TemporaryDirectory(dir=self.repo_root / ".agent_harness") as tmp:
            script = Path(tmp) / "guard.sh"
            script.write_text("rg SymbolExtractor src\n", encoding="utf-8")
            command = f"bash {script}"

            for adapter in adapters:
                with self.subTest(adapter=adapter):
                    proc = self.run_hook(adapter, command, env=env)

                    self.assertEqual("deny", self.hook_decision(adapter, proc))
                    self.assertIn("script contains blocked command", self.hook_reason(adapter, proc))

    def test_pre_tool_adapters_fail_closed_when_secret_scanner_is_unavailable(self) -> None:
        adapters = (".codex/hooks/bash_guard.py", ".claude/hooks/bash-guard.py")
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
                "exit 0\n",
                encoding="utf-8",
            )
            fake_git.chmod(0o700)

            env = dict(os.environ)
            env["CLAUDE_PROJECT_DIR"] = str(self.repo_root)
            env["PATH"] = str(bin_dir)

            for adapter in adapters:
                with self.subTest(adapter=adapter):
                    proc = self.run_hook(adapter, "git -c user.name=Codex commit -m test", env=env)

                    self.assertEqual("deny", self.hook_decision(adapter, proc))
                    self.assertIn("gitleaks is unavailable", self.hook_reason(adapter, proc))

    def test_adapters_fail_closed_on_malformed_payloads(self) -> None:
        adapters = (
            ".codex/hooks/bash_guard.py",
            ".codex/hooks/permission_request_guard.py",
            ".claude/hooks/bash-guard.py",
        )
        env = dict(os.environ)
        env["CLAUDE_PROJECT_DIR"] = str(self.repo_root)

        for adapter in adapters:
            with self.subTest(adapter=adapter):
                proc = self.run_hook(adapter, "{", raw_payload=True, env=env)

                self.assertEqual("deny", self.hook_decision(adapter, proc))
                self.assertIn("failed to parse", self.hook_reason(adapter, proc))
