using System.Reflection;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunImpact_MissingDepthValueShowsPerFlagHint_Issue1507()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(["QueryCommandRunner", "--depth"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: --depth requires a value.", stderr);
        Assert.Contains("Hint: deprecated alias", stderr);
        Assert.Contains("--max-hops 5", stderr);
    }

    [Fact]
    public void RunImpact_OutOfRangeDepthUpperBound_ReturnsUsageError_Issue1700()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
            ["Target", "--depth", "999999999"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--depth must be less than or equal to 64", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine("impact")}", stderr);
        Assert.DoesNotContain("database not found", stderr);
    }

    [Fact]
    public void GetTransitiveCallers_MaxDepthBoundaryProbeBudgetTerminatesStably_Issue3820()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_impact_boundary_probe_budget");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            var writer = new DbWriter(db.Connection);
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "src/Dense.cs",
                Lang = "csharp",
                Size = 1,
                Lines = 1,
                Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });

            var references = new List<ReferenceRecord>();
            for (var i = 0; i < ImpactBoundaryCallerProbeSourceCount(); i++)
            {
                references.Add(new ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = "Root",
                    ReferenceKind = "call",
                    Line = i + 1,
                    Column = 1,
                    Context = $"Caller{i}();",
                    ContainerKind = "function",
                    ContainerName = $"Caller{i}",
                });

                if (i == 0)
                    continue;

                references.Add(new ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = "Caller0",
                    ReferenceKind = "call",
                    Line = i + 1,
                    Column = 1,
                    Context = $"Caller{i}();",
                    ContainerKind = "function",
                    ContainerName = $"Caller{i}",
                });
            }

            writer.InsertReferences(references);
            writer.MarkGraphReady();
            using var reader = new DbReader(db.Connection);
            var visited = Enumerable.Range(1, ImpactBoundaryCallerProbeSourceCount() - 1)
                .Select(i => $"src/Dense.cs:Caller{i}:call")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var method = typeof(DbReader).GetMethod("InspectBoundaryCallers", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var inspection = method!.Invoke(reader,
            [
                "Caller0",
                "Root",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                visited,
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                new List<ImpactCycleResult>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                "csharp",
                null,
                null,
                false,
            ]);
            Assert.NotNull(inspection);
            var type = inspection!.GetType();

            Assert.True((bool)type.GetProperty("HasUnvisitedCaller")!.GetValue(inspection)!);
            Assert.True((bool)type.GetProperty("ProbeBudgetHit")!.GetValue(inspection)!);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }

        static int ImpactBoundaryCallerProbeSourceCount() => DbReader.ImpactBoundaryCallerProbeBudget + 100;
    }

    [Fact]
    public void RunImpact_CountOnlyJson_StaleSqlGraphContractIncludesDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_impact_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["fn_Target", "--db", dbPath, "--json", "--lang", "sql", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.GetProperty("authoritative_count").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_ZeroJson_EmitsEnvelopeAndFreshness()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_zero_json_impact");
        try
        {
            var dbPath = CreateIndexedDbWithSingleFile(projectRoot, markGraphReady: true);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["DefinitelyMissingSymbol", "--db", dbPath, "--json", "--max-hops", "3"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertZeroResultPayload(json, "callers");
            Assert.Equal("DefinitelyMissingSymbol", json.GetProperty("query").GetString());
            Assert.Equal(3, json.GetProperty("max_hops").GetInt32());
            Assert.Equal(3, json.GetProperty("max_depth").GetInt32());
            Assert.False(json.GetProperty("truncated").GetBoolean());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.Equal("no_matching_definition", json.GetProperty("zero_result_reason").GetString());
            Assert.Equal("resolution", json.GetProperty("suggestion_type").GetString());
            Assert.Equal("definition_not_found", Assert.Single(json.GetProperty("impact_failure_chain").EnumerateArray()).GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_ZeroDepthJson_ResolvesSymbolWithoutTraversingCallers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_zero_depth_impact");
        try
        {
            var dbPath = CreateIndexedDbWithSingleFile(projectRoot, markGraphReady: true);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["HandleRequest", "--db", dbPath, "--json", "--max-hops", "0"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("HandleRequest", json.GetProperty("query").GetString());
            Assert.Equal(0, json.GetProperty("max_hops").GetInt32());
            Assert.Equal(0, json.GetProperty("max_depth").GetInt32());
            Assert.Equal(0, json.GetProperty("actual_depth").GetInt32());
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("definition_count").GetInt32());
            Assert.Equal("depth_requested_zero", json.GetProperty("zero_result_reason").GetString());
            Assert.Equal("precondition", json.GetProperty("suggestion_type").GetString());
            Assert.Equal("depth_requested_zero", Assert.Single(json.GetProperty("impact_failure_chain").EnumerateArray()).GetString());
            Assert.Equal("Use `cdidx impact <symbol> --max-hops 1` or higher to traverse callers.", json.GetProperty("suggestion").GetString());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_StrictReturnsFeatureUnavailableForResolutionFailure()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_impact_strict_resolution_failure");
        try
        {
            var dbPath = CreateIndexedDbWithSingleFile(projectRoot, markGraphReady: true);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["DefinitelyMissingSymbol", "--db", dbPath, "--json", "--strict"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.FeatureUnavailable, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("no_matching_definition", json.GetProperty("zero_result_reason").GetString());
            Assert.Equal("definition_not_found", Assert.Single(json.GetProperty("impact_failure_chain").EnumerateArray()).GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_ClassSymbolJsonReturnsHeuristicFileDependencyHints()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_class_fallback");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FolderDiffService.cs", "csharp",
                """
                public class FolderDiffService
                {
                    public void ExecuteFolderDiffAsync() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Run(FolderDiffService service)
                    {
                        service.ExecuteFolderDiffAsync();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FolderDiffService", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("file_dependency_hints", json.GetProperty("impact_mode").GetString());
            Assert.True(json.GetProperty("heuristic").GetBoolean());
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("confirmed_count").GetInt32());
            Assert.Equal(0, json.GetProperty("confirmed_file_count").GetInt32());
            Assert.Equal(1, json.GetProperty("hint_count").GetInt32());
            Assert.Equal(1, json.GetProperty("hint_file_count").GetInt32());
            Assert.False(json.GetProperty("has_multiple_definitions").GetBoolean());
            Assert.True(json.GetProperty("has_class_like_definitions").GetBoolean());
            Assert.Equal("src/App.cs", json.GetProperty("file_impacts")[0].GetProperty("source_path").GetString());
            Assert.Equal("src/FolderDiffService.cs", json.GetProperty("file_impacts")[0].GetProperty("target_path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_ClassAndNamespaceWithSameNameJsonStillReturnsHeuristicHints()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_namespace_sibling");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FooService.cs", "csharp",
                """
                namespace FooService;

                public class FooService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Boot(FooService service)
                    {
                        service.Run();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FooService", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("file_dependency_hints", json.GetProperty("impact_mode").GetString());
            Assert.True(json.GetProperty("heuristic").GetBoolean());
            Assert.True(json.GetProperty("has_multiple_definitions").GetBoolean());
            Assert.False(json.GetProperty("has_multiple_definition_files").GetBoolean());
            Assert.Equal(2, json.GetProperty("definition_count").GetInt32());
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("confirmed_count").GetInt32());
            Assert.Equal(1, json.GetProperty("hint_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_HeuristicHintsCountOnlyJsonUsesVisibleResultCount()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_hint_count_only");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FolderDiffService.cs", "csharp",
                """
                public class FolderDiffService
                {
                    public void ExecuteFolderDiffAsync() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Run(FolderDiffService service)
                    {
                        service.ExecuteFolderDiffAsync();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FolderDiffService", "--db", dbPath, "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("file_dependency_hints", json.GetProperty("impact_mode").GetString());
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
            Assert.Equal(0, json.GetProperty("confirmed_count").GetInt32());
            Assert.Equal(0, json.GetProperty("confirmed_file_count").GetInt32());
            Assert.Equal(1, json.GetProperty("hint_count").GetInt32());
            Assert.Equal(1, json.GetProperty("hint_file_count").GetInt32());
            Assert.False(json.GetProperty("degraded").GetBoolean());
            Assert.True(json.GetProperty("authoritative_count").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_CountOnlyJson_UserLimitTruncationIsNonAuthoritative_Issue3566()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_impact_count_truncated_authority_3566");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/lib.py", "python",
                """
                def target():
                    return 0
                """);
            for (int i = 0; i < 6; i++)
            {
                TestProjectHelper.InsertIndexedFile(dbPath, $"src/caller_{i:D2}.py", "python",
                    $$"""
                    def caller_{{i:D2}}():
                        return target()
                    """);
            }
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["target", "--db", dbPath, "--json", "--count", "--limit", "2"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.Equal(2, json.GetProperty("files").GetInt32());
            Assert.Equal(2, json.GetProperty("file_count").GetInt32());
            Assert.True(json.GetProperty("truncated").GetBoolean());
            Assert.Equal("user_limit", json.GetProperty("truncated_reason").GetString());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.GetProperty("authoritative_count").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_UserLimitTruncation_EmitsTruncatedReasonInJson()
    {
        // #1533: when impact truncates because the user-supplied --limit was reached,
        // the JSON payload exposes truncated_reason="user_limit" so AI/MCP consumers
        // can offer the correct retry advice (raise --limit).
        // #1533: --limit による打ち切り時、JSON に truncated_reason="user_limit" を含めて
        // AI/MCP クライアントが「--limit を上げる」適切な再試行案内を出せるようにする。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_user_limit_reason");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/lib.py", "python",
                """
                def target():
                    return 0
                """);
            for (int i = 0; i < 6; i++)
            {
                TestProjectHelper.InsertIndexedFile(dbPath, $"src/caller_{i:D2}.py", "python",
                    $$"""
                    def caller_{{i:D2}}():
                        return target()
                    """);
            }
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["target", "--db", dbPath, "--json", "--limit", "2"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("truncated").GetBoolean());
            Assert.Equal("user_limit", json.GetProperty("truncated_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_NotTruncated_OmitsTruncatedReasonFromJson()
    {
        // #1533: when truncated=false the truncated_reason field is omitted, so
        // schema consumers can rely on its presence to mean an actionable truncation.
        // #1533: truncated=false のときは truncated_reason フィールドを省略し、
        // スキーマ利用側が「フィールドが存在する＝対応すべき打ち切り」と判断できるようにする。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_no_truncate_reason");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/lib.py", "python",
                """
                def target():
                    return 0
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/single_caller.py", "python",
                """
                def caller():
                    return target()
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["target", "--db", dbPath, "--json", "--limit", "10"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("truncated").GetBoolean());
            Assert.False(json.TryGetProperty("truncated_reason", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_CycleJsonEmitsTerminationFields()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_cycle_reason");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/impact_cycle.cs", "csharp",
                """
                public static class ImpactCycle
                {
                    public static void A() { B(); }
                    public static void B() { C(); }
                    public static void C() { A(); }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["C", "--db", dbPath, "--json", "--limit", "20"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("truncated").GetBoolean());
            Assert.Equal("cycle_detected", json.GetProperty("termination_reason").GetString());
            Assert.True(json.GetProperty("cycle_detected").GetBoolean());
            var cycle = Assert.Single(json.GetProperty("cycles").EnumerateArray());
            Assert.Equal(new[] { "A", "B", "C" }, cycle.GetProperty("members").EnumerateArray().Select(member => member.GetString()).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_FoldEquivalentClassDefinitionsJsonReportAmbiguity()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_fold_siblings");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FooService.cs", "csharp",
                """
                public class FooService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FullwidthFooService.cs", "csharp",
                """
                public class ＦｏｏＳｅｒｖｉｃｅ
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Boot(FooService service)
                    {
                        service.Run();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FooService", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.True(json.GetProperty("has_multiple_definitions").GetBoolean());
            Assert.Equal(2, json.GetProperty("definition_count").GetInt32());
            Assert.Equal("multiple_definition_files", json.GetProperty("zero_result_reason").GetString());
            Assert.Equal(0, json.GetProperty("hint_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_PartialClassJsonReturnsResolutionHintPayload()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_partial_hint");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Worker.Part1.cs", "csharp",
                """
                public partial class Worker
                {
                    public void Start() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Worker.Part2.cs", "csharp",
                """
                public partial class Worker
                {
                    public void Stop() { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Worker", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("has_multiple_definitions").GetBoolean());
            Assert.True(json.GetProperty("has_multiple_definition_files").GetBoolean());
            Assert.Equal("multiple_definition_files", json.GetProperty("zero_result_reason").GetString());
            Assert.Contains("deps --path <definition-path> --reverse", json.GetProperty("suggestion").GetString());
            Assert.Equal(2, json.GetProperty("definition_file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_ClassCollisionWithoutTypeEvidenceReturnsNoHints()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FooService.cs", "csharp",
                """
                public class FooService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/BarService.cs", "csharp",
                """
                public class BarService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Boot(BarService service)
                    {
                        service.Run();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FooService", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.False(json.GetProperty("heuristic").GetBoolean());
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("hint_count").GetInt32());
            Assert.Equal(0, json.GetProperty("file_impacts").GetArrayLength());
            Assert.Equal("class_symbol_no_symbol_callers", json.GetProperty("zero_result_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_CommentOnlyTypeMentionDoesNotProduceHeuristicHints()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_comment_only_type_name");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FooService.cs", "csharp",
                """
                public class FooService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/OtherService.cs", "csharp",
                """
                public class OtherService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Boot(OtherService service)
                    {
                        service.Run(); // TODO: maybe replace with FooService later
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FooService", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("hint_count").GetInt32());
            Assert.Equal("class_symbol_no_symbol_callers", json.GetProperty("zero_result_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_StringLiteralTypeMentionDoesNotProduceHeuristicHints()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_string_only_type_name");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FooService.cs", "csharp",
                """
                public class FooService
                {
                    public void Execute() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Worker.cs", "csharp",
                """
                public class Worker
                {
                    public void Execute() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Boot(Worker worker)
                    {
                        var label = "FooService";
                        worker.Execute();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FooService", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.Equal(0, json.GetProperty("hint_count").GetInt32());
            Assert.Equal("class_symbol_no_symbol_callers", json.GetProperty("zero_result_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_NamespaceJsonDoesNotFallbackToFileDependencies()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_namespace");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Services.cs", "csharp",
                """
                namespace Acme;

                public class FooService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                namespace Acme;

                public class App
                {
                    public void Boot(FooService service)
                    {
                        service.Run();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Acme", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal("non_callable_symbol_kind", json.GetProperty("zero_result_reason").GetString());
            Assert.Equal(0, json.GetProperty("file_impacts").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_ImportOnlyQueryJsonReportsNonCallableSymbolKind()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_import_only");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.py", "python",
                """
                import requests
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["requests", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.Equal(1, json.GetProperty("definition_count").GetInt32());
            Assert.Equal("non_callable_symbol_kind", json.GetProperty("zero_result_reason").GetString());
            Assert.Contains("definition <symbol>", json.GetProperty("suggestion").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_ZeroResultJsonPayloadRemainsStableAcrossRepeatedTempProjects()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            RunImpactPartialClassZeroResultIteration(iteration);
            RunImpactImportOnlyZeroResultIteration(iteration);
        }
    }

    [Fact]
    public void RunImpact_UnicodeTypeEvidenceStillReturnsHeuristicHints()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_unicode_type_evidence");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/ＦｏｏＳｅｒｖｉｃｅ.cs", "csharp",
                """
                public class ＦｏｏＳｅｒｖｉｃｅ
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Boot(ＦｏｏＳｅｒｖｉｃｅ service)
                    {
                        service.Run();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["ＦｏｏＳｅｒｖｉｃｅ", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("file_dependency_hints", json.GetProperty("impact_mode").GetString());
            Assert.Equal(1, json.GetProperty("hint_count").GetInt32());
            Assert.Equal("file_heuristic", json.GetProperty("file_impacts")[0].GetProperty("result_kind").GetString());
            Assert.Equal("src/App.cs", json.GetProperty("file_impacts")[0].GetProperty("source_path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_ExcludeTestsJsonIgnoresOutOfScopeDuplicateDefinitions()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_exclude_tests_duplicate");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FooService.cs", "csharp",
                """
                public class FooService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "tests/FooServiceTests.cs", "csharp",
                """
                public class FooService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Boot(FooService service)
                    {
                        service.Run();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FooService", "--db", dbPath, "--exclude-tests", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("file_dependency_hints", json.GetProperty("impact_mode").GetString());
            Assert.True(json.GetProperty("heuristic").GetBoolean());
            Assert.False(json.GetProperty("has_multiple_definitions").GetBoolean());
            Assert.False(json.GetProperty("has_multiple_definition_files").GetBoolean());
            Assert.Equal(1, json.GetProperty("definition_file_count").GetInt32());
            Assert.Equal(1, json.GetProperty("hint_count").GetInt32());
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal("src/FooService.cs", json.GetProperty("definitions")[0].GetProperty("path").GetString());
            Assert.Equal("src/App.cs", json.GetProperty("file_impacts")[0].GetProperty("source_path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_UnsupportedLanguageDuplicateDoesNotTriggerMultipleDefinitions()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_unsupported_duplicate");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FooService.cs", "csharp",
                """
                public class FooService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/tools.txt", "text",
                """
                FooService() {
                  :
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Boot(FooService service)
                    {
                        service.Run();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FooService", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("file_dependency_hints", json.GetProperty("impact_mode").GetString());
            Assert.True(json.GetProperty("heuristic").GetBoolean());
            Assert.False(json.GetProperty("has_multiple_definitions").GetBoolean());
            Assert.False(json.GetProperty("has_multiple_definition_files").GetBoolean());
            Assert.Equal(1, json.GetProperty("definition_file_count").GetInt32());
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal("src/FooService.cs", json.GetProperty("definitions")[0].GetProperty("path").GetString());
            Assert.Equal("src/App.cs", json.GetProperty("file_impacts")[0].GetProperty("source_path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_ExactDefinitionResolutionSkipsUnsupportedMatchesBeforeLimit()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_unsupported_overflow");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (int i = 0; i < 60; i++)
            {
                TestProjectHelper.InsertIndexedFile(dbPath, $"scripts/Foo{i:D2}.txt", "text",
                    """
                    Foo() {
                      :
                    }
                    """);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Foo.cs", "csharp",
                """
                public class Foo
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Boot(Foo service)
                    {
                        service.Run();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Foo", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("file_dependency_hints", json.GetProperty("impact_mode").GetString());
            Assert.Equal(1, json.GetProperty("definition_count").GetInt32());
            Assert.Equal("src/Foo.cs", json.GetProperty("definitions")[0].GetProperty("path").GetString());
            Assert.Equal("src/App.cs", json.GetProperty("file_impacts")[0].GetProperty("source_path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_SubstringTypeEvidenceDoesNotProduceHeuristicHints()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_substring_type_evidence");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Foo.cs", "csharp",
                """
                public class Foo
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FooService.cs", "csharp",
                """
                public class FooService
                {
                    public void Run() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Handle(FooService service)
                    {
                        service.Run();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Foo", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.Equal(0, json.GetProperty("hint_count").GetInt32());
            Assert.Equal("class_symbol_no_symbol_callers", json.GetProperty("zero_result_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_DuplicateDefinitionsInOneFileJsonReportsAmbiguity()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_same_file_duplicate");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Services.cs", "csharp",
                """
                namespace A
                {
                    public class FooService
                    {
                        public void Run() { }
                    }
                }

                namespace B
                {
                    public class FooService
                    {
                        public void Run() { }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FooService", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.Equal(2, json.GetProperty("definition_count").GetInt32());
            Assert.Equal(1, json.GetProperty("definition_file_count").GetInt32());
            Assert.True(json.GetProperty("has_multiple_definitions").GetBoolean());
            Assert.False(json.GetProperty("has_multiple_definition_files").GetBoolean());
            Assert.Equal("multiple_definitions", json.GetProperty("zero_result_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_DuplicateDefinitionsInOneFileHumanOutputMentionsDefinitionAndFileCounts()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_same_file_duplicate_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Services.cs", "csharp",
                """
                namespace A
                {
                    public class FooService
                    {
                        public void Run() { }
                    }
                }

                namespace B
                {
                    public class FooService
                    {
                        public void Run() { }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FooService", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.DoesNotContain("file_dependency_hints", stdout);
            Assert.Contains("2 definitions across 1 file", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_HeuristicHintsJsonSetTruncatedAndReturnSuccess()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_hint_truncated");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FolderDiffService.cs", "csharp",
                """
                public class FolderDiffService
                {
                    public void ExecuteFolderDiffAsync() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App1.cs", "csharp",
                """
                public class App1
                {
                    public void Boot(FolderDiffService service)
                    {
                        service.ExecuteFolderDiffAsync();
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App2.cs", "csharp",
                """
                public class App2
                {
                    public void Boot(FolderDiffService service)
                    {
                        service.ExecuteFolderDiffAsync();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FolderDiffService", "--db", dbPath, "--limit", "1", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("file_dependency_hints", json.GetProperty("impact_mode").GetString());
            Assert.True(json.GetProperty("truncated").GetBoolean());
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("hint_count").GetInt32());
            Assert.Equal(1, json.GetProperty("file_impacts").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_HeuristicHintsJsonKeepActualReferenceCount()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_hint_refcount");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FolderDiffService.cs", "csharp",
                """
                public class FolderDiffService
                {
                    public void ExecuteFolderDiffAsync() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp",
                """
                public class App
                {
                    public void Boot(FolderDiffService service)
                    {
                        service.ExecuteFolderDiffAsync();
                        service.ExecuteFolderDiffAsync();
                        service.ExecuteFolderDiffAsync();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FolderDiffService", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("file_dependency_hints", json.GetProperty("impact_mode").GetString());
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(4, json.GetProperty("file_impacts")[0].GetProperty("reference_count").GetInt32());
            Assert.Equal("ExecuteFolderDiffAsync,FolderDiffService", json.GetProperty("file_impacts")[0].GetProperty("symbols").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_UnresolvedExternalCallWithoutTypeEvidenceReturnsNoHints()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_unresolved_external");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FolderDiffService.cs", "csharp",
                """
                public class FolderDiffService
                {
                    public void ExecuteFolderDiffAsync() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/ExternalConsumer.cs", "csharp",
                """
                public class ExternalConsumer
                {
                    public void Boot()
                    {
                        ExecuteFolderDiffAsync();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FolderDiffService", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("none", json.GetProperty("impact_mode").GetString());
            Assert.Equal(0, json.GetProperty("hint_count").GetInt32());
            Assert.Equal("class_symbol_no_symbol_callers", json.GetProperty("zero_result_reason").GetString());
            Assert.Equal(0, json.GetProperty("file_impacts").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImpact_CSharpVerbatimQueryMissKeepsOriginalInputInJsonAndHumanOutput()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_impact_verbatim_miss");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Verbatim.cs", "csharp",
                """
                public class @class
                {
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (jsonExitCode, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["@missing", "--db", dbPath, "--lang", "csharp", "--json"],
                _jsonOptions));

            using var jsonDocument = ParseJsonOutput(jsonStdout);
            var json = jsonDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, jsonExitCode);
            Assert.Equal(string.Empty, jsonStderr);
            Assert.Equal("@missing", json.GetProperty("query").GetString());
            Assert.Equal("@missing", json.GetProperty("resolved_name").GetString());
            Assert.Equal("no_matching_definition", json.GetProperty("zero_result_reason").GetString());

            var (humanExitCode, humanStdout, humanStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["@missing", "--db", dbPath, "--lang", "csharp"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Equal(string.Empty, humanStdout);
            Assert.Contains("No impact found for '@missing'.", humanStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
