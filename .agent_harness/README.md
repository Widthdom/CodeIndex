# Shared Agent Guard Harness

This directory contains the shared command-policy core used by both Claude Code
and Codex hook adapters in this repository.

Policy logic lives in `command_guard_core.py`.
Adapter entrypoints live in `.claude/hooks/` and `.codex/hooks/`.

`guard_policy_contract.json` is the shared inventory for high-value guard
policy cases and tool surfaces. The contract tests load it and run the same
allow/deny cases through the shared core, Codex hook adapters, Claude hook
adapter, and settings/hook registrations so drift is visible during review.
