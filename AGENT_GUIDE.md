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
See `.codex/workflows/README.md` for the workflow directory map and rule-placement guidance.

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

Command-search enforcement is tool-specific and adapter-driven:

- Codex uses `.codex/hooks.json`, which invokes `.codex/hooks/bash_guard.py` and `.codex/hooks/permission_request_guard.py`.
- Claude Code uses `.claude/settings.json`, which invokes `.claude/hooks/bash-guard.py`.
- Both Bash guard adapters delegate shared command policy to `.agent_harness/command_guard_core.py`; update the shared core for common command policy and review both adapters only when tool-specific behavior changes.
- Codex uses the `codeindex_workspace` permission profile for workspace writes plus limited GitHub CLI network access to `github.com` and `api.github.com`.
- Codex may use normal development GitHub CLI commands including `gh issue list/view/create/edit/comment`, `gh pr list/view/create/edit/comment/ready/close`, `gh repo view`, and `gh status`.
- Keep `gh auth`, `gh api`, `gh secret`, `gh release`, `gh repo create`, `gh repo fork`, `gh repo delete`, and `gh pr merge` blocked. `gh api` is blocked because arbitrary REST/GraphQL calls can bypass subcommand-level policy intent; `gh pr merge` is blocked because it mutates remote PR state in a high-risk way.

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
Before adding a test method, check whether the scenario can share the closest existing method's setup and execution without obscuring a distinct contract. Prefer one cohesive method with a shared fixture for related read-only variants; keep separate methods when isolation, discovery identity, mutable state, or failure diagnosis materially benefits.
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
When an AI agent creates a commit, use `git commit --no-gpg-sign` so local signing-key passphrases do not block agent-driven commits.
Commit messages must be in English and include relevant issue numbers.
Prefer PR body `Fixes #123` lines as the primary auto-close mechanism.

## Review Rules

For adversarial review, follow `.codex/workflows/adversarial-review.md`.
Reviews must focus on blocking/actionable issues, not nitpicks.

## PR and CI Rules

Follow `.codex/workflows/pr-finalize.md`.
CI watching must be bounded. Do not loop indefinitely.

## Status Contract

