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
                "unknown_extension_file_count",
                "Unknown extension inventory",
                "`0` means the last successful full scan found no non-empty unknown extensions; positive values summarize skipped files with extensions that no language recognized.",
                "the field is absent in legacy indexes or before a current scanner stamps unknown-extension inventory metadata.",
                "Review `unknown_extension_files` and `unknown_extension_groups` in `cdidx status --json`, then update ignores or language support before rebuilding."),
            new(
                "head_freshness",
                "Compact HEAD freshness summary",
                "`state=fresh` means `status --check` proved the index matches the workspace; without `--check`, `state=head_current` means only the runtime HEAD matched `indexed_head` (see `indexed_head_source`).",
                "`state=stale`, `state=head_changed`, `state=check_unavailable`, or `state=unchecked` means consumers should inspect `state_reason`, `indexed_head_source`, and the nested head fields before trusting freshness-sensitive results.",
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
