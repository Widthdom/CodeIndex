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
    public void RunDeps_JsonSummaryOnly_OmitsEdgesAndEmitsProgress_Issue4112()
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
            Assert.Contains("Progress: deps", stderr);
            Assert.Contains("phase=read_edges", stderr);
            Assert.Contains("phase=write_output", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_JsonMaxBytes_FailsBeforeWritingPayload_Issue4112()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_max_json_bytes");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--json", "--max-json-bytes", "1", "--lang", "sql"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("deps JSON output is", stderr);
            Assert.Contains("exceeds --max-json-bytes 1", stderr);
            Assert.Contains("Usage: cdidx deps", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_JsonGraphSummaryOnly_OmitsNodesAndEdges_Issue4112()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_json_graph_summary_only");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--format", "json-graph", "--summary-only", "--limit", "80", "--lang", "sql"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("summary_only").GetBoolean());
            Assert.True(json.GetProperty("count").GetInt32() >= 1);
            Assert.False(json.TryGetProperty("nodes", out _));
            Assert.False(json.TryGetProperty("edges", out _));
            Assert.Contains("Progress: deps", stderr);
            Assert.Contains("phase=write_output", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_JsonGraphMaxBytes_FailsBeforeWritingPayload_Issue4112()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_deps_json_graph_max_json_bytes");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", dbPath, "--format", "json-graph", "--max-json-bytes", "1", "--lang", "sql"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("deps JSON output is", stderr);
            Assert.Contains("exceeds --max-json-bytes 1", stderr);
            Assert.Contains("Usage: cdidx deps", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_JsonMaxBytes_AppliesToMissingGraphZeroPayload_Issue4112()
    {
        var (projectRoot, readOnlyUri) = CreateReadOnlyMissingGraphTableDb("cdidx_deps_missing_graph_max_json_bytes");
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", readOnlyUri, "--json", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("deps JSON output is", stderr);
            Assert.Contains("exceeds --max-json-bytes 1", stderr);
            Assert.Contains("Usage: cdidx deps", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_JsonSummaryOnly_OmitsEdgesForMissingGraphZeroPayload_Issue4112()
    {
        var (projectRoot, readOnlyUri) = CreateReadOnlyMissingGraphTableDb("cdidx_deps_missing_graph_summary_only");
        try
        {
            var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunDeps(
                ["--db", readOnlyUri, "--json", "--summary-only"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("summary_only").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.TryGetProperty("edges", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDeps_SummaryOnlyWithDotFormat_IsRejected_Issue4112()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--json", "--format", "dot", "--summary-only"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--summary-only is only supported with deps JSON output", stderr);
        Assert.Contains("Usage: cdidx deps", stderr);
    }

    [Fact]
    public void RunDeps_MaxJsonBytesWithGraphMlFormat_IsRejected_Issue4112()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDeps(
            ["--json", "--format", "graphml", "--max-json-bytes", "1024"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--max-json-bytes is only supported with deps JSON output", stderr);
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
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("hotspots JSON output is", stderr);
            Assert.Contains("exceeds --max-json-bytes 1", stderr);
            Assert.Contains("Usage: cdidx hotspots", stderr);
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
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("hotspots JSON output is", stderr);
            Assert.Contains("exceeds --max-json-bytes 1", stderr);
            Assert.Contains("Usage: cdidx hotspots", stderr);
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
            using (var db = new DbContext(dbPath))
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
                ["Run", "--db", dbPath, "--json", "--body", "--snippet-lines", "2"],
                "int Login(int user)",
                expectedContentTruncated: true);
            AssertBodyExcerpt(
                QueryCommandRunner.RunCallees,
                ["Login", "--db", dbPath, "--json", "--body", "--snippet-lines", "1"],
                "int Run(int user)",
                expectedContentTruncated: true);

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







































    [Theory]
    [InlineData("references", "MissingSymbol")]
    [InlineData("callers", "MissingSymbol")]
    [InlineData("callees", "MissingSymbol")]
    public void GraphCommands_SymbolKindArgumentWarnsAboutReferenceKindSemantics(string command, string query)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_{command}_kind_warning");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, _, stderr) = CaptureConsole(() => RunGraphCommand(
                command,
                [query, "--db", dbPath, "--kind", "class"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("symbol kind", stderr);
            Assert.Contains("filters by reference kind", stderr);
            Assert.Contains("call", stderr);
            Assert.Contains("friend", stderr);
            Assert.Contains("instantiate", stderr);
            Assert.Contains("subscribe", stderr);
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



















































































































































































































    [Theory]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    public void GraphCommands_ExactZeroJson_RespectRequestedLimitAndCapSamples(string command)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_query_runner_{command}_exact_zero_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SeedGraphExactZeroFixture(dbPath, command);

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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    public void GraphCommands_ExactZeroJson_OmitHintWhenRelaxedQueryStillReturnsZero(string command)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_query_runner_{command}_exact_zero_miss");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SeedGraphExactZeroFixture(dbPath, command);

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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }










}
