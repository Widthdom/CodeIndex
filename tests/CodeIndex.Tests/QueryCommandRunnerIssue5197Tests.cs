using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunDeps_LargeCycleSeparatesBoundedNodeDisplayFromCompleteAnalysis_Issue5197()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_large_cycle_5197");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        const int nodeCount = QueryCommandRunner.DefaultDependencyCycleNodeLimit + 5;
        for (var i = 0; i < nodeCount; i++)
        {
            InsertFileWithSymbolsAndReferences(
                dbPath,
                $"src/Node{i:D2}.cs",
                [$"Node{i:D2}"],
                [$"Node{(i + 1) % nodeCount:D2}"]);
        }
        MarkDependencyGraphReady(dbPath);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--summary-only", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        var summary = Assert.Single(json.GetProperty("cycle_summaries").EnumerateArray());
        var largest = json.GetProperty("largest_component");

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.False(json.TryGetProperty("cycles", out _));
        Assert.True(json.GetProperty("analysis_complete").GetBoolean());
        Assert.True(json.GetProperty("display_truncated").GetBoolean());
        Assert.Equal("component_node_limit", json.GetProperty("display_truncation_reason").GetString());
        Assert.Equal("bounded_sample", json.GetProperty("node_materialization_mode").GetString());
        Assert.Equal(nodeCount, summary.GetProperty("node_count").GetInt32());
        Assert.Equal(nodeCount, summary.GetProperty("internal_edge_count").GetInt32());
        Assert.Equal(nodeCount, summary.GetProperty("reference_count").GetInt64());
        Assert.Equal(QueryCommandRunner.DefaultDependencyCycleNodeLimit, summary.GetProperty("nodes_returned").GetInt32());
        Assert.Equal(5, summary.GetProperty("nodes_omitted_count").GetInt32());
        Assert.Equal(nodeCount, largest.GetProperty("node_count").GetInt32());
        Assert.Equal("file", json.GetProperty("cycle_grouping_mode").GetString());
        Assert.False(json.GetProperty("cycle_grouping_applied").GetBoolean());
        Assert.Contains(
            "--all-cycle-nodes",
            json.GetProperty("next_step_flags").EnumerateArray().Select(static value => value.GetString()));

        var (expandedExitCode, expandedStdout, expandedStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--all-cycle-nodes", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));

        using var expandedDocument = ParseJsonOutput(expandedStdout);
        var expandedJson = expandedDocument.RootElement;
        var expandedCycle = Assert.Single(expandedJson.GetProperty("cycles").EnumerateArray());
        Assert.Equal(CommandExitCodes.Success, expandedExitCode);
        Assert.Equal(string.Empty, expandedStderr);
        Assert.False(expandedJson.GetProperty("display_truncated").GetBoolean());
        Assert.True(expandedJson.GetProperty("analysis_complete").GetBoolean());
        Assert.Equal("complete", expandedJson.GetProperty("node_materialization_mode").GetString());
        Assert.Equal(nodeCount, expandedCycle.GetProperty("nodes_returned").GetInt32());
        Assert.False(expandedCycle.GetProperty("nodes_truncated").GetBoolean());

        var (humanExitCode, humanStdout, humanStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--cycles", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, humanExitCode);
        Assert.Contains("(1 dependency cycles)", humanStderr);
        Assert.Contains("5 nodes omitted; rerun with --all-cycle-nodes", humanStdout);
        Assert.DoesNotContain("src/Node54.cs", humanStdout);

        var (expandedHumanExitCode, expandedHumanStdout, expandedHumanStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--cycles", "--all-cycle-nodes", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, expandedHumanExitCode);
        Assert.Contains("(1 dependency cycles)", expandedHumanStderr);
        Assert.Contains("src/Node54.cs", expandedHumanStdout);
        Assert.DoesNotContain("nodes omitted", expandedHumanStdout);

        var (jsonGraphExitCode, jsonGraphStdout, jsonGraphStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--cycles", "--format", "json-graph", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));
        using var jsonGraphDocument = ParseJsonOutput(jsonGraphStdout);
        var jsonGraph = jsonGraphDocument.RootElement;
        var graphNodes = jsonGraph.GetProperty("nodes").EnumerateArray().ToArray();
        var graphNodeIds = graphNodes
            .Select(static node => node.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(CommandExitCodes.Success, jsonGraphExitCode);
        Assert.Equal(string.Empty, jsonGraphStderr);
        Assert.Equal(QueryCommandRunner.DefaultDependencyCycleNodeLimit, graphNodes.Length);
        Assert.DoesNotContain("src/Node54.cs", graphNodeIds);
        Assert.All(jsonGraph.GetProperty("edges").EnumerateArray(), edge =>
        {
            Assert.Contains(edge.GetProperty("source").GetString(), graphNodeIds);
            Assert.Contains(edge.GetProperty("target").GetString(), graphNodeIds);
        });
        Assert.True(jsonGraph.GetProperty("display_truncated").GetBoolean());
        Assert.Equal(nodeCount, jsonGraph.GetProperty("returned_node_count").GetInt32());
        Assert.Equal(
            QueryCommandRunner.DefaultDependencyCycleNodeLimit,
            jsonGraph.GetProperty("returned_nodes_materialized").GetInt32());
        Assert.Equal(5, jsonGraph.GetProperty("returned_nodes_omitted_count").GetInt32());

        var (expandedGraphExitCode, expandedGraphStdout, expandedGraphStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--cycles", "--format", "json-graph", "--all-cycle-nodes", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));
        using var expandedGraphDocument = ParseJsonOutput(expandedGraphStdout);
        Assert.Equal(CommandExitCodes.Success, expandedGraphExitCode);
        Assert.Equal(string.Empty, expandedGraphStderr);
        Assert.Equal(nodeCount, expandedGraphDocument.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(nodeCount, expandedGraphDocument.RootElement.GetProperty("edges").GetArrayLength());
        Assert.False(expandedGraphDocument.RootElement.GetProperty("display_truncated").GetBoolean());

        foreach (var graphFormat in new[] { "dot", "graphml" })
        {
            var (graphExitCode, graphStdout, graphStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--cycles", "--format", graphFormat, "--limit", "1", "--lang", "csharp"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, graphExitCode);
            Assert.Contains("src/Node49.cs", graphStdout);
            Assert.DoesNotContain("src/Node54.cs", graphStdout);
            Assert.Contains("limited to 50 nodes per component", graphStderr);
            Assert.Contains("5 nodes are omitted across 1 returned component", graphStderr);
            Assert.Contains("--all-cycle-nodes", graphStderr);
        }

        var (budgetExitCode, _, budgetStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--cycles", "--all-cycle-nodes", "--graph-budget", "10", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, budgetExitCode);
        Assert.Contains("graph edge budget reached", budgetStderr);
        Assert.DoesNotContain("--all-cycle-nodes", budgetStderr);
    }

    [Fact]
    public void RunDeps_CycleSummaryIncludesEveryAdvertisedEvidenceDimension_Issue5197()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_cycle_evidence_5197");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFileWithSymbolsAndReferences(dbPath, "src/EvidenceA.cs", ["EvidenceA"], ["EvidenceB"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/EvidenceB.cs", ["EvidenceB"], ["EvidenceA"]);
        SetCycleReferenceResolution(dbPath, "src/EvidenceA.cs", "src/EvidenceB.cs", "unresolved");
        SetCycleReferenceResolution(dbPath, "src/EvidenceB.cs", "src/EvidenceA.cs", "resolved");
        MarkDependencyGraphReady(dbPath);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--summary-only", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        var evidence = Assert.Single(document.RootElement
            .GetProperty("cycle_summaries")
            .EnumerateArray())
            .GetProperty("retained_evidence");
        var origin = Assert.Single(evidence.GetProperty("by_origin").EnumerateArray());
        var targetKind = Assert.Single(evidence.GetProperty("by_target_kind").EnumerateArray());
        var suppressionReasons = evidence.GetProperty("by_suppression_reason")
            .EnumerateArray()
            .ToDictionary(
                static item => item.GetProperty("suppression_reason").GetString()!,
                static item => item.GetProperty("reference_count").GetInt64(),
                StringComparer.Ordinal);

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal("symbol_name_match", origin.GetProperty("origin").GetString());
        Assert.Equal(2, origin.GetProperty("reference_count").GetInt64());
        Assert.Equal("symbol", targetKind.GetProperty("target_kind").GetString());
        Assert.Equal(2, targetKind.GetProperty("reference_count").GetInt64());
        Assert.Equal(1, suppressionReasons["csharp_non_authoritative_qualified_call"]);
        Assert.Equal(1, suppressionReasons["unavailable"]);
    }

    [Fact]
    public void RunDeps_JsonGraphCursorPageReportsReturnedNodeOmissionSeparatelyFromLargestComponent_Issue5197()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_graph_page_5197");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertDependencyCycle(dbPath, "Largest", QueryCommandRunner.DefaultDependencyCycleNodeLimit + 5);
        InsertDependencyCycle(dbPath, "Paged", QueryCommandRunner.DefaultDependencyCycleNodeLimit + 2);
        MarkDependencyGraphReady(dbPath);

        var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--cycles", "--format", "json-graph", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));
        using var firstDocument = ParseJsonOutput(firstStdout);
        var cursor = firstDocument.RootElement.GetProperty("next_cursor").GetString();

        Assert.Equal(CommandExitCodes.Success, firstExitCode);
        Assert.Equal(string.Empty, firstStderr);
        Assert.False(string.IsNullOrEmpty(cursor));

        var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--cycles", "--format", "json-graph", "--limit", "1", "--cursor", cursor!, "--lang", "csharp"],
            _jsonOptions));
        using var secondDocument = ParseJsonOutput(secondStdout);
        var secondPage = secondDocument.RootElement;

        Assert.Equal(CommandExitCodes.Success, secondExitCode);
        Assert.Equal(string.Empty, secondStderr);
        Assert.Equal(
            QueryCommandRunner.DefaultDependencyCycleNodeLimit + 2,
            secondPage.GetProperty("returned_node_count").GetInt32());
        Assert.Equal(
            QueryCommandRunner.DefaultDependencyCycleNodeLimit,
            secondPage.GetProperty("returned_nodes_materialized").GetInt32());
        Assert.Equal(2, secondPage.GetProperty("returned_nodes_omitted_count").GetInt32());
        Assert.Equal(
            QueryCommandRunner.DefaultDependencyCycleNodeLimit,
            secondPage.GetProperty("nodes").GetArrayLength());
        Assert.Equal(
            5,
            secondPage.GetProperty("largest_component").GetProperty("nodes_omitted_count").GetInt32());
    }

    [Fact]
    public void RunDeps_CycleCursorRejectsEvidenceOnlyGraphChanges_Issue5197()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_cycle_evidence_cursor_5197");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertDependencyCycle(dbPath, "First", 2);
        InsertDependencyCycle(dbPath, "Second", 2);
        SetCycleReferenceResolution(dbPath, "src/First00.cs", "src/First01.cs", "resolved");
        MarkDependencyGraphReady(dbPath);

        var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--limit", "1", "--lang", "csharp"],
            _jsonOptions));
        using var firstDocument = ParseJsonOutput(firstStdout);
        var cursor = firstDocument.RootElement.GetProperty("next_cursor").GetString();

        Assert.Equal(CommandExitCodes.Success, firstExitCode);
        Assert.Equal(string.Empty, firstStderr);
        Assert.False(string.IsNullOrEmpty(cursor));

        SetCycleReferenceResolution(dbPath, "src/First00.cs", "src/First01.cs", "ambiguous");

        var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--limit", "1", "--cursor", cursor!, "--lang", "csharp"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, secondExitCode);
        Assert.Equal(string.Empty, secondStdout);
        Assert.Contains("cursor does not match", secondStderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunDeps_CSharpNoiseSuppressionRemovesOnlyNonAuthoritativeQualifiedCalls_Issue5197()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_csharp_cycle_noise_5197");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFileWithSymbolsAndReferences(dbPath, "src/FallbackA.cs", ["FallbackA"], ["FallbackB"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/FallbackB.cs", ["FallbackB"], ["FallbackA"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/ResolvedA.cs", ["ResolvedA"], ["ResolvedB"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/ResolvedB.cs", ["ResolvedB", "ResolvedB"], ["ResolvedA"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/ResolvedBDecoy.cs", ["ResolvedB"], ["ResolvedA"]);
        SetCycleReferenceResolution(dbPath, "src/FallbackA.cs", "src/FallbackB.cs", "unresolved");
        SetCycleReferenceResolution(dbPath, "src/FallbackB.cs", "src/FallbackA.cs", "resolved");
        SetCycleReferenceResolution(dbPath, "src/ResolvedA.cs", "src/ResolvedB.cs", "resolved");
        SetCycleReferenceResolution(dbPath, "src/ResolvedB.cs", "src/ResolvedA.cs", "resolved");
        SetCycleReferenceResolution(dbPath, "src/ResolvedBDecoy.cs", "src/ResolvedA.cs", "resolved");
        MarkDependencyGraphReady(dbPath);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--suppress-noise", "--limit", "10", "--lang", "csharp"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        var cycle = Assert.Single(json.GetProperty("cycles").EnumerateArray());
        var nodes = cycle.GetProperty("nodes").EnumerateArray().Select(static node => node.GetString()).ToArray();
        var resolutionBreakdown = cycle.GetProperty("retained_evidence").GetProperty("by_resolution_state");
        var resolution = Assert.Single(resolutionBreakdown.EnumerateArray());
        var reason = Assert.Single(json.GetProperty("symbol_filter").GetProperty("suppression_reasons").EnumerateArray());

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(["src/ResolvedA.cs", "src/ResolvedB.cs"], nodes);
        Assert.Equal("resolved", resolution.GetProperty("resolution_state").GetString());
        Assert.Equal(2, resolution.GetProperty("reference_count").GetInt64());
        Assert.True(cycle.GetProperty("retained_evidence").GetProperty("classification_complete").GetBoolean());
        Assert.Equal("csharp_non_authoritative_qualified_call", reason.GetProperty("reason").GetString());
        Assert.Equal(2, reason.GetProperty("edges_affected").GetInt32());
        Assert.Equal(2, reason.GetProperty("edges_removed").GetInt32());
        Assert.Equal(2, reason.GetProperty("references_removed").GetInt64());
    }

    [Fact]
    public void RunDeps_CSharpNoiseSuppressionKeepsOnlyResolvedGroupCandidateFiles_Issue5197()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_csharp_resolved_group_5197");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFileWithSymbolsAndReferences(dbPath, "src/Caller.cs", ["Caller"], ["Target"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/PartialDeclaration.cs", ["Target"], ["Caller"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/PartialImplementation.cs", ["Target"], ["Caller"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/Decoy.cs", ["Target"], ["Caller"]);
        SetCycleReferenceResolvedGroupCandidates(
            dbPath,
            "src/Caller.cs",
            ["src/PartialDeclaration.cs", "src/PartialImplementation.cs"]);
        SetCycleReferenceResolution(dbPath, "src/PartialDeclaration.cs", "src/Caller.cs", "resolved");
        SetCycleReferenceResolution(dbPath, "src/PartialImplementation.cs", "src/Caller.cs", "resolved");
        SetCycleReferenceResolution(dbPath, "src/Decoy.cs", "src/Caller.cs", "resolved");
        MarkDependencyGraphReady(dbPath);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--suppress-noise", "--limit", "10", "--lang", "csharp"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        var cycle = Assert.Single(json.GetProperty("cycles").EnumerateArray());
        var nodes = cycle.GetProperty("nodes").EnumerateArray().Select(static node => node.GetString()).ToArray();
        var resolutions = cycle
            .GetProperty("retained_evidence")
            .GetProperty("by_resolution_state")
            .EnumerateArray()
            .ToDictionary(
                static item => item.GetProperty("resolution_state").GetString()!,
                static item => item.GetProperty("reference_count").GetInt64(),
                StringComparer.Ordinal);
        var reason = Assert.Single(json.GetProperty("symbol_filter").GetProperty("suppression_reasons").EnumerateArray());

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(
            ["src/Caller.cs", "src/PartialDeclaration.cs", "src/PartialImplementation.cs"],
            nodes);
        Assert.Equal(2, resolutions["resolved"]);
        Assert.Equal(2, resolutions["resolved_group"]);
        Assert.Equal("csharp_non_authoritative_qualified_call", reason.GetProperty("reason").GetString());
        Assert.Equal(1, reason.GetProperty("edges_affected").GetInt32());
        Assert.Equal(1, reason.GetProperty("edges_removed").GetInt32());
        Assert.Equal(1, reason.GetProperty("references_removed").GetInt64());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    public void RunDeps_CSharpNoiseSuppressionFailsClosedWithoutCurrentIdentityContract_Issue5197(
        string? persistedContractVersion)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_deps_stale_identity_cycle_5197");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        InsertFileWithSymbolsAndReferences(dbPath, "src/StaleA.cs", ["StaleA"], ["StaleB"]);
        InsertFileWithSymbolsAndReferences(dbPath, "src/StaleB.cs", ["StaleB"], ["StaleA"]);
        SetCycleReferenceResolution(dbPath, "src/StaleA.cs", "src/StaleB.cs", "unresolved");
        SetCycleReferenceResolution(dbPath, "src/StaleB.cs", "src/StaleA.cs", "unresolved");
        MarkDependencyGraphReady(dbPath);
        SetReferenceIdentityContractVersion(dbPath, persistedContractVersion);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--db", dbPath, "--json", "--cycles", "--suppress-noise", "--limit", "10", "--lang", "csharp"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        var cycle = Assert.Single(json.GetProperty("cycles").EnumerateArray());
        var nodes = cycle.GetProperty("nodes").EnumerateArray().Select(static node => node.GetString()).ToArray();
        var resolution = Assert.Single(cycle
            .GetProperty("retained_evidence")
            .GetProperty("by_resolution_state")
            .EnumerateArray());
        var symbolFilter = json.GetProperty("symbol_filter");

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(["src/StaleA.cs", "src/StaleB.cs"], nodes);
        Assert.Equal("unavailable", resolution.GetProperty("resolution_state").GetString());
        Assert.Equal(2, resolution.GetProperty("reference_count").GetInt64());
        Assert.Equal(2, symbolFilter.GetProperty("references_before").GetInt64());
        Assert.Equal(2, symbolFilter.GetProperty("references_after").GetInt64());
        Assert.False(symbolFilter.TryGetProperty("suppression_reasons", out _));
    }

    private static void SetReferenceIdentityContractVersion(string dbPath, string? persistedContractVersion)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.SetMeta(DbContext.ReferenceIdentityContractVersionMetaKey, persistedContractVersion);
    }

    private static void InsertDependencyCycle(string dbPath, string prefix, int nodeCount)
    {
        for (var index = 0; index < nodeCount; index++)
        {
            InsertFileWithSymbolsAndReferences(
                dbPath,
                $"src/{prefix}{index:D2}.cs",
                [$"{prefix}{index:D2}"],
                [$"{prefix}{(index + 1) % nodeCount:D2}"]);
        }
    }

    private static void SetCycleReferenceResolution(
        string dbPath,
        string sourcePath,
        string targetPath,
        string resolutionState)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE symbol_references
            SET reference_kind = 'call',
                target_qualifier = 'Receiver',
                resolution_state = $resolutionState,
                resolution_candidate_count = 1,
                target_symbol_id = CASE WHEN $resolutionState = 'resolved' THEN (
                    SELECT s.id
                    FROM symbols s
                    JOIN files f ON f.id = s.file_id
                    WHERE f.path = $targetPath
                      AND s.name = symbol_references.symbol_name
                    ORDER BY s.id
                    LIMIT 1
                ) ELSE NULL END
            WHERE file_id = (SELECT id FROM files WHERE path = $sourcePath)
            """;
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$targetPath", targetPath);
        command.Parameters.AddWithValue("$resolutionState", resolutionState);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void SetCycleReferenceResolvedGroupCandidates(
        string dbPath,
        string sourcePath,
        IReadOnlyList<string> targetPaths)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE symbol_references
            SET reference_kind = 'call',
                target_qualifier = 'Receiver',
                resolution_state = 'resolved_group',
                resolution_candidate_count = $candidateCount,
                target_symbol_id = NULL,
                target_symbol_key = 'family:issue5197'
            WHERE file_id = (SELECT id FROM files WHERE path = $sourcePath)
            """;
        update.Parameters.AddWithValue("$candidateCount", targetPaths.Count);
        update.Parameters.AddWithValue("$sourcePath", sourcePath);
        Assert.Equal(1, update.ExecuteNonQuery());

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM symbol_reference_candidates
            WHERE reference_id = (
                SELECT r.id
                FROM symbol_references r
                JOIN files f ON f.id = r.file_id
                WHERE f.path = $sourcePath
            )
            """;
        delete.Parameters.AddWithValue("$sourcePath", sourcePath);
        delete.ExecuteNonQuery();

        foreach (var targetPath in targetPaths)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
                SELECT r.id, s.id, 0
                FROM symbol_references r
                JOIN files source_file ON source_file.id = r.file_id
                JOIN files target_file ON target_file.path = $targetPath
                JOIN symbols s ON s.file_id = target_file.id AND s.name = r.symbol_name
                WHERE source_file.path = $sourcePath
                """;
            insert.Parameters.AddWithValue("$sourcePath", sourcePath);
            insert.Parameters.AddWithValue("$targetPath", targetPath);
            Assert.Equal(1, insert.ExecuteNonQuery());
        }

        transaction.Commit();
    }
}
