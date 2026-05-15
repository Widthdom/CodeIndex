# Claude Code Entry Point

Read `AGENT_GUIDE.md` first. It is the shared source of truth for CodeIndex agent behavior.

For task-specific procedures, read the relevant workflow in `.codex/workflows/`:

- issue fixing: `.codex/workflows/issue-fix.md`
- changelog fragments: `.codex/workflows/changelog-fragment.md`
- release changelog: `.codex/workflows/release-changelog.md`
- adversarial review: `.codex/workflows/adversarial-review.md`
- commit checks: `.codex/workflows/precommit.md`
- PR finalization and CI checks: `.codex/workflows/pr-finalize.md`
- related/new issue scope control: `.codex/workflows/issue-scope.md`

The `.codex/workflows/` directory is a shared workflow library for all coding agents, not only Codex.

## Claude Code Notes

- Follow the repository-tracked `.claude/settings.json` and `.claude/hooks/bash-guard.py` policy files when running in Claude Code.
- Do not edit those policy files during ordinary implementation work unless the task is explicitly about Claude Code guard behavior.
- For shell search and navigation, prefer the built-in Grep / Glob tools or the locally built `cdidx` binary described in `AGENT_GUIDE.md`.

## Status Contract

- `status --json` and related JSON/MCP payloads currently expose the trust fields documented in `README.md` and `DEVELOPER_GUIDE.md`, including `fold_ready`, `fold_ready_reason`, `graph_table_available`, `issues_table_available`, `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`, `hotspot_family_ready`, `hotspot_family_degraded_reason`, `csharp_symbol_name_ready`, `csharp_metadata_target_ready`, `indexed_head_commit`, and `worktree_head_changed`.
- When `fold_ready` is the only degraded readiness bit, the CLI also adds `degraded_reason`, `recommended_action`, and `alternative_action`.
- Keep the three docs synchronized if this contract changes.

## Reference Extraction

- Dockerfile multi-stage builds now emit `call`-kind reference edges for `FROM <stage> AS <new>` and `COPY --from=<stage>` when the source name matches a named stage in the same file, so `callers` and `impact` can follow stage dependencies instead of treating intermediate stages as unused.
- Rust macro invocations (`name!(...)` / `name![...]` / `name!{...}`) now emit `call`-kind reference edges, while the `macro_rules!` declaration keyword remains suppressed so macro definitions do not double-count as calls.
