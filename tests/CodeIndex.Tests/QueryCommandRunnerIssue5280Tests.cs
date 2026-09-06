using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunDeps_FiltersMixedEvidenceBeforeOrdinaryAndReverseAggregation_Issue5280()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_evidence_filter_5280");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFileWithSymbolsAndReferences(dbPath, "src/Caller.java", ["Caller"], ["Target", "Target", "Target"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/Target.java", ["Target"], []);
        SetDependencyLanguage(dbPath, "java");
        SetDependencyEvidence(dbPath, "src/Caller.java",
            [("call", "resolved"), ("type_reference", "unresolved"), ("unsubscribe", "resolved")]);
        MarkDependencyGraphReady(dbPath);
        SetReferenceIdentityContractVersion(dbPath, DbContext.ReferenceIdentityContractVersion.ToString());
        Assert.Equal(1, CountDependencyEvidence(dbPath, "type_reference", "unresolved"));

        foreach (var reverse in new[] { false, true })
        {
            var args = new List<string>
            {
                "--db", dbPath, "--json", "--lang", "java",
                "--resolution-state", " Resolved ", "--reference-kind", "CALL",
            };
            if (reverse)
                args.Add("--reverse");

            var (exitCode, stdout, stderr) = CaptureConsole(
                () => QueryCommandRunner.RunDeps(args.ToArray(), _jsonOptions));
            using var document = ParseJsonOutput(stdout);
            var edge = Assert.Single(document.RootElement.GetProperty("edges").EnumerateArray());
            var evidence = Assert.Single(edge.GetProperty("evidence").EnumerateArray());
            var filter = document.RootElement
                .GetProperty("query_context")
                .GetProperty("dependency_evidence_filter");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, edge.GetProperty("reference_count").GetInt32());
            Assert.Equal("resolved", evidence.GetProperty("resolution_state").GetString());
            Assert.Equal("call", evidence.GetProperty("reference_kind").GetString());
            Assert.Equal("resolved", filter.GetProperty("resolution_states")[0].GetString());
            Assert.Equal("call", filter.GetProperty("reference_kinds")[0].GetString());
            Assert.Equal("aggregation_ranking_and_graph_budget", filter.GetProperty("applied_before").GetString());
            Assert.False(filter.GetProperty("whole_program_completeness_implied").GetBoolean());
        }

        var subscribeResult = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--lang", "java", "--reference-kind", "subscribe"],
            _jsonOptions));
        using var subscribeDocument = ParseJsonOutput(subscribeResult.Stdout);
        var subscribeEdge = Assert.Single(subscribeDocument.RootElement.GetProperty("edges").EnumerateArray());
        var subscribeEvidence = Assert.Single(subscribeEdge.GetProperty("evidence").EnumerateArray());
        Assert.Equal(CommandExitCodes.Success, subscribeResult.Result);
        Assert.Equal(1, subscribeEdge.GetProperty("reference_count").GetInt32());
        Assert.Equal("unsubscribe", subscribeEvidence.GetProperty("reference_kind").GetString());
    }

    [Fact]
    public void RunDeps_FiltersCycleEvidenceBeforeBudgetAndBindsCursorToFilters_Issue5280()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_cycle_filter_5280");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFilteredCycle(dbPath, "CycleA", "CycleB");
        InsertFilteredCycle(dbPath, "CycleC", "CycleD");
        for (var index = 0; index < 4; index++)
        {
            InsertFileWithSymbolsAndReferences(dbPath, $"src/00Decoy{index}.cs", [$"Decoy{index}"], ["DecoyTarget"]);
            SetDependencyEvidence(dbPath, $"src/00Decoy{index}.cs", [("call", "resolved")]);
        }
        InsertFileWithSymbolsAndReferences(dbPath, "src/00DecoyTarget.cs", ["DecoyTarget"], []);
        SetDependencyEvidence(dbPath, "src/CycleA.cs", [("call", "resolved"), ("type_reference", "unresolved")]);
        SetDependencyEvidence(dbPath, "src/CycleB.cs", [("call", "resolved"), ("type_reference", "unresolved")]);
        SetDependencyEvidence(dbPath, "src/CycleC.cs", [("call", "resolved"), ("type_reference", "unresolved")]);
        SetDependencyEvidence(dbPath, "src/CycleD.cs", [("call", "resolved"), ("type_reference", "unresolved")]);
        MarkDependencyGraphReady(dbPath);
        SetReferenceIdentityContractVersion(dbPath, DbContext.ReferenceIdentityContractVersion.ToString());
        Assert.Equal(4, CountDependencyEvidence(dbPath, "type_reference", "unresolved"));

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--summary-only", "--limit", "1", "--graph-budget", "4",
                "--resolution-state", "unresolved", "--reference-kind", "type_reference", "--lang", "csharp"],
            _jsonOptions));
        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        Assert.True(json.GetProperty("cycle_summaries").GetArrayLength() == 1, stdout);
        var summary = json.GetProperty("cycle_summaries")[0];
        var cursor = json.GetProperty("next_cursor").GetString();
        var evidence = Assert.Single(summary.GetProperty("retained_evidence").GetProperty("by_reference_kind").EnumerateArray());

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.True(json.GetProperty("analysis_complete").GetBoolean());
        Assert.Equal(2, json.GetProperty("total_cycle_count").GetInt32());
        Assert.Equal(2, summary.GetProperty("reference_count").GetInt64());
        Assert.Equal("type_reference", evidence.GetProperty("reference_kind").GetString());
        Assert.False(string.IsNullOrEmpty(cursor));

        var (mismatchExitCode, mismatchStdout, mismatchStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--summary-only", "--limit", "1", "--graph-budget", "4",
                "--cursor", cursor!, "--resolution-state", "resolved", "--reference-kind", "call", "--lang", "csharp"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, mismatchExitCode);
        Assert.Equal(string.Empty, mismatchStdout);
        Assert.Contains("cursor does not match", mismatchStderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunDeps_MissingAndStaleResolutionMetadataMatchUnavailableOnly_Issue5280()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_unavailable_filter_5280");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFilteredCycle(dbPath, "StaleA", "StaleB");
        MarkDependencyGraphReady(dbPath);

        SetReferenceIdentityContractVersion(dbPath, DbContext.ReferenceIdentityContractVersion.ToString());
        ClearDependencyResolutionMetadata(dbPath);
        var missing = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--resolution-state", "unavailable", "--lang", "csharp"],
            _jsonOptions));
        using var missingDocument = ParseJsonOutput(missing.Stdout);
        Assert.Equal(CommandExitCodes.Success, missing.Result);
        Assert.Single(missingDocument.RootElement.GetProperty("cycles").EnumerateArray());

        SetReferenceIdentityContractVersion(dbPath, null);

        var unavailable = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--resolution-state", "unavailable", "--lang", "csharp"],
            _jsonOptions));
        using var unavailableDocument = ParseJsonOutput(unavailable.Stdout);
        Assert.Equal(CommandExitCodes.Success, unavailable.Result);
        Assert.Single(unavailableDocument.RootElement.GetProperty("cycles").EnumerateArray());

        var resolved = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--resolution-state", "resolved", "--lang", "csharp"],
            _jsonOptions));
        using var resolvedDocument = ParseJsonOutput(resolved.Stdout);
        Assert.Equal(CommandExitCodes.Success, resolved.Result);
        Assert.Equal(0, resolvedDocument.RootElement.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("--resolution-state", "missing")]
    [InlineData("--reference-kind", "missing")]
    public void RunDeps_RejectsUnknownEvidenceFilterValues_Issue5280(string option, string value)
    {
        var result = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", "definitely-missing.db", "--json", option, value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, result.Result);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("Unsupported dependency", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLite", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunDeps_RejectsEvidenceFiltersAboveBoundBeforeOpeningDatabase_Issue5280()
    {
        var values = string.Join(',', Enumerable.Repeat("resolved", DependencyEvidenceFilter.MaxValues + 1));
        var result = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", "definitely-missing.db", "--json", "--resolution-state", values],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, result.Result);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains($"at most {DependencyEvidenceFilter.MaxValues} values", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLite", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static void InsertFilteredCycle(string dbPath, string first, string second)
    {
        InsertFileWithSymbolsAndReferences(dbPath, $"src/{first}.cs", [first], [second, second]);
        InsertFileWithSymbolsAndReferences(dbPath, $"src/{second}.cs", [second], [first, first]);
        SetDependencyEvidence(dbPath, $"src/{first}.cs", [("call", "resolved"), ("type_reference", "unresolved")]);
        SetDependencyEvidence(dbPath, $"src/{second}.cs", [("call", "resolved"), ("type_reference", "unresolved")]);
    }

    private static void SetDependencyLanguage(string dbPath, string language)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE files SET lang = $language";
        command.Parameters.AddWithValue("$language", language);
        command.ExecuteNonQuery();
    }

    private static void SetDependencyEvidence(
        string dbPath,
        string sourcePath,
        IReadOnlyList<(string Kind, string Resolution)> evidence)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT r.id
            FROM symbol_references r
            JOIN files f ON f.id = r.file_id
            WHERE f.path = $path
            ORDER BY r.id
            """;
        select.Parameters.AddWithValue("$path", sourcePath);
        var ids = new List<long>();
        using (var reader = select.ExecuteReader())
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        Assert.Equal(evidence.Count, ids.Count);

        for (var index = 0; index < ids.Count; index++)
        {
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE symbol_references
                SET reference_kind = $kind,
                    resolution_state = $resolution,
                    resolution_candidate_count = CASE WHEN $resolution = 'unresolved' THEN 0 ELSE 1 END
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$kind", evidence[index].Kind);
            update.Parameters.AddWithValue("$resolution", evidence[index].Resolution);
            update.Parameters.AddWithValue("$id", ids[index]);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
    }

    private static int CountDependencyEvidence(string dbPath, string kind, string resolution)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE reference_kind = $kind AND resolution_state = $resolution";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$resolution", resolution);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void ClearDependencyResolutionMetadata(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE symbol_references
            SET resolution_state = NULL,
                resolution_candidate_count = 0
            """;
        Assert.True(command.ExecuteNonQuery() > 0);
    }
}
