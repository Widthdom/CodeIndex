# CodeIndex Agent Guide

This file is the shared, authoritative agent guide for CodeIndex.
It is used by Codex, Claude Code, and any other coding agent working in this repository.

`AGENTS.md` and `CLAUDE.md` are entry points only. Do not duplicate full workflows there.
When this guide and an entry-point file disagree, this guide wins unless the difference is explicitly tool-specific.

## Read Order

For implementation tasks:

1. Read the agent entry point for your tool, if one was loaded automatically.
2. Read this file.
3. Read the relevant workflow in `.codex/workflows/`.
4. Read project-specific files referenced by that workflow, such as `docs/SELF_IMPROVEMENT.md`, `docs/DEVELOPER_GUIDE.md`, or `docs/TESTING_GUIDE.md`.
5. Read only the additional source files needed for the task.

## Search and Indexing Rules

For code search, do not use `grep`, `rg`, `python`, or a globally installed `cdidx`.
Use the locally built CodeIndex binary from this repository:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll
```

Before implementation, first check whether the local index already matches the current workspace:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll status --check --json
```

If the command exits `0` and reports `index_matches_workspace: true`, you may trust the existing `.cdidx/codeindex.db` without rebuilding it. If it exits with stale-index status or reports mismatched `workspace_check` counts, refresh the local index as documented by the project guidance. If the exact index-refresh command is documented elsewhere, use that documented command instead of inventing a new one.

This rule applies to code search and repository understanding. It does not forbid Git commands, build tools, test runners, package managers, or small shell checks that are not being used to search implementation code.

## Scope Rules

Keep changes focused on the requested issue or task.
Do not expand scope merely because an improvement is interesting.
Related issues may be included only when the relationship is clear and the same change naturally resolves them.
Use `.codex/workflows/issue-scope.md` for detailed scope rules.

## Planning Rules

Before editing, create a short plan.
For normal issue work, the plan must be at most 10 lines and include only:

- files or areas likely to change;
- validation commands;
- main risks.

Do not spend excessive output tokens on planning.

## Implementation Rules

Prefer the smallest correct fix.
Add or update tests when behavior changes.
Avoid unrelated refactors.
Preserve public behavior unless the issue explicitly requires a change.
When editing changelog content, verify that both English and Japanese entries are updated where the repository convention requires both.

## Documentation Rules

- Treat documentation as part of the feature contract, not as optional cleanup.
- If a change affects user-visible behavior, CLI/MCP output, flags, error messages, install/release behavior, or contributor/agent workflow, update the matching docs in the same change. For CodeIndex this usually means `README.md`, `docs/DEVELOPER_GUIDE.md`, `docs/TESTING_GUIDE.md`, `docs/SELF_IMPROVEMENT.md`, `INTEGRATION_POLICY.md`, `CLAUDE.md`, `AGENT_GUIDE.md`, or the relevant `.codex/workflows/*.md` file.
- Do not open or merge a PR with a user-visible change unless the required docs and changelog updates are present, or the PR body explicitly explains why no docs/changelog change is needed.
  - Changelog entries are required for user-visible or behavior-changing work. For ordinary implementation PRs, write the changelog entry as a bilingual fragment under `changelog.d/unreleased/`; do not update `CHANGELOG.md` directly as the default path.
  - Use issue-based fragment names and `issues:` front matter only when the work is actually tied to GitHub issues. For non-issue work, use a `+<slug>.<category>.md` fragment and omit `issues` entirely. Never write `issues: null` or `issues: []`.
  - Ordinary implementation PRs must not edit `CHANGELOG.md`. Reserve direct `CHANGELOG.md` edits for release-preparation PRs that aggregate fragments into a release note. If `CHANGELOG.md` is edited, update both English and Japanese sections in the same commit, and only after confirming the work is a release-preparation change.

## Repository Rules

- Follow `docs/DEVELOPER_GUIDE.md` for architecture and dependency policy. Production/runtime dependencies stay limited to `Microsoft.Data.Sqlite`.
- Follow `docs/TESTING_GUIDE.md` for test conventions, helpers, and parallelism rules.
- Follow `docs/SELF_IMPROVEMENT.md` when the task is about improving `cdidx` itself.
- If a change is user-facing, keep the matching tests, docs, and changelog entry in the same commit.
- Preserve cross-platform behavior when touching filesystem behavior, process execution, console output, or SQLite lifetime.
- Ask before implementing breaking, destructive, or user-workflow-changing changes.

## Commit Rules

Before each commit, follow `.codex/workflows/precommit.md`.
Commit messages must be in English and include relevant issue numbers.
Prefer PR body `Fixes #123` lines as the primary auto-close mechanism.

## Review Rules

For adversarial review, follow `.codex/workflows/adversarial-review.md`.
Reviews must focus on blocking/actionable issues, not nitpicks.

## PR and CI Rules

Follow `.codex/workflows/pr-finalize.md`.
CI watching must be bounded. Do not loop indefinitely.

## When You Cannot Complete an Operation

If an operation cannot be completed in the current environment, report:

- what failed;
- what you tried;
- the exact command or manual action needed;
- why it is needed.

If the user requested yellow text for handoff actions, use ANSI yellow when the terminal supports it.