- `status --json` and related JSON/MCP payloads currently expose the trust fields documented in `README.md` and `DEVELOPER_GUIDE.md`, including `fold_ready`, `fold_ready_reason`, `graph_table_available`, `graph_data_current`, `index_complete`, `index_incomplete_reasons`, `issues_table_available`, `file_issues_data_current`, `migration_in_progress`, `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`, `hotspot_family_ready`, `hotspot_family_degraded_reason`, `language_readiness`, `csharp_symbol_name_ready`, `csharp_metadata_target_ready`, `csharp_metadata_target_degraded_reason`, `indexed_head_commit`, `worktree_head_changed`, `indexed_head_sha`, `indexed_head_branch`, `indexed_head_timestamp`, `commits_ahead_of_indexed_head`, `head_freshness`, `index_writer_version`, `index_newer_than_reader`, `index_newer_than_reader_reason`, `unknown_extension_file_count`, `unknown_extension_files`, `unknown_extension_files_truncated`, `unknown_extension_file_path_limit`, `unknown_extension_extension_counts`, `unknown_extension_category_counts`, `unknown_extension_groups`, `extractors`, `git_executable`, `path_case_sensitive`, `data_dir`, `data_dir_source`, `data_dir_mode`, `db_file_mode`, `database_permission_policy`, `database_permission_diagnostics`, `mac_profile`, `mac_profile_diagnostics`, `db_size_bytes`, `wal_size_bytes`, `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`), `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`), `maintenance_guidance`, WAL checkpoint diagnostics (`read_only_fallback`, `wal_checkpoint_attempted`, `wal_checkpoint_succeeded`, `wal_checkpoint_skipped_reason`, `wal_checkpoint_failure_reason`, `wal_checkpoint_busy`, `wal_checkpoint_log_page_count`, `wal_checkpoint_checkpointed_page_count`, `wal_checkpoint_remaining_page_count`, `read_only_immutable_fallback`, `wal_stale_snapshot_risk`, `wal_stale_snapshot_reason`), `symbol_kinds`, `symbols_by_language`, status kind cap metadata (`symbol_kind_limit`, `symbol_kind_name_limit`, `symbol_kind_total_count`, `symbol_kind_omitted_count`, `symbol_kind_names_truncated`, `symbols_by_language_kind_total_counts`, `symbols_by_language_kind_omitted_counts`, `symbols_by_language_kind_names_truncated`), `process`, `last_index_run`, `last_failed_or_partial_index_run`, `last_failed_or_partial_index_run.progress_persisted`, `last_failed_or_partial_index_run.recovery_hint`, `last_failed_or_partial_index_run.file_errors`, `last_workspace_freshened_at`, `hooks`, `hook_diagnostics`, `trust_overrides`, MCP-only `mcp_session`, `mcp.rate_limit.bucket_limit`, `mcp.rate_limit.bucket_limit_rejection_count`, and the `status --check`-only `stale_after_seconds` / `index_age_seconds` threshold audit fields and `repair_commands`.
- `maintenance_guidance.fts_optimization` is the shared, read-only recommendation contract for status, explain, optimize preview, and optimize execution. Keep `recommended`, `action`, `reason`, `threshold_writes`, `observed_writes`, and `state` synchronized; stale or unavailable snapshots must not recommend mutation.
- A valid CLI `status --stale-after <duration>` implies the workspace check. Check-mode JSON includes `query_context.check_mode` (`explicit` or `implied_by_stale_after`) and `query_context.stale_after_seconds`; ordinary status JSON omits `query_context`.
- `database_size_attribution` is part of the synchronized status contract. Preserve its read-only main/WAL/SHM separation; exact logical reconciliation across object, freelist, and unexplained-residual bytes; table/index and page-type subtotals; 20-object/128-character sanitized bounds; and explicit `available=false` / stable `unavailable_reason` behavior without zero-valued unavailable object metrics.
- Explicit WAL truncate-checkpoint diagnostics must preserve SQLite's `(busy, log, checkpointed)` result, treat non-zero `busy` or positive remaining pages as unsuccessful with bounded machine reasons, accept `(0, -1, -1)` as the successful non-WAL no-op, and never expose raw exception text or paths.
- When any readiness field is degraded, the CLI adds `degraded_root_cause`, `degraded_reason`, `recommended_action`, `alternative_action`, and `readiness_degradations[]`. `degraded_root_cause` is the primary stable machine code; `readiness_degradations[]` lists every degraded field with `root_cause`, human reason, and remediation strings.
- `hotspot_family_degraded_reason` currently uses `hotspot_family_support_not_indexed`, `hotspot_family_metadata_stale`, `hotspot_family_disabled_at_index_time`, `partial_family_key_population`, and `hotspot_family_marker_fingerprint_incomplete`; the incomplete marker fingerprint code means marker traversal hit safety caps and should stay synchronized with README / developer-guide recovery notes.
- `issues_table_available` reports physical `file_issues` table presence only. `file_issues_data_current` reports whether the table is also stamped current for the active index generation.
- `graph_table_available` reports a queryable persisted reference generation, while `graph_data_current`, `reference_graph_complete`, and `index_complete` report current-generation coverage. Reference extraction is bounded at 50,000 lookup symbols, 20,000 lookup lines, 512 names per line, and 20,000 container candidates; `reference_extraction_limits`, `reference_graph_incomplete_reasons`, and `reference_extraction_cap_hits` publish cap state, and `last_index_run.reference_extraction_cap_hits` snapshots it per run. Cap hits persist per file and propagate degraded, non-authoritative absence semantics to callers, callees, deps, and impact. Indexed Crystal, Groovy, Tcl, Prolog, or `ambiguous_pl` rows with a missing or stale extractor stamp add `dynamic_reference_graph_contract_stale` to `reference_graph_incomplete_reasons` and keep graph readiness false until a normal index refresh rewrites them. A per-file extraction failure keeps successful graph rows queryable, stamps completeness false with bounded `last_failed_or_partial_index_run.file_errors`, and returns exit `11` unless `index --allow-partial` explicitly opts into exit `0`. While such file failures remain unresolved, a later scoped update automatically uses the normal incremental full-scan path so unrelated targets cannot clear the failure and successful recovery can restore every workspace-wide readiness contract without `--rebuild`.
- Successful CLI full/update indexing, immediate status/workspace status, and MCP indexing/status must derive `index_complete`, `index_incomplete_reasons`, `reference_graph_complete`, and `reference_graph_incomplete_reasons` from the same persisted-readiness snapshot. Symbols-only runs and persisted file-size, symbol-count, reference-count, extractor-failure, or reference-cap evidence make the generation incomplete. Legacy databases keep the complete compatibility default only when persisted rows do not prove an omission.
- `index_writer_version` records the `cdidx` version that last wrote to the DB (stamped into `codeindex_meta` as `cdidx_writer_version` on every full scan, update, and MCP index). `index_newer_than_reader` flips to `true` whenever any persisted numeric contract stamp in `codeindex_meta` (or unknown `PRAGMA user_version` readiness bits) exceeds the current binary's compiled maximum, so an older CLI re-opening a DB written by a newer CLI degrades loudly with an audit trail instead of silently dropping back to text-search fallbacks. `index_newer_than_reader_reason` enumerates the specific newer-than-reader stamps.
- `status` also surfaces indexed-HEAD freshness via `indexed_head_sha`, `indexed_head_branch`, `indexed_head_timestamp`, `commits_ahead_of_indexed_head`, and the compact `head_freshness` summary. They are stamped by `cdidx index` on every successful run (full scan AND partial update, distinct from `indexed_head_commit` which is full-scan only) on a best-effort basis (never blocks an otherwise-successful index) and omitted on non-git workspaces, detached HEAD (branch only), or legacy DBs created before this contract. `worktree_head_changed` compares runtime HEAD with this latest stamp when available and falls back to `indexed_head_commit` only for legacy DBs. `head_freshness.state=fresh` requires `status --check` to match the workspace, `fresh_but_incomplete` keeps matching-workspace freshness distinct from incomplete extraction coverage, and `state=head_current` only means the runtime HEAD matches the `indexed_head` selected by `indexed_head_source`.
- `status` also surfaces unknown-extension scan coverage via `unknown_extension_file_count`, stamped by successful full-repository index runs (`cdidx index <projectPath>` and MCP `index_project`) as the number of non-indexed files with non-empty extensions that do not map to a known language. Current scans also stamp `unknown_extension_files` as a path sample bounded by `unknown_extension_file_path_limit` items and the string-list decoded-character budget, `unknown_extension_files_truncated` when more paths existed than were emitted for either bound, and `unknown_extension_file_path_limit` as the item cap rather than a guarantee that that many paths are returned. Newer scans also expose `unknown_extension_extension_counts`, `unknown_extension_category_counts`, and `unknown_extension_groups`; groups classify common non-code buckets such as repository metadata, licenses, binary assets, configuration, structural metadata, and language-support candidates, and include `recommended_action` values of `ignore_configuration`, `first_class_structural_extraction`, or `language_support`. These fields are omitted on legacy DBs or before a current full scan has stamped them.
- `status` also surfaces extractor plugin and pattern-config runtime diagnostics via `extractors`, including loaded counts, the zero parent `retained_load_context_count`, the isolated-worker `load_context_lifecycle`, skipped file counts, and a bounded diagnostics list for incompatible or malformed plugin/pattern files. `extractors.pattern_configs[]` reports accepted sidecars with sanitized `path`, `source` (`workspace` or `user`), normalized `language`, and `rule_count`; workspace discovery stops at its explicit root and path identity follows the live filesystem case policy. Pattern rules and their 128-rule budget live in immutable workspace snapshots; reindex replaces the owning snapshot, and timeout cooldowns/diagnostics remain workspace-scoped. `extractors.snapshot_scope` and highest-first `registration_precedence` expose snapshot selection and the fixed `built_in > user_plugin > user_pattern > workspace_plugin > workspace_pattern` resolution order; replacing one workspace snapshot never mutates another. Diagnostic paths, categories, and messages are sanitized before output; `diagnostics[].category` is the stable machine-readable failure code.
- `status` surfaces worker-discovered post-extraction hook manifests and callback budgets through `hooks[]`, including stable assembly-qualified `hooks[].id`, `hooks[].callback_budget_ms`, and the worker-only `hooks[].load_context_lifecycle`. Assembly loading, module initialization, type inspection, and constructor validation occur only in deadline-, memory-, and output-bounded discovery workers, which are terminated after returning a manifest. `hook_diagnostics[]` reports sanitized discovery and callback diagnostics such as candidate-limit truncation, assembly load failure, constructor failure, callback failure, and timeout; `hook_diagnostics[].category` is the stable machine-readable failure code and `hook_diagnostics[].hook_id` links a concrete-hook failure to the corresponding manifest. Index runs enforce `CDIDX_HOOK_CALLBACK_BUDGET_MS` (default: 5000 ms) on scratch copies, discard timed-out mutations, and disable only the timed-out assembly-qualified hook ID for the remainder of the current run.
- `status` also surfaces accepted trust-boundary environment overrides through `trust_overrides[]`. Entries include `kind`, `environment_variable`, sanitized `value`, optional sanitized `path`, and `message`; current entries cover `CDIDX_TRUST_WORKSPACE_PLUGINS` workspace plugin discovery, `CDIDX_HOOKS_DIR` hook directory overrides, and the absolute `CDIDX_GIT_EXECUTABLE` executable override. `git_executable` reports the selected source, acceptance, stable reason, sanitized path, owner-only-write result, Unix mode, owner category, owner/ancestor trust (including POSIX sticky-ancestor and Windows ACL policy), and bounded `git --version` execution-probe result even when an explicit Git override is rejected. Build `git_executable` and its matching `trust_overrides[]` entry from the same resolution snapshot. Keep this visible runtime field registered in `status --explain` and in both README status-field tables.
- `status` also surfaces `.cdidx` data-directory permissions via `data_dir_mode` on POSIX filesystems. New `.cdidx` data directories are forced to `0700`; the field is omitted on Windows, URI DBs, or when the directory mode cannot be inspected.
- `status` also surfaces database Unix-mode hardening through `db_file_mode`, `database_permission_policy`, and optional `database_permission_diagnostics[]`. The default `best_effort` policy keeps SQLite-capable FUSE/network mounts usable while emitting the stable `database_permission_hardening_failed` warning and support-safe operation/target/reason/remediation entries for `IOException`, `UnauthorizedAccessException`, and `NotSupportedException`. `CDIDX_DB_PERMISSION_POLICY=strict` makes every applicable database/WAL/SHM mode operation mandatory and fails with the same stable error code plus remediation. Windows and explicit SQLite file URIs skip this POSIX-only enforcement.
- `status` also surfaces filesystem case-sensitivity via `path_case_sensitive`, stamped on every successful `cdidx index` run (full scan AND partial update, plus MCP-driven indexes) from `core.ignorecase` + a live filesystem probe. `true` means the volume is case-sensitive (`Foo.cs` and `foo.cs` are distinct); `false` means case-insensitive. Omitted on legacy DBs that predate the stamp. Use it to audit path-equality decisions on case-sensitive APFS, WSL NTFS / dev-drive, and ReFS mounts where the prior OS-keyed heuristic could mis-classify the workspace (#1546).
- `status` also surfaces Linux mandatory-access-control context via `mac_profile` when `/proc/self/attr/current` or `/proc/self/attr/exec` indicates an AppArmor or SELinux profile. If proc attribute reads fail on Linux, `mac_profile_diagnostics[]` reports bounded `path`, `category`, and `message` entries so users can distinguish "no profile" from "profile detection failed" (#1768, #3480).
- `status` also surfaces DB/WAL size, per-language symbol-kind histograms, current process heap/GC/working-set metrics, and the last successful index run metadata. `process` is captured at status-call time; `last_index_run` is persisted at the end of successful CLI and MCP index runs and can include a peak-memory summary when CLI `--memory-trace` was used. `last_index_run.bytes_read_skipped_file_count` and `bytes_read_incomplete` report whether unreadable files were omitted from the `bytes_read` total. `last_index_run.diagnostics`, `diagnostic_count`, and `diagnostics_truncated` carry bounded warnings for best-effort index metadata writes that failed after the index data itself was successfully written. `last_workspace_freshened_at` is the latest successful index/update timestamp and can be newer than `indexed_at` when a partial or no-op update confirms freshness without rewriting indexed file rows.
- MCP `status` also surfaces session diagnostics via `mcp_session` and rate limiter bucket cap diagnostics via `mcp.rate_limit.bucket_limit` / `mcp.rate_limit.bucket_limit_rejection_count`. `mcp_session` is not persisted DB state; it includes the current `log_level`, bounded captured `roots`, optional `client_info`, bounded optional `client_capabilities`, and an always-present `metrics` object. `mcp_session.metrics.enabled=false` explicitly identifies an unconfigured sink. An enabled sink reports `path`, `max_bytes`, `bytes_written`, `disposed`, `degraded`, `queue_capacity`, `queue_depth`, `queued_event_count`, `written_event_count`, `dropped_event_count`, `queue_full_drop_count`, `serialization_failure_count`, `write_failure_count`, `rotation_failure_count`, `batch_flush_count`, `consecutive_failure_count`, `recovery_count`, and bounded optional `next_retry_at`, `last_recovery_at`, and `last_failure`. Metrics degradation is telemetry state and does not by itself change MCP ping/HTTP liveness status. When audit logging is configured, `mcp_session.audit_log` and ping's `audit_log` also report `queued_record_count` and `written_record_count` alongside the existing queue, drop, write, and rotation diagnostics. Shutdown-only abandoned counts and timeout state remain in the final sink shutdown result and bounded stderr diagnostic because the server is no longer available to answer status or ping after shutdown. When advertised roots are capped, `roots_truncated`, `root_count`, `root_limit`, and `root_uri_length_limit` describe the truncation. When client capabilities are capped, `client_capabilities_truncated`, `client_capabilities_truncation_reason`, `client_capabilities_serialized_bytes`, `client_capabilities_byte_limit`, and `client_capabilities_depth_limit` describe the retained diagnostic subset. `mcp.rate_limit.bucket_limit` is the process-local cap across normalized `(partition, caller)` buckets: every direct call uses one fixed caller-wide coarse partition, canonical known tools additionally use secondary per-tool partitions, and unknown `batch_query` slots share one fixed invalid-slot partition per caller. `mcp.rate_limit.bucket_limit_rejection_count` counts calls denied because creating a new bucket would exceed that cap. After an immediate expired-bucket prune, `retry_after_ms` reports the earliest point when every charged token and required capacity constraint can admit the retry (#4547).
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
