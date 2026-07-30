using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private sealed record StatusFieldExplanation(
        string FieldName,
        string Label,
        string ReadyText,
        string DegradedText,
        string Remediation);

    private static readonly StatusFieldExplanation[] StatusReadinessFields =
    [
        new(
            "graph_table_available",
            "Reference graph table",
            "reference, caller, callee, impact, unused, and hotspot queries can read indexed reference edges.",
            "reference graph queries degrade to empty or incomplete results because the symbol_references table is missing.",
            "Run `cdidx index <projectPath>` to rebuild the graph-capable index."),
        new(
            "graph_data_current",
            "Reference graph generation",
            "all indexed reference rows belong to a complete current index generation.",
            "successful graph rows remain queryable, but one or more files failed and graph coverage is incomplete.",
            "Fix the per-file error reported in `last_failed_or_partial_index_run`, then rerun the same index command; do not rebuild unless separately required."),
        new(
            "reference_graph_complete",
            "Reference extraction coverage",
            "reference-extraction cap state is available, current dynamic-language extractor contracts are stamped, and extraction completed without hitting a hard lookup or container-candidate safety cap.",
            "reference-extraction cap state is unavailable, a dynamic-language extractor stamp is stale, or one or more files hit a hard cap; absent graph edges are not authoritative.",
            "Inspect `reference_graph_incomplete_reasons` and `reference_extraction_cap_hits.state_available`: refresh indexing for unavailable state or stale contracts; for a hard cap, inspect `files`, reduce or exclude the generated/pathological source, and rerun indexing."),
        new(
            "issues_table_available",
            "Validation issues table",
            "the file_issues table exists in this index.",
            "validate output degrades to empty because the file_issues table is missing.",
            "Run `cdidx index <projectPath>` to rebuild the issue table."),
        new(
            "file_issues_data_current",
            "Validation issues data",
            "file_issues rows are stamped current for this index generation.",
            "file_issues rows may be stale or partial for this index generation.",
            "Run `cdidx index <projectPath>` to refresh file issue rows."),
        new(
            "migration_in_progress",
            "Migration/write state",
            "no index write or migration is currently in progress.",
            "an index write or migration is in progress, so readiness may be temporarily degraded.",
            "Wait for the active `cdidx index` run to finish, then rerun `cdidx status --json`."),
        new(
            "index_complete",
            "Index generation completeness",
            "the latest persisted index generation completed every candidate file.",
            "one or more files failed while successful files and their graph rows were committed.",
            "Fix the structured per-file failure under `last_failed_or_partial_index_run.file_errors`, then rerun the same index command; a rebuild is not required."),
        new(
            "sql_graph_contract_ready",
            "SQL graph contract",
            "SQL reference/dependency rows were written with the current call-column and qualified-name contract.",
            "SQL graph/dependency readers may return stale or incomplete results.",
            "Run `cdidx index <projectPath>` to rewrite SQL graph rows."),
        new(
            "hotspot_family_ready",
            "Hotspot family contract",
            "cross-file hotspot family grouping is stamped for all supported languages in this index.",
            "cross-file hotspot grouping may be degraded for one or more languages.",
            "Run `cdidx index <projectPath> --rebuild` to restamp authoritative hotspot families for every indexed row."),
        new(
            "csharp_symbol_name_ready",
            "C# symbol-name contract",
            "C# exact-name lookup uses authoritative persisted names for operators, conversions, and indexers.",
            "C# exact-name lookup for operators, conversions, and indexers may fall back to older canonical names.",
            "Run `cdidx index <projectPath>` to upgrade canonical C# symbol names."),
        new(
            "csharp_metadata_target_ready",
            "C# metadata target contract",
            "deps and impact use authoritative C# metadata-attribute targets.",
            "deps and impact metadata-attribute edges fall back to legacy signature/name heuristics.",
            "Run `cdidx index <projectPath>` to restamp authoritative C# metadata targets."),
        new(
            "fold_ready",
            "Unicode exact-name fold contract",
            "--exact-name can use Unicode NFKC + CaseFold equality.",
            "--exact-name falls back to ASCII COLLATE NOCASE, so non-ASCII casing pairs may not match.",
            "Run `cdidx backfill-fold` to restamp folded-name columns in place, or `cdidx index <projectPath> --rebuild` for a full rebuild."),
        new(
            "index_newer_than_reader",
            "Reader compatibility",
            "this cdidx binary understands all persisted index contract versions.",
            "this DB was written by a newer cdidx, so older readers may degrade instead of trusting newer contract stamps.",
            "Run status with a current cdidx binary, or rebuild the DB with the version you intend to use."),
    ];

    private static readonly StatusFieldExplanation[] StatusExplainFields =
        StatusReadinessFields.Concat(
        [
            new(
                "git_head",
                "Runtime Git HEAD",
                "the current workspace Git HEAD commit was resolved at status time.",
                "the field is absent outside a Git checkout or when Git HEAD cannot be resolved.",
                "Run inside a Git workspace or pass a database tied to a Git workspace to compare index stamps."),
            new(
                "git_is_dirty",
                "Runtime Git dirty state",
                "`true` means git status reported uncommitted changes, including untracked files; `false` means no changes were reported.",
                "the field is absent outside a Git checkout or when dirty-state detection is unavailable.",
                "Run `git status` in the workspace to inspect uncommitted changes directly."),
            new(
                "git_executable",
                "Trusted Git executable selection",
                "`accepted=true` means cdidx validated the absolute Git path, metadata type, owner/mode and ancestor trust, then successfully executed `git --version`.",
                "`accepted=false` includes a stable `reason` identifying the failed path, metadata, owner, mode, ancestor, or execution probe.",
                $"Set `{GitHelper.GitExecutableEnvironmentVariable}` to a trusted absolute `git` path (`git.exe` on Windows), then inspect the nested diagnostics again."),
            new(
                "indexed_head_commit",
                "Legacy full-scan HEAD stamp",
                "the index records the Git HEAD from the most recent successful full scan for legacy compatibility.",
                "this full-scan-only stamp can differ from `indexed_head_sha` after incremental indexing and may be absent in legacy or non-Git indexes.",
                "Prefer `indexed_head_sha` for current freshness checks; rebuild or run `cdidx index <projectPath>` when only this legacy stamp is available."),
            new(
                "worktree_head_changed",
                "Worktree HEAD drift",
                "`false` means the runtime HEAD matches the latest index HEAD stamp; `true` means the checkout moved since the index stamp.",
                "the field is absent when neither `indexed_head_sha` nor the legacy `indexed_head_commit` can be compared with runtime HEAD.",
                "Run `cdidx index <projectPath>` to refresh the index for the current checkout."),
            new(
                "indexed_head_sha",
                "Latest index HEAD stamp",
                "the index records the Git HEAD from the last successful index run, including incremental updates.",
                "the field is absent in legacy indexes, non-Git workspaces, or when the index run could not resolve HEAD.",
                "Use this field before `indexed_head_commit` when auditing freshness after incremental indexing."),
            new(
                "indexed_head_branch",
                "Latest index branch stamp",
                "the index records the branch short name captured with `indexed_head_sha`.",
                "the field is absent for detached HEAD, legacy indexes, non-Git workspaces, or unresolved branch names.",
                "Use it as context for `indexed_head_sha`; rerun `cdidx index <projectPath>` after switching branches."),
            new(
                "indexed_head_timestamp",
                "Latest index HEAD timestamp",
                "the index records when `indexed_head_sha` and `indexed_head_branch` were stamped.",
                "the field is absent in legacy indexes or when the index run could not persist the timestamp.",
                "Rerun `cdidx index <projectPath>` to refresh the timestamp with the current checkout."),
            new(
                "commits_ahead_of_indexed_head",
                "Commits ahead of indexed HEAD",
                "`0` means runtime HEAD is not ahead of `indexed_head_sha`; positive values mean the checkout advanced after indexing.",
                "the field is absent when Git comparison is unavailable or history is not comparable.",
                "Run `cdidx index <projectPath>` when the value is positive before trusting freshness-sensitive results."),
            new(
                "data_dir_mode",
                "Data directory mode",
                "the field reports the current cdidx data directory's Unix permission mode, such as `0700`, when the platform exposes it.",
                "the field is absent for URI databases, missing directories, platforms without Unix file modes, or readers that cannot inspect the directory.",
                "Use this field for support-safe permission audits; inspect the data directory locally when the field is absent or broader than expected."),
            new(
                "db_file_mode",
                "Database file mode",
                "the field reports the active database's Unix permission mode, such as `0600`, when cdidx can inspect it.",
                "the field is absent for URI databases, missing files, platforms without Unix file modes, or best-effort mode reads that produced a permission diagnostic.",
                "Inspect `database_permission_diagnostics`; move the database or correct its owner-only permissions when the mode cannot be read or is broader than expected."),
            new(
                "database_permission_policy",
                "Database permission policy",
                "`best_effort` keeps usable SQLite databases available while reporting Unix-mode hardening failures; `strict` requires every applicable mode operation to succeed.",
                "an invalid configured policy fails before the database opens, while strict mode fails with `database_permission_hardening_failed` when enforcement is unavailable.",
                $"Set `{DatabasePermissionPolicy.EnvironmentVariable}` to `{DatabasePermissionPolicy.BestEffortName}` (default) or `{DatabasePermissionPolicy.StrictName}` according to the filesystem and security requirement."),
            new(
                "database_permission_diagnostics",
                "Database permission diagnostics",
                "an absent field means no applicable Unix-mode operation failed during this database open and status read.",
                "entries identify the failed `operation`, database `target`, stable `reason`, support-safe message, and `recommended_action` without exposing the database path.",
                $"Follow each `recommended_action`, or set `{DatabasePermissionPolicy.EnvironmentVariable}={DatabasePermissionPolicy.StrictName}` when permission hardening must be mandatory."),
            new(
                StatusResult.SqliteConnectionPolicyJsonFieldName,
                "SQLite connection policy",
                "the object reports the active/open modes, pooling and immutable-URI choices, command timeout, cancellation requirement, and WAL snapshot-risk diagnostics used by this status read.",
                "read-only fallback or stale-snapshot risk fields identify when the preferred query-only connection path could not be used safely or may omit hot WAL content.",
                "Inspect the nested policy and WAL diagnostics; avoid explicit immutable mode for a hot WAL database, or rerun after the writer checkpoints and closes."),
            new(
                "maintenance_guidance",
                "Database maintenance guidance",
                "`fts_optimization` reports one shared `recommended`, `action`, `reason`, `threshold_writes`, `observed_writes`, and `state` decision used by status and optimize.",
                "`state=stale` or `state=unavailable` suppresses an optimize recommendation because the persisted write counter or database-page snapshot is not trustworthy.",
                "Wait for active indexing to finish and rerun `cdidx status --json`; run `cdidx optimize --dry-run --json` to inspect the same decision before mutation."),
            new(
                "unknown_extension_file_count",
                "Unknown extension inventory",
                "`0` means the last successful full scan found no non-empty unknown extensions; positive values summarize skipped files with extensions that no language recognized.",
                "the field is absent in legacy indexes or before a current scanner stamps unknown-extension inventory metadata.",
                "Review `unknown_extension_files` and `unknown_extension_groups` in `cdidx status --json`, then update ignores or language support before rebuilding."),
            new(
                "head_freshness",
                "Compact HEAD freshness summary",
                "`state=fresh` means `status --check` proved the complete index matches the workspace; `state=fresh_but_incomplete` keeps workspace freshness distinct from failed-file coverage; without `--check`, `state=head_current` means only the runtime HEAD matched `indexed_head` (see `indexed_head_source`).",
                "`state=stale`, `state=stale_and_incomplete`, `state=head_changed`, `state=check_unavailable`, or `state=unchecked` means consumers should inspect `state_reason`, `index_complete`, and the nested head fields before trusting freshness-sensitive results.",
                "Use this summary for machine routing, and use `indexed_head_sha` over legacy `indexed_head_commit` when `indexed_head_source=latest_index`."),
            new(
                "index_matches_workspace",
                "Workspace freshness check",
                "`true` means `status --check` compared the current workspace with the index and found no missing, changed, deleted, or stale tracked files.",
                "`false` means the index does not fully match the workspace, and absence means the workspace check was not requested or could not run.",
                "Run `cdidx status --check --json` for mismatch details, then run `cdidx index <projectPath>` to refresh the index."),
            new(
                "path_case_sensitive",
                "Filesystem case sensitivity",
                "`true` means the indexed workspace path comparison is case-sensitive; `false` means case-insensitive.",
                "the field is absent on legacy indexes that predate the workspace case-sensitivity stamp.",
                "Run `cdidx index <projectPath>` with a current cdidx binary to stamp filesystem case sensitivity."),
        ]).ToArray();
}
