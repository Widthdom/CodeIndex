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
    public void GraphCommands_ReferenceCapHitMarksAbsenceQueriesIncomplete_Issue4620()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_reference_cap_hit_4620");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.py", "python", "def app():\n    pass\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkIssuesReady();
                writer.MarkFoldReady();
                writer.MarkCSharpSymbolNameContractReady();
                using var command = db.Connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO file_issues (file_id, kind, line, message, severity)
                    SELECT id, 'reference_definition_lookup_symbol_budget_exceeded', 0, 'synthetic cap hit', 'warning'
                    FROM files
                    WHERE path = 'src/app.py'
                    """;
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            foreach (var commandName in new[] { "callers", "callees" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => RunGraphCommand(
                    commandName,
                    ["MissingSymbol", "--db", dbPath, "--json", "--count", "--exact"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = ParseJsonOutput(stdout);
                AssertReferenceGraphIncomplete(document.RootElement);
            }

            var (depsExitCode, depsStdout, depsStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--summary-only"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, depsExitCode);
            Assert.Equal(string.Empty, depsStderr);
            using (var depsDocument = ParseJsonOutput(depsStdout))
                AssertReferenceGraphIncomplete(depsDocument.RootElement);

            var (impactExitCode, impactStdout, impactStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["MissingSymbol", "--db", dbPath, "--json"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, impactExitCode);
            Assert.Equal(string.Empty, impactStderr);
            using (var impactDocument = ParseJsonOutput(impactStdout))
                AssertReferenceGraphIncomplete(impactDocument.RootElement);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphCommands_StaleDynamicContractMarksAbsenceQueriesIncomplete_Issue4746()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_dynamic_contract_4746");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cr",
                "crystal",
                "def helper(value)\n  value\nend\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkIssuesReady();
                writer.SetMeta(DbContext.GetSymbolExtractorVersionMetaKey("crystal"), "2");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => RunGraphCommand(
                "callers",
                ["MissingSymbol", "--db", dbPath, "--json", "--count", "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.GetProperty("reference_graph_complete").GetBoolean());
            Assert.Equal(
                0L,
                json.GetProperty("reference_extraction_cap_hits").GetProperty("hit_count").GetInt64());
            Assert.Contains(
                DbReader.DynamicReferenceGraphContractStaleReason,
                json.GetProperty("reference_graph_incomplete_reasons")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static void AssertReferenceGraphIncomplete(JsonElement json)
    {
        Assert.True(json.GetProperty("degraded").GetBoolean());
        Assert.False(json.GetProperty("reference_graph_complete").GetBoolean());
        Assert.Equal(50_000, json.GetProperty("reference_extraction_limits").GetProperty("max_lookup_symbols").GetInt32());
        Assert.Contains(
            "reference_definition_lookup_symbol_budget_exceeded",
            json.GetProperty("reference_graph_incomplete_reasons").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(1, json.GetProperty("reference_extraction_cap_hits").GetProperty("hit_count").GetInt64());
    }

    [Fact]
    public void RunDeps_InvalidFormat_FlattensControlCharacters_Issue3092()
    {
        var value = "bad\nforged\tvalue";

        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--format", value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("deps --format must be one of", stderr);
        Assert.Contains("bad forged value", stderr);
        Assert.DoesNotContain(value, stderr);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    public void RunDeps_UndocumentedFormatAliasesAreRejected_Issue4474(string format)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--format", format],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("deps --format must be one of edgelist, dot, graphml, or json-graph", stderr);
    }

    [Fact]
    public void GraphOutputHelp_IncludesBoundedJsonControls_Issue4112()
    {
        var depsUsage = ConsoleUi.GetUsageLine("deps");
        var hotspotsUsage = ConsoleUi.GetUsageLine("hotspots");

        Assert.Contains("--summary-only", depsUsage);
        Assert.Contains("--max-json-bytes", depsUsage);
        Assert.Contains("--summary-only", hotspotsUsage);
        Assert.Contains("--max-json-bytes", hotspotsUsage);
    }

    [Fact]
    public void RunDeps_JsonSummaryModesShareGraphFixture_Issues4112And4353And4450()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_summary_only");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--summary-only", "--limit", "80", "--lang", "sql"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("summary_only").GetBoolean());
            Assert.True(json.GetProperty("count").GetInt32() >= 1);
            Assert.False(json.TryGetProperty("edges", out _));
            Assert.Equal("sql", json.GetProperty("query_context").GetProperty("lang").GetString());
            Assert.Equal(string.Empty, stderr);

            var (verboseExitCode, verboseStdout, verboseStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--summary-only", "--limit", "80", "--lang", "sql", "--verbose"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, verboseExitCode);
            ParseJsonOutput(verboseStdout).Dispose();
            Assert.Contains("Progress: deps", verboseStderr);
            Assert.Contains("phase=read_edges", verboseStderr);
            Assert.Contains("phase=write_output", verboseStderr);

            var (graphExitCode, graphStdout, graphStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--format", "json-graph", "--summary-only", "--limit", "80", "--lang", "sql"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, graphExitCode);
            Assert.Equal(string.Empty, graphStdout);
            Assert.Contains("summary-only is not supported with --format json-graph", graphStderr);
            Assert.DoesNotContain("Progress: deps", graphStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_JsonFormatsShareMaxBytesFixture_Issue4112()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_max_json_bytes");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--max-json-bytes", "1", "--lang", "sql"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var error = document.RootElement;
            Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, error.GetProperty("error_code").GetString());
            Assert.Equal("response_budget", error.GetProperty("category").GetString());
            Assert.Equal("deps", error.GetProperty("command").GetString());
            Assert.Equal(1, error.GetProperty("requested_bytes").GetInt64());
            Assert.True(error.GetProperty("minimum_required_bytes_known").GetBoolean());

            var (graphExitCode, graphStdout, graphStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--format", "json-graph", "--max-json-bytes", "1", "--lang", "sql"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, graphExitCode);
            Assert.Equal(string.Empty, graphStderr);
            using var graphDocument = ParseJsonOutput(graphStdout);
            Assert.Equal(
                CommandErrorCodes.ResponseBudgetTooSmall,
                graphDocument.RootElement.GetProperty("error_code").GetString());
            Assert.Equal("deps", graphDocument.RootElement.GetProperty("command").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_BroadJsonSummaryOnly_FailsBeforeWorkspaceGraphScan_Issue4322()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_broad_summary_issue4322");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                for (var i = 0; i <= 250; i++)
                {
                    writer.UpsertFile(new FileRecord
                    {
                        Path = $"src/File{i:D3}.cs",
                        Lang = "csharp",
                        Size = 1,
                        Lines = 1,
                        Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        Checksum = Guid.NewGuid().ToString("N"),
                    });
                }
                writer.MarkGraphReady();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--summary-only", "--limit", "5"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("too broad for this index", stderr);
            Assert.DoesNotContain("phase=read_edges", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_MissingGraphJsonModesShareZeroPayloadFixture_Issues4112And4619()
    {
        var (projectRoot, readOnlyUri) = CreateReadOnlyMissingGraphTableDb("cdidx_deps_missing_graph_max_json_bytes");
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", readOnlyUri, "--json", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var error = document.RootElement;
            Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, error.GetProperty("error_code").GetString());
            Assert.Equal("response_budget", error.GetProperty("category").GetString());
            Assert.Equal("deps", error.GetProperty("command").GetString());
            Assert.Equal(1, error.GetProperty("requested_bytes").GetInt64());

            var (summaryExitCode, summaryStdout, _) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", readOnlyUri, "--json", "--summary-only"],
                _jsonOptions));

            using var summaryDocument = ParseJsonOutput(summaryStdout);
            var summaryJson = summaryDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, summaryExitCode);
            Assert.Equal(0, summaryJson.GetProperty("count").GetInt32());
            Assert.True(summaryJson.GetProperty("summary_only").GetBoolean());
            Assert.True(summaryJson.GetProperty("degraded").GetBoolean());
            Assert.False(summaryJson.TryGetProperty("edges", out _));

            foreach (var (arguments, resultsKey) in new[]
            {
                (new[] { "--db", readOnlyUri, "--json", "--cycles" }, "cycles"),
                (new[] { "--db", readOnlyUri, "--format", "json-graph", "--cycles" }, "edges"),
            })
            {
                var (cycleExitCode, cycleStdout, cycleStderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                    arguments,
                    _jsonOptions));

                using var cycleDocument = ParseJsonOutput(cycleStdout);
                var cycleJson = cycleDocument.RootElement;

                Assert.Equal(CommandExitCodes.Success, cycleExitCode);
                Assert.Equal(string.Empty, cycleStderr);
                Assert.Equal(0, cycleJson.GetProperty("count").GetInt32());
                Assert.False(cycleJson.GetProperty("graph_table_available").GetBoolean());
                Assert.True(cycleJson.GetProperty("degraded").GetBoolean());
                Assert.Contains("not authoritative", cycleJson.GetProperty("note").GetString());
                Assert.Empty(cycleJson.GetProperty(resultsKey).EnumerateArray());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_JsonSummaryOnly_OmitsEdgesForZeroPayload_Issue4112()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_zero_summary_only");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--summary-only"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("summary_only").GetBoolean());
            Assert.False(json.TryGetProperty("edges", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("dot", "--summary-only", null, "--summary-only is only supported with deps JSON output")]
    [InlineData("graphml", "--max-json-bytes", "1024", "--max-json-bytes is only supported with deps JSON output")]
    public void RunDeps_JsonOnlyControlsRejectNonJsonFormats_Issue4112(
        string format,
        string option,
        string? value,
        string expectedError)
    {
        var args = value is null
            ? new[] { "--json", "--format", format, option }
            : new[] { "--json", "--format", format, option, value };
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            args,
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(expectedError, stderr);
        Assert.Contains("Usage: cdidx deps", stderr);
    }

    [Fact]
    public void RunHotspots_JsonSummaryOnly_OmitsHotspotsAndEmitsProgress_Issue4112()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_summary_only");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Hotspot.cs", "csharp",
                """
                public class Hotspot
                {
                    private void Shared()
                    {
                        Shared();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--summary-only", "--kind", "function", "--limit", "80"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("summary_only").GetBoolean());
            Assert.True(json.GetProperty("count").GetInt32() >= 1);
            Assert.False(json.TryGetProperty("hotspots", out _));
            Assert.Equal("symbol", json.GetProperty("grouped_by").GetString());
            Assert.Contains("Progress: hotspots", stderr);
            Assert.Contains("phase=read_hotspots", stderr);
            Assert.Contains("phase=write_output", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_JsonMaxBytes_FailsBeforeWritingPayload_Issue4112()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_max_json_bytes");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Hotspot.cs", "csharp",
                """
                public class Hotspot
                {
                    private void Shared()
                    {
                        Shared();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--max-json-bytes", "1", "--kind", "function"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var error = document.RootElement;
            Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, error.GetProperty("error_code").GetString());
            Assert.Equal("response_budget", error.GetProperty("category").GetString());
            Assert.Equal("hotspots", error.GetProperty("command").GetString());
            Assert.Equal(1, error.GetProperty("requested_bytes").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_JsonMaxBytes_AppliesToMissingGraphZeroPayload_Issue4112()
    {
        var (projectRoot, readOnlyUri) = CreateReadOnlyMissingGraphTableDb("cdidx_hotspots_missing_graph_max_json_bytes");
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", readOnlyUri, "--json", "--max-json-bytes", "1", "--kind", "function"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var error = document.RootElement;
            Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, error.GetProperty("error_code").GetString());
            Assert.Equal("response_budget", error.GetProperty("category").GetString());
            Assert.Equal("hotspots", error.GetProperty("command").GetString());
            Assert.Equal(1, error.GetProperty("requested_bytes").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_JsonSummaryOnly_OmitsHotspotsForMissingGraphZeroPayload_Issue4112()
    {
        var (projectRoot, readOnlyUri) = CreateReadOnlyMissingGraphTableDb("cdidx_hotspots_missing_graph_summary_only");
        try
        {
            var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", readOnlyUri, "--json", "--summary-only", "--kind", "function"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("summary_only").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.TryGetProperty("hotspots", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_JsonSummaryOnly_OmitsHotspotsForZeroPayload_Issue4112()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_zero_summary_only");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--summary-only", "--kind", "function"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("summary_only").GetBoolean());
            Assert.False(json.TryGetProperty("hotspots", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByNameJsonSummaryOnly_OmitsHotspotsForZeroPayload_Issue4112()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_name_zero_summary_only");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--summary-only", "--group-by-name", "--kind", "function"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("summary_only").GetBoolean());
            Assert.False(json.TryGetProperty("hotspots", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphCommands_BodyOptionAddsCappedBodyExcerpt_Issue1594()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_body");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Session.cs", "csharp", """
            class Session
            {
                int Run(int user)
                {
                    var value = user;
                    return value;
                }

                int Login(int user)
                {
                    return Run(user);
                }
            }
            """);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var select = db.Connection.CreateCommand();
                select.CommandText = "SELECT id FROM files WHERE path = 'src/Session.cs'";
                var fileId = Convert.ToInt32(select.ExecuteScalar());
                var writer = new DbWriter(db.Connection);
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "Run",
                        ReferenceKind = "call",
                        Line = 11,
                        Column = 16,
                        Context = "        return Run(user);",
                        ContainerKind = "function",
                        ContainerName = "Login",
                    }
                ]);
                writer.MarkGraphReady();
            }

            AssertBodyExcerpt(
                QueryCommandRunner.RunReferences,
                ["Run", "--db", dbPath, "--json", "--body", "--snippet-lines", "1"],
                "int Login(int user)",
                expectedContentTruncated: true);
            AssertBodyExcerpt(
                QueryCommandRunner.RunCallers,
                ["Run", "--db", dbPath, "--json", "--body", "--snippet-lines", "20"],
                "int Login(int user)",
                expectedContentTruncated: false);
            AssertBodyExcerpt(
                QueryCommandRunner.RunCallees,
                ["Login", "--db", dbPath, "--json", "--body", "--snippet-lines", "1"],
                "int Run(int user)",
                expectedContentTruncated: true);

            var (textExitCode, textStdout, textStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Run", "--db", dbPath, "--body", "--snippet-lines", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, textExitCode);
            Assert.Contains("int Login(int user)", textStdout);
            Assert.Contains("references in", textStderr);

            var (impactExitCode, impactStdout, impactStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Run", "--db", dbPath, "--json", "--body", "--snippet-lines", "2"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, impactExitCode);
            Assert.Equal(string.Empty, impactStderr);
            using var impactDocument = ParseJsonOutput(impactStdout);
            var impactCaller = impactDocument.RootElement.GetProperty("callers")[0];
            Assert.Contains("int Login(int user)", impactCaller.GetProperty("body_content").GetString());
            Assert.Equal(2, CountLines(impactCaller.GetProperty("body_content").GetString()!));
            Assert.True(impactCaller.GetProperty("body_content_truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphCommands_ExplicitSnippetLinesRequireVisibleBodyOutput_Issue4882()
    {
        var scenarios = new (string[] Args, string ExpectedMessage)[]
        {
            (["--snippet-lines", "3"], "--snippet-lines requires --body"),
            (["--snippet-lines=3", "--json"], "--snippet-lines requires --body"),
            (["--snippet-lines", "3", "--format", "qf"], "--snippet-lines requires --body"),
            (["--snippet-lines", "3", "--format", "lsp"], "--snippet-lines requires --body"),
            (["--snippet-lines", "3", "--format", "compact"], "--snippet-lines requires --body"),
            (["--body", "--snippet-lines", "3", "--format", "qf"], "--snippet-lines with --body requires text or JSON result output"),
            (["--body", "--snippet-lines", "3", "--format", "lsp"], "--snippet-lines with --body requires text or JSON result output"),
            (["--body", "--snippet-lines", "3", "--format", "compact"], "--snippet-lines with --body requires text or JSON result output"),
            (["--body", "--snippet-lines", "3", "--count"], "--snippet-lines with --body requires text or JSON result output"),
        };

        foreach (var command in new[] { "references", "callers", "callees" })
        {
            foreach (var scenario in scenarios)
            {
                var args = new[] { "QueryCommandRunner" }.Concat(scenario.Args).ToArray();
                var (exitCode, stdout, stderr) = CaptureConsole(() => RunGraphCommand(command, args, _jsonOptions));

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Equal(string.Empty, stdout);
                Assert.Contains($"Error [{CommandErrorCodes.UsageError}]:", stderr);
                Assert.Contains(scenario.ExpectedMessage, stderr);
                Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
                Assert.DoesNotContain("database not found", stderr, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void GraphCommands_SnippetLinesAboveMaximumKeepRangeError_Issue4882()
    {
        foreach (var command in new[] { "references", "callers", "callees" })
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => RunGraphCommand(
                command,
                ["QueryCommandRunner", "--snippet-lines", "21"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("--snippet-lines must be less than or equal to 20, got '21'", stderr);
            Assert.DoesNotContain("--snippet-lines requires --body", stderr);
            Assert.DoesNotContain("database not found", stderr, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GraphCommands_OptionLikeVerbatimQueriesAreNotSnippetOptions_Issue4882()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_verbatim_snippet_query_4882");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            MarkGraphAndFoldReady(dbPath);
            var queryForms = new[]
            {
                new[] { "--db", dbPath, "--json", "--", "--snippet-lines" },
                new[] { "--db", dbPath, "--json", "--", "--snippet-lines=3" },
                new[] { "--db", dbPath, "--json", "--query", "--snippet-lines" },
            };

            foreach (var command in new[] { "references", "callers", "callees" })
            {
                foreach (var args in queryForms)
                {
                    var (exitCode, stdout, stderr) = CaptureConsole(
                        () => RunGraphCommand(command, args, _jsonOptions));

                    Assert.Equal(CommandExitCodes.Success, exitCode);
                    Assert.Equal(string.Empty, stderr);
                    using var document = ParseJsonOutput(stdout);
                    Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }







































    [Fact]
    public void GraphCommands_SymbolKindArgumentWarnsAboutReferenceKindSemantics()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_kind_warning");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            MarkGraphAndFoldReady(dbPath);

            foreach (var command in new[] { "references", "callers", "callees" })
            {
                var (exitCode, _, stderr) = CaptureConsole(() => RunGraphCommand(
                    command,
                    ["MissingSymbol", "--db", dbPath, "--kind", "class"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Contains("symbol kind", stderr);
                Assert.Contains("filters by reference kind", stderr);
                Assert.Contains("call", stderr);
                Assert.Contains("friend", stderr);
                Assert.Contains("instantiate", stderr);
                Assert.Contains("subscribe", stderr);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    public void GraphCommands_InvalidReferenceKindFailsWithScopedValidKindList(string command)
    {
        var args = new[] { "Target", "--kind", "badkind" };

        var (exitCode, stdout, stderr) = CaptureConsole(() => RunGraphCommand(command, args, _jsonOptions));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("invalid --kind value `badkind`", stderr);
        Assert.Contains("Hint: use one of:", stderr);
        Assert.Contains("call", stderr);
        Assert.Contains(command == "references" ? "type_reference" : "friend", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
    }



















































































































































































































    [Fact]
    public void GraphCommands_ExactZeroJson_RespectRequestedLimitAndCapSamples()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_graph_exact_zero_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SeedGraphExactZeroFixture(dbPath);

            foreach (var command in new[] { "references", "callers", "callees" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => RunGraphCommand(command,
                    GetExactZeroArgs(command, dbPath, limit: 6, queryOverride: null, countOnly: true),
                    _jsonOptions));

                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, json.GetProperty("count").GetInt32());
                Assert.Equal(6, json.GetProperty("exact_zero_hint").GetProperty("relaxed_count").GetInt32());
                Assert.Equal(5, json.GetProperty("exact_zero_hint").GetProperty("sample_names").GetArrayLength());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphCommands_ExactZeroJson_OmitHintWhenRelaxedQueryStillReturnsZero()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_graph_exact_zero_miss");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SeedGraphExactZeroFixture(dbPath);

            foreach (var command in new[] { "references", "callers", "callees" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => RunGraphCommand(command,
                    GetExactZeroArgs(command, dbPath, limit: 6, queryOverride: "DefinitelyMissing", countOnly: true),
                    _jsonOptions));

                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, json.GetProperty("count").GetInt32());
                Assert.False(json.TryGetProperty("exact_zero_hint", out _));
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }










}
