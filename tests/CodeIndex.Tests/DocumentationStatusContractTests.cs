namespace CodeIndex.Tests;

public class DocumentationStatusContractTests
{
    private static readonly string[] StatusContractFields =
    [
        "fold_ready",
        "fold_ready_reason",
        "graph_table_available",
        "graph_data_current",
        "reference_extraction_limits",
        "reference_graph_complete",
        "reference_graph_incomplete_reasons",
        "reference_extraction_cap_hits",
        "issues_table_available",
        "file_issues_data_current",
        "migration_in_progress",
        "sql_graph_contract_ready",
        "sql_graph_contract_degraded_reason",
        "hotspot_family_ready",
        "hotspot_family_degraded_reason",
        "language_readiness",
        "csharp_symbol_name_ready",
        "csharp_metadata_target_ready",
        "csharp_metadata_target_degraded_reason",
        "indexed_head_commit",
        "worktree_head_changed",
        "indexed_head_sha",
        "indexed_head_branch",
        "indexed_head_timestamp",
        "commits_ahead_of_indexed_head",
        "head_freshness",
        "index_writer_version",
        "index_newer_than_reader",
        "index_newer_than_reader_reason",
        "unknown_extension_file_count",
        "unknown_extension_files",
        "unknown_extension_files_truncated",
        "unknown_extension_file_path_limit",
        "unknown_extension_extension_counts",
        "unknown_extension_category_counts",
        "unknown_extension_groups",
        "extractors",
        "hooks",
        "hook_diagnostics",
        "trust_overrides",
        "path_case_sensitive",
        "mac_profile",
        "db_size_bytes",
        "wal_size_bytes",
        "db_pragma_settings",
        "prepared_command_cache",
        "maintenance_guidance",
        "process",
        "last_index_run",
        "last_workspace_freshened_at",
        "stale_after_seconds",
        "index_age_seconds",
        "last_failed_or_partial_index_run",
        "degraded_reason",
        "recommended_action",
        "alternative_action",
        "repair_commands",
        "mcp_session",
        "mcp_session.metrics",
        "queue_capacity",
        "queue_depth",
        "queued_event_count",
        "written_event_count",
        "dropped_event_count",
        "queue_full_drop_count",
        "serialization_failure_count",
        "write_failure_count",
        "rotation_failure_count",
        "batch_flush_count",
        "consecutive_failure_count",
        "recovery_count",
        "next_retry_at",
        "last_recovery_at",
        "last_failure",
        "queued_record_count",
        "written_record_count",
        "mcp.rate_limit.bucket_limit",
        "mcp.rate_limit.bucket_limit_rejection_count",
    ];

    [Theory]
    [InlineData("README.md")]
    [InlineData("DEVELOPER_GUIDE.md")]
    [InlineData("AGENT_GUIDE.md")]
    public void StatusContractDocs_MentionEveryTrustField(string relativePath)
    {
        var content = RepositoryTestPaths.ReadText(relativePath);

        foreach (var field in StatusContractFields)
        {
            Assert.Contains(field, content, StringComparison.Ordinal);
        }
    }

}
