# CodeIndex Agent Guide

This file is the shared, authoritative agent guide for CodeIndex.
It is used by Codex, Claude Code, and any other coding agent working in this repository.

`AGENTS.md` and `CLAUDE.md` are thin entry points only; they just redirect here. **Any new rule, policy, workflow pointer, or contract note must be added to this file (or to a `.codex/workflows/*.md` workflow), not to `AGENTS.md` or `CLAUDE.md`.** Tool-specific guidance goes under `Tool-Specific Notes` in this file. When this guide and an entry-point file disagree, this guide wins.

## Read Order

For implementation tasks:

1. Read the agent entry point for your tool, if one was loaded automatically (`AGENTS.md` for Codex, `CLAUDE.md` for Claude Code). Those files are thin entry points and only point here.
2. Read this file.
3. Read the relevant workflow in `.codex/workflows/` (see the Workflow Index below).
4. Read project-specific files referenced by that workflow, such as `SELF_IMPROVEMENT.md`, `DEVELOPER_GUIDE.md`, or `TESTING_GUIDE.md`.
5. Read only the additional source files needed for the task.

## Workflow Index

Task-specific procedures live in `.codex/workflows/`. The directory is a shared workflow library for all coding agents, not only Codex.

- issue fixing: `.codex/workflows/issue-fix.md`
- changelog fragments: `.codex/workflows/changelog-fragment.md`
- release changelog: `.codex/workflows/release-changelog.md`
- adversarial review: `.codex/workflows/adversarial-review.md`
- commit checks: `.codex/workflows/precommit.md`
- PR finalization and CI checks: `.codex/workflows/pr-finalize.md`
- related/new issue scope control: `.codex/workflows/issue-scope.md`

## Search and Indexing Rules

For CodeIndex work, dogfood the project-built CodeIndex binary.

Do not use `grep`, `rg`, `ripgrep`, `ag`, `ack`, `find`, `fd`, `locate`, `git grep`, Python scripts, or a globally installed `cdidx` for code search or repository discovery. Use the locally built CodeIndex binary from this repository:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll
```

Examples:

- `dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search SymbolExtractor`
- `dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll symbols --lang csharp`
- `dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll inspect src/CodeIndex/Indexer/SymbolExtractor.cs`

Before implementation, first check whether the local index already matches the current workspace:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll status --check --json
```

If the command exits `0` and reports `index_matches_workspace: true`, you may trust the existing `.cdidx/codeindex.db` without rebuilding it. If it exits with stale-index status or reports mismatched `workspace_check` counts, refresh the local index as documented by the project guidance. If the exact index-refresh command is documented elsewhere, use that documented command instead of inventing a new one.

This rule applies to code search and repository understanding. It does not forbid Git commands, build tools, test runners, package managers, or small shell checks that are not being used to search implementation code. Enforcement of forbidden tools is provided separately by the Claude and Codex guard hooks.

## Tool-Specific Notes

### Claude Code

- Follow the repository-tracked `.claude/settings.json` and `.claude/hooks/bash-guard.py` policy files when running in Claude Code.
- Do not edit those policy files during ordinary implementation work unless the task is explicitly about Claude Code guard behavior.
- For shell search and navigation, prefer the built-in Grep / Glob tools or the locally built `cdidx` binary described above.

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
- If a change affects user-visible behavior, CLI/MCP output, flags, error messages, install/release behavior, or contributor/agent workflow, update the matching docs in the same change. For CodeIndex this usually means `README.md`, `DEVELOPER_GUIDE.md`, `TESTING_GUIDE.md`, `SELF_IMPROVEMENT.md`, `INTEGRATION_POLICY.md`, `AGENT_GUIDE.md`, or the relevant `.codex/workflows/*.md` file. Do not put agent workflow or policy content in `AGENTS.md` / `CLAUDE.md`; they are thin entry points.
- Do not open or merge a PR with a user-visible change unless the required docs and changelog updates are present, or the PR body explicitly explains why no docs/changelog change is needed.
  - Changelog entries are required for user-visible or behavior-changing work. For ordinary implementation PRs, write the changelog entry as a bilingual fragment under `changelog.d/unreleased/`; do not update `CHANGELOG.md` directly as the default path.
  - Use issue-based fragment names and `issues:` front matter only when the work is actually tied to GitHub issues. For non-issue work, use a `+<slug>.<category>.md` fragment and omit `issues` entirely. Never write `issues: null` or `issues: []`.
  - Ordinary implementation PRs must not edit `CHANGELOG.md`. Reserve direct `CHANGELOG.md` edits for release-preparation PRs that aggregate fragments into a release note. If `CHANGELOG.md` is edited, update both English and Japanese sections in the same commit, and only after confirming the work is a release-preparation change.

## Repository Rules

- Follow `DEVELOPER_GUIDE.md` for architecture and dependency policy. Production/runtime dependencies stay limited to `Microsoft.Data.Sqlite`.
- Follow `TESTING_GUIDE.md` for test conventions, helpers, and parallelism rules.
- Follow `SELF_IMPROVEMENT.md` when the task is about improving `cdidx` itself.
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

## Status Contract

- `status --json` and related JSON/MCP payloads currently expose the trust fields documented in `README.md` and `DEVELOPER_GUIDE.md`, including `fold_ready`, `fold_ready_reason`, `graph_table_available`, `issues_table_available`, `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`, `hotspot_family_ready`, `hotspot_family_degraded_reason`, `csharp_symbol_name_ready`, `csharp_metadata_target_ready`, `indexed_head_commit`, `worktree_head_changed`, `index_writer_version`, `index_newer_than_reader`, and `index_newer_than_reader_reason`.
- When `fold_ready` is the only degraded readiness bit, the CLI also adds `degraded_reason`, `recommended_action`, and `alternative_action`.
- `index_writer_version` records the `cdidx` version that last wrote to the DB (stamped into `codeindex_meta` as `cdidx_writer_version` on every full scan, update, and MCP index). `index_newer_than_reader` flips to `true` whenever any persisted numeric contract stamp in `codeindex_meta` (or unknown `PRAGMA user_version` readiness bits) exceeds the current binary's compiled maximum, so an older CLI re-opening a DB written by a newer CLI degrades loudly with an audit trail instead of silently dropping back to text-search fallbacks. `index_newer_than_reader_reason` enumerates the specific newer-than-reader stamps.
- `status` also surfaces indexed-HEAD freshness via `indexed_head_sha`, `indexed_head_branch`, `indexed_head_timestamp`, and `commits_ahead_of_indexed_head`. They are stamped by `cdidx index` on every successful run (full scan AND partial update, distinct from `indexed_head_commit` which is full-scan only) on a best-effort basis (never blocks an otherwise-successful index) and omitted on non-git workspaces, detached HEAD (branch only), or legacy DBs created before this contract.
- Keep `README.md`, `DEVELOPER_GUIDE.md`, and this file synchronized if this contract changes.

## Reference Extraction

- Dockerfile multi-stage builds now emit `call`-kind reference edges for `FROM <stage> AS <new>` and `COPY --from=<stage>` when the source name matches a named stage in the same file, so `callers` and `impact` can follow stage dependencies instead of treating intermediate stages as unused.
- Rust macro invocations (`name!(...)` / `name![...]` / `name!{...}`) now emit `call`-kind reference edges, while the `macro_rules!` declaration keyword remains suppressed so macro definitions do not double-count as calls.

## When You Cannot Complete an Operation

If an operation cannot be completed in the current environment, report:

- what failed;
- what you tried;
- the exact command or manual action needed;
- why it is needed.

If the user requested yellow text for handoff actions, use ANSI yellow when the terminal supports it.
