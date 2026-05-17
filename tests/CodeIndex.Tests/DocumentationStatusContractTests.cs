namespace CodeIndex.Tests;

public class DocumentationStatusContractTests
{
    private static readonly string[] StatusContractFields =
    [
        "fold_ready",
        "fold_ready_reason",
        "graph_table_available",
        "issues_table_available",
        "sql_graph_contract_ready",
        "sql_graph_contract_degraded_reason",
        "hotspot_family_ready",
        "hotspot_family_degraded_reason",
        "csharp_symbol_name_ready",
        "csharp_metadata_target_ready",
        "indexed_head_commit",
        "worktree_head_changed",
        "indexed_head_sha",
        "indexed_head_branch",
        "indexed_head_timestamp",
        "commits_ahead_of_indexed_head",
        "index_writer_version",
        "index_newer_than_reader",
        "index_newer_than_reader_reason",
        "unknown_extension_file_count",
        "path_case_sensitive",
        "stale_after_seconds",
        "index_age_seconds",
        "degraded_reason",
        "recommended_action",
        "alternative_action",
    ];

    [Theory]
    [InlineData("README.md")]
    [InlineData("DEVELOPER_GUIDE.md")]
    [InlineData("AGENT_GUIDE.md")]
    public void StatusContractDocs_MentionEveryTrustField(string relativePath)
    {
        var repoRoot = GetRepositoryRoot();
        var docPath = Path.Combine(repoRoot, relativePath);
        var content = File.ReadAllText(docPath);

        foreach (var field in StatusContractFields)
        {
            Assert.Contains(field, content, StringComparison.Ordinal);
        }
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENT_GUIDE.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
