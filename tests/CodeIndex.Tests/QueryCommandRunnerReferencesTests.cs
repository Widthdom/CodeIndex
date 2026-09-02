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
    public void RunCallees_LocationFormatsPreserveFirstCallSiteSpanAndLegacyFallback_Issue4841()
    {
        const string targetName = "Issue4841CliTarget";
        const string callerName = "Issue4841CliCaller";
        const string callLine = "    void Issue4841CliCaller() { var text = \"日本語\";\tIssue4841CliTarget(); Issue4841CliTarget(); }";
        var source = string.Join('\n',
            "class Issue4841CliProbe",
            "{",
            "    void Issue4841CliTarget() { }",
            callLine,
            "}",
            "class Issue4841CliVeryLongParent { }",
            "class Issue4841CliChild : Issue4841CliVeryLongParent",
            "{",
            "    Issue4841CliChild() : base() { }",
            "}",
            "");
        var expectedColumn = callLine.IndexOf(targetName, StringComparison.Ordinal) + 1;
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callees_locations_issue4841");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Issue4841CliProbe.cs", "csharp", source);
            MarkGraphAndFoldReady(dbPath);
            string[] baseArgs = [callerName, "--db", dbPath, "--lang", "csharp", "--exact"];

            var (jsonExit, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--json"],
                _jsonOptions));
            var jsonRow = Assert.Single(ParseJsonLines(jsonStdout)).RootElement;
            Assert.Equal(CommandExitCodes.Success, jsonExit);
            Assert.Equal(string.Empty, jsonStderr);
            Assert.Equal(4, jsonRow.GetProperty("first_line").GetInt32());
            Assert.Equal(expectedColumn, jsonRow.GetProperty("first_column").GetInt32());
            Assert.Equal(targetName.Length, jsonRow.GetProperty("first_length").GetInt32());
            Assert.Equal(2, jsonRow.GetProperty("reference_count").GetInt32());

            var (compactExit, compactStdout, compactStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--format", "compact"],
                _jsonOptions));
            using var compactDocument = ParseJsonOutput(compactStdout);
            var compactRow = compactDocument.RootElement.GetProperty("results")[0];
            Assert.Equal(CommandExitCodes.Success, compactExit);
            Assert.Equal(string.Empty, compactStderr);
            Assert.Equal(expectedColumn, compactRow.GetProperty("column").GetInt32());

            var (quickfixExit, quickfixStdout, quickfixStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--format", "qf"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, quickfixExit);
            Assert.Equal(string.Empty, quickfixStderr);
            Assert.Equal(
                $"src/Issue4841CliProbe.cs:4:{expectedColumn}:{callerName} -> {targetName}",
                quickfixStdout.Trim());

            var (lspExit, lspStdout, lspStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--format", "lsp"],
                _jsonOptions));
            using var lspDocument = ParseJsonOutput(lspStdout);
            var preciseRange = lspDocument.RootElement[0].GetProperty("range");
            Assert.Equal(CommandExitCodes.Success, lspExit);
            Assert.Equal(string.Empty, lspStderr);
            Assert.Equal(3, preciseRange.GetProperty("start").GetProperty("line").GetInt32());
            Assert.Equal(expectedColumn - 1, preciseRange.GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(expectedColumn - 1 + targetName.Length, preciseRange.GetProperty("end").GetProperty("character").GetInt32());

            var (sarifExit, sarifStdout, sarifStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--format", "sarif"],
                _jsonOptions));
            using var sarifDocument = ParseJsonOutput(sarifStdout);
            var preciseRegion = sarifDocument.RootElement
                .GetProperty("runs")[0]
                .GetProperty("results")[0]
                .GetProperty("locations")[0]
                .GetProperty("physicalLocation")
                .GetProperty("region");
            Assert.Equal(CommandExitCodes.Success, sarifExit);
            Assert.Equal(string.Empty, sarifStderr);
            Assert.Equal(4, preciseRegion.GetProperty("startLine").GetInt32());
            Assert.Equal(expectedColumn, preciseRegion.GetProperty("startColumn").GetInt32());
            Assert.Equal(expectedColumn + targetName.Length, preciseRegion.GetProperty("endColumn").GetInt32());

            string[] constructorChainArgs = ["Issue4841CliChild", "--db", dbPath, "--lang", "csharp", "--exact"];
            var (chainJsonExit, chainJsonStdout, chainJsonStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. constructorChainArgs, "--json"],
                _jsonOptions));
            var chainJsonRow = ParseJsonLines(chainJsonStdout)
                .Select(document => document.RootElement)
                .Single(row => row.GetProperty("callee_name").GetString() == "Issue4841CliVeryLongParent");
            Assert.Equal(CommandExitCodes.Success, chainJsonExit);
            Assert.Equal(string.Empty, chainJsonStderr);
            Assert.Equal("Issue4841CliVeryLongParent", chainJsonRow.GetProperty("callee_name").GetString());
            Assert.Equal("base".Length, chainJsonRow.GetProperty("first_length").GetInt32());

            var (chainLspExit, chainLspStdout, chainLspStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. constructorChainArgs, "--format", "lsp"],
                _jsonOptions));
            using var chainLspDocument = ParseJsonOutput(chainLspStdout);
            var chainRange = chainLspDocument.RootElement
                .EnumerateArray()
                .Select(location => location.GetProperty("range"))
                .Single(range =>
                    range.GetProperty("end").GetProperty("character").GetInt32()
                    - range.GetProperty("start").GetProperty("character").GetInt32()
                    == "base".Length);
            Assert.Equal(CommandExitCodes.Success, chainLspExit);
            Assert.Equal(string.Empty, chainLspStderr);
            Assert.Equal(
                chainRange.GetProperty("start").GetProperty("character").GetInt32() + "base".Length,
                chainRange.GetProperty("end").GetProperty("character").GetInt32());

            var (chainSarifExit, chainSarifStdout, chainSarifStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. constructorChainArgs, "--format", "sarif"],
                _jsonOptions));
            using var chainSarifDocument = ParseJsonOutput(chainSarifStdout);
            var chainRegion = chainSarifDocument.RootElement
                .GetProperty("runs")[0]
                .GetProperty("results")
                .EnumerateArray()
                .Select(result => result
                    .GetProperty("locations")[0]
                    .GetProperty("physicalLocation")
                    .GetProperty("region"))
                .Single(region =>
                    region.GetProperty("endColumn").GetInt32()
                    - region.GetProperty("startColumn").GetInt32()
                    == "base".Length);
            Assert.Equal(CommandExitCodes.Success, chainSarifExit);
            Assert.Equal(string.Empty, chainSarifStderr);
            Assert.Equal(
                chainRegion.GetProperty("startColumn").GetInt32() + "base".Length,
                chainRegion.GetProperty("endColumn").GetInt32());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var command = db.Connection.CreateCommand();
                command.CommandText = """
                    UPDATE symbol_references
                    SET span_length = NULL
                    WHERE container_name = @caller
                      AND symbol_name = @target;
                    """;
                command.Parameters.AddWithValue("@caller", callerName);
                command.Parameters.AddWithValue("@target", targetName);
                Assert.Equal(2, command.ExecuteNonQuery());
            }

            var (spanlessJsonExit, spanlessJsonStdout, spanlessJsonStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--json"],
                _jsonOptions));
            var spanlessJsonRow = Assert.Single(ParseJsonLines(spanlessJsonStdout)).RootElement;
            Assert.Equal(CommandExitCodes.Success, spanlessJsonExit);
            Assert.Equal(string.Empty, spanlessJsonStderr);
            Assert.Equal(expectedColumn, spanlessJsonRow.GetProperty("first_column").GetInt32());
            Assert.Equal(JsonValueKind.Null, spanlessJsonRow.GetProperty("first_length").ValueKind);

            var (spanlessLspExit, spanlessLspStdout, spanlessLspStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--format", "lsp"],
                _jsonOptions));
            using var spanlessLspDocument = ParseJsonOutput(spanlessLspStdout);
            var spanlessRange = spanlessLspDocument.RootElement[0].GetProperty("range");
            Assert.Equal(CommandExitCodes.Success, spanlessLspExit);
            Assert.Equal(string.Empty, spanlessLspStderr);
            Assert.Equal(expectedColumn - 1, spanlessRange.GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(expectedColumn - 1, spanlessRange.GetProperty("end").GetProperty("character").GetInt32());

            var (spanlessSarifExit, spanlessSarifStdout, spanlessSarifStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--format", "sarif"],
                _jsonOptions));
            using var spanlessSarifDocument = ParseJsonOutput(spanlessSarifStdout);
            var spanlessRegion = spanlessSarifDocument.RootElement
                .GetProperty("runs")[0]
                .GetProperty("results")[0]
                .GetProperty("locations")[0]
                .GetProperty("physicalLocation")
                .GetProperty("region");
            Assert.Equal(CommandExitCodes.Success, spanlessSarifExit);
            Assert.Equal(string.Empty, spanlessSarifStderr);
            Assert.Equal(expectedColumn, spanlessRegion.GetProperty("startColumn").GetInt32());
            Assert.False(spanlessRegion.TryGetProperty("endColumn", out _));

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var command = db.Connection.CreateCommand();
                command.CommandText = """
                    UPDATE symbol_references
                    SET column_number = NULL
                    WHERE container_name = @caller
                      AND symbol_name = @target;
                    """;
                command.Parameters.AddWithValue("@caller", callerName);
                command.Parameters.AddWithValue("@target", targetName);
                Assert.Equal(2, command.ExecuteNonQuery());
            }

            var (legacyJsonExit, legacyJsonStdout, legacyJsonStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--json"],
                _jsonOptions));
            var legacyJsonRow = Assert.Single(ParseJsonLines(legacyJsonStdout)).RootElement;
            Assert.Equal(CommandExitCodes.Success, legacyJsonExit);
            Assert.Equal(string.Empty, legacyJsonStderr);
            Assert.Equal(JsonValueKind.Null, legacyJsonRow.GetProperty("first_column").ValueKind);
            Assert.Equal(JsonValueKind.Null, legacyJsonRow.GetProperty("first_length").ValueKind);

            var (legacyQuickfixExit, legacyQuickfixStdout, legacyQuickfixStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--format", "qf"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, legacyQuickfixExit);
            Assert.Equal(string.Empty, legacyQuickfixStderr);
            Assert.Contains(":4:0:", legacyQuickfixStdout, StringComparison.Ordinal);

            var (legacyLspExit, legacyLspStdout, legacyLspStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--format", "lsp"],
                _jsonOptions));
            using var legacyLspDocument = ParseJsonOutput(legacyLspStdout);
            var legacyRange = legacyLspDocument.RootElement[0].GetProperty("range");
            Assert.Equal(CommandExitCodes.Success, legacyLspExit);
            Assert.Equal(string.Empty, legacyLspStderr);
            Assert.Equal(0, legacyRange.GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(0, legacyRange.GetProperty("end").GetProperty("character").GetInt32());

            var (legacySarifExit, legacySarifStdout, legacySarifStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                [.. baseArgs, "--format", "sarif"],
                _jsonOptions));
            using var legacySarifDocument = ParseJsonOutput(legacySarifStdout);
            var legacyRegion = legacySarifDocument.RootElement
                .GetProperty("runs")[0]
                .GetProperty("results")[0]
                .GetProperty("locations")[0]
                .GetProperty("physicalLocation")
                .GetProperty("region");
            Assert.Equal(CommandExitCodes.Success, legacySarifExit);
            Assert.Equal(string.Empty, legacySarifStderr);
            Assert.Equal(1, legacyRegion.GetProperty("startColumn").GetInt32());
            Assert.False(legacyRegion.TryGetProperty("endColumn", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ConfigLanguageSelectsRulesReferences_Issue4740()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_config_language");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "rules/policy.rules",
                "config",
                """prefix_rule(include = ["rules/common.rules"], decision = "allow")""");
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["rules/common.rules", "--db", dbPath, "--json", "--lang", "config", "--exact-name"],
                _jsonOptions));
            var rows = ParseJsonLines(stdout).Select(line => line.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var row = Assert.Single(rows);
            Assert.Equal("config", row.GetProperty("lang").GetString());
            Assert.Equal("rules/common.rules", row.GetProperty("symbol_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphQueries_ReportMissingHdlGraphContractWithoutExactMode_Issue4742()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hdl_graph_contract_queries");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "top.v",
                "verilog",
                """
                module child;
                endmodule
                module top;
                    child u_child();
                endmodule
                """);
            MarkGraphAndFoldReady(dbPath);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var command = db.Connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM symbol_references;
                    DELETE FROM codeindex_meta WHERE key = 'hdl_graph_contract_version';
                    """;
                command.ExecuteNonQuery();
            }

            var queryResults = new[]
            {
                CaptureConsole(() => QueryCommandRunner.RunReferences(
                    ["child", "--db", dbPath, "--lang", "verilog", "--json"],
                    _jsonOptions)),
                CaptureConsole(() => QueryCommandRunner.RunCallers(
                    ["child", "--db", dbPath, "--lang", "verilog", "--json"],
                    _jsonOptions)),
                CaptureConsole(() => QueryCommandRunner.RunCallees(
                    ["child", "--db", dbPath, "--lang", "verilog", "--json"],
                    _jsonOptions)),
                CaptureConsole(() => QueryCommandRunner.RunImpact(
                    ["child", "--db", dbPath, "--lang", "verilog", "--json"],
                    _jsonOptions)),
                CaptureConsole(() => QueryCommandRunner.RunUnused(
                    ["--db", dbPath, "--lang", "verilog", "--json"],
                    _jsonOptions)),
                CaptureConsole(() => QueryCommandRunner.RunDeps(
                    ["--db", dbPath, "--lang", "verilog", "--json"],
                    _jsonOptions)),
            };

            foreach (var (exitCode, stdout, _) in queryResults)
            {
                Assert.Equal(CommandExitCodes.Success, exitCode);
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;
                Assert.True(json.GetProperty("degraded").GetBoolean());
                Assert.False(json.GetProperty("hdl_graph_contract_ready").GetBoolean());
                Assert.Contains(
                    "hdl_graph_contract_ready=false",
                    json.GetProperty("hdl_graph_contract_degraded_reason").GetString(),
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_AllowsExcludePathValueThatLooksLikePreviewOption()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_preview_like_exclude_path_value");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["target", "--db", dbPath, "--exclude-path=--focus-line", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("0", stdout.Trim());
            Assert.DoesNotContain("is not supported", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_RejectsMissingMaxLineWidthValue()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_missing_max_line_width");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["target", "--db", dbPath, "--max-line-width", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            // Missing-value guard short-circuits before TryParsePositiveInt; see
            // RunExcerpt_RejectsMissingFocusColumnValue for the matching contract note.
            // TryParsePositiveInt より前で値欠如として短絡する。契約の詳細は上記テスト参照。
            Assert.Contains("--max-line-width requires a value", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_UsageIncludesCount()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
            [],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("[--count]", stderr);
    }

    [Fact]
    public void RunReferences_RejectsNegativeAndNonNumericMaxLineWidthValues()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_invalid_max_line_width");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            foreach (var invalidValue in new[] { "-1", "abc" })
            {
                var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                    ["target", "--db", dbPath, "--max-line-width", invalidValue, "--json"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Contains("--max-line-width requires an integer between 0 and 4096", stderr);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_JsonZeroResults_EmitEnvelopeAndFreshness()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_zero_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 32,
                    Lines = 1,
                    Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "bootstrap", Line = 1, StartLine = 1, EndLine = 1 }
                ]);
                writer.MarkGraphReady();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["MissingRef", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertZeroResultPayload(json, "references");
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_JsonKeepsCsharpTypeAliasPatternReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_csharp_type_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Defs.cs",
                "csharp",
                """
                using Red = RealTypes.Red;
                using static Probe.Color;

                namespace Probe;

                enum Color { Red, Blue }
                class Demo
                {
                    bool Match(object value) => value is Red;
                    void ProbeType() { _ = typeof(Red); }
                }

                namespace RealTypes;
                class Red {}
                """);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var countCmd = db.Connection.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE symbol_name = 'Red'";
                Assert.Equal(2L, (long)countCmd.ExecuteScalar()!);
            }
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            var references = ParseJsonLines(stdout).Select(line => line.RootElement).ToList();
            Assert.Equal(2, references.Count);
            Assert.Contains(references, reference =>
                reference.GetProperty("symbol_name").GetString() == "Red"
                && reference.GetProperty("reference_kind").GetString() == "type_reference"
                && reference.GetProperty("container_name").GetString() == "Match");
            Assert.Contains(references, reference =>
                reference.GetProperty("symbol_name").GetString() == "Red"
                && reference.GetProperty("reference_kind").GetString() == "type_reference"
                && reference.GetProperty("container_name").GetString() == "ProbeType");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_JsonKeepsGlobalCsharpTypeAliasPatternReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_csharp_global_type_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/GlobalUsings.cs",
                "csharp",
                """
                global using Red = RealTypes.Red;
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Use.cs",
                "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                enum Color { Red, Blue }
                class Demo
                {
                    bool Match(object value) => value is Red;
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/RealRed.cs",
                "csharp",
                """
                namespace RealTypes;
                class Red {}
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            var references = ParseJsonLines(stdout).Select(line => line.RootElement).ToList();
            Assert.Single(references);
            Assert.Equal("Red", references[0].GetProperty("symbol_name").GetString());
            Assert.Equal("type_reference", references[0].GetProperty("reference_kind").GetString());
            Assert.Equal("Match", references[0].GetProperty("container_name").GetString());

            var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            var countJson = ParseJsonOutput(countStdout).RootElement;
            Assert.Equal(1, countJson.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_JsonKeepsCsharpQualifiedIsPatternCallExact()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_csharp_qualified_is_pattern_call_exact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Use.cs",
                "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }

                public class Red {}

                class Demo
                {
                    bool Match(object value) => value is Color.Red or Color.Blue;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--kind", "member_read"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            var references = ParseJsonLines(stdout).Select(line => line.RootElement).ToList();
            Assert.Single(references);
            var reference = references[0];
            Assert.Equal("Red", reference.GetProperty("symbol_name").GetString());
            Assert.Equal("member_read", reference.GetProperty("reference_kind").GetString());
            Assert.Equal("Match", reference.GetProperty("container_name").GetString());
            Assert.Contains("value is Color.Red or Color.Blue;", reference.GetProperty("context").GetString());

            var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--kind", "member_read", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            var countJson = ParseJsonOutput(countStdout).RootElement;
            Assert.Equal(1, countJson.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_JsonClampsLongSingleLineContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_long_line");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longLine = "const x = 0; " + new string('a', 320) + " target(); " + new string('b', 320);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "dist/app.js",
                    Lang = "javascript",
                    Size = longLine.Length,
                    Lines = 1,
                    Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertChunks([
                    new ChunkRecord { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = longLine }
                ]);
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "target",
                        ReferenceKind = "call",
                        Line = 1,
                        Column = longLine.IndexOf("target", StringComparison.Ordinal) + 1,
                        Context = longLine,
                    }
                ]);
                writer.MarkGraphReady();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["target", "--db", dbPath, "--json", "--max-line-width", "96"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("context_truncated").GetBoolean());
            Assert.Contains("target()", json.GetProperty("context").GetString());
            Assert.True(json.GetProperty("context").GetString()!.Length <= 96);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphCommands_AcceptExplicitCaptureAndProjectReferenceKinds()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_explicit_dependency_kinds");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var csharpFileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/Capture.cs",
                    Lang = "csharp",
                    Size = 64,
                    Lines = 1,
                    Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                var solutionFileId = writer.UpsertFile(new FileRecord
                {
                    Path = "CodeIndex.sln",
                    Lang = "solution",
                    Size = 64,
                    Lines = 1,
                    Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = csharpFileId,
                        SymbolName = "seed",
                        ReferenceKind = "capture",
                        Line = 1,
                        Column = 20,
                        Context = "Action read = () => seed;",
                        ContainerKind = "lambda",
                        ContainerName = "ReadSeed",
                    },
                    new ReferenceRecord
                    {
                        FileId = solutionFileId,
                        SymbolName = "src/App/App.csproj",
                        ReferenceKind = "project_reference",
                        Line = 1,
                        Column = 1,
                        Context = "Project(src/App/App.csproj)",
                        ContainerKind = "project",
                        ContainerName = "App",
                    },
                ]);
                writer.MarkGraphReady();
            }

            foreach (var (query, kind, lang) in new[]
            {
                ("seed", "capture", "csharp"),
                ("src/App/App.csproj", "project_reference", "solution"),
            })
            {
                var (referencesExitCode, referencesStdout, referencesStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                    [query, "--db", dbPath, "--kind", kind, "--lang", lang, "--exact", "--json"],
                    _jsonOptions));
                using var referencesDocument = ParseJsonOutput(referencesStdout);

                var (callersExitCode, callersStdout, callersStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                    [query, "--db", dbPath, "--kind", kind, "--lang", lang, "--json"],
                    _jsonOptions));
                using var callersDocument = ParseJsonOutput(callersStdout);

                Assert.Equal(CommandExitCodes.Success, referencesExitCode);
                Assert.Equal(string.Empty, referencesStderr);
                Assert.Equal(kind, referencesDocument.RootElement.GetProperty("reference_kind").GetString());
                Assert.Equal(CommandExitCodes.Success, callersExitCode);
                Assert.Equal(string.Empty, callersStderr);
                Assert.Equal(kind, callersDocument.RootElement.GetProperty("reference_kind").GetString());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_JsonZeroResults_WithMissingGraphTable_ReturnsDegradedPayload()
    {
        var (projectRoot, readOnlyUri) = CreateReadOnlyMissingGraphTableDb("cdidx_references_zero_json_missing_graph");
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Run", "--db", readOnlyUri, "--json", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.GetProperty("exact_index_available").GetBoolean());
            Assert.Contains("symbol_references table missing", json.GetProperty("degraded_reason").GetString());
            Assert.Equal(0, json.GetProperty("references").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_CountOnlyJson_WithMissingGraphTable_ReturnsNonAuthoritativeEnvelope_Issue3566()
    {
        var (projectRoot, readOnlyUri) = CreateReadOnlyMissingGraphTableDb("cdidx_references_count_json_missing_graph_3566");
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Run", "--db", readOnlyUri, "--json", "--exact", "--count", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var queryContext = json.GetProperty("query_context");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("files").GetInt32());
            Assert.Equal(0, json.GetProperty("file_count").GetInt32());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.GetProperty("authoritative_count").GetBoolean());
            Assert.False(json.GetProperty("exact_index_available").GetBoolean());
            Assert.True(json.GetProperty("freshness_available").GetBoolean());
            Assert.True(json.GetProperty("indexed_file_count").GetInt32() > 0);
            Assert.Equal("Run", queryContext.GetProperty("text").GetString());
            Assert.Equal("csharp", queryContext.GetProperty("lang").GetString());
            Assert.True(queryContext.GetProperty("count").GetBoolean());
            Assert.True(queryContext.GetProperty("exact").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_Json_CSharpInterpolationBoundariesShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_csharp_interpolation_workspace");
        try
        {
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/interpolated-raw.cs",
                """"
                public class RawApp
                {
                    private string RunRaw() => "ok";

                    public string Render()
                    {
                        return $"""
                            value = {RunRaw()}
                            literal = function main()
                        """;
                    }
                }
                """");
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/nested-interpolated-raw.cs",
                """"
                public class NestedApp
                {
                    private string RunNested() => "ok";

                    public string Render()
                    {
                        return $"""
                            value = {$"{RunNested()}"}
                            literal = function main()
                        """;
                    }
                }
                """");
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/escaped-verbatim-braces.cs",
                """
                public class EscapedApp
                {
                    public string Render()
                    {
                        return $@"{{EscapedOnlyRun()}}";
                    }
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/nested-raw-fixture.cs",
                """"
                public class NestedRawApp
                {
                    private int RunNestedRaw() => 1;
                    private string IdNestedRaw(string value) => value;

                    public int Render()
                    {
                        return $"""
                            value = {IdNestedRaw("""
                                ExecuteNestedRaw();
                                public class PhantomNestedRaw
                                {
                                    public void Go() { }
                                }
                                """) + RunNestedRaw()}
                            """.Length;
                    }
                }
                """");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            void AssertSingleCall(string query, string path)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                    [query, "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                    _jsonOptions));
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(query, json.GetProperty("symbol_name").GetString());
                Assert.Equal(path, json.GetProperty("path").GetString());
                Assert.Equal("call", json.GetProperty("reference_kind").GetString());
                Assert.Equal("Render", json.GetProperty("container_name").GetString());
                Assert.Equal(8, json.GetProperty("line").GetInt32());
                Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            }

            void AssertNoReferences(string query)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                    [query, "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                    _jsonOptions));
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, json.GetProperty("count").GetInt32());
                Assert.Equal(0, json.GetProperty("references").GetArrayLength());
                Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            }

            AssertSingleCall("RunRaw", "src/interpolated-raw.cs");
            AssertSingleCall("RunNested", "src/nested-interpolated-raw.cs");
            AssertNoReferences("EscapedOnlyRun");
            AssertNoReferences("ExecuteNestedRaw");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_AcceptsTypeReferenceKind_WithoutUnknownKindWarning()
    {
        // issue #444: `references --kind type_reference` is a legitimate query (compile-time
        // type-position edges from C#/Java base lists, declaration types, generic constraints,
        // `is`/`as`/`instanceof`, and XML-doc `cref`). It must succeed without the "unknown
        // reference kind" hint that was previously printed by `WriteGraphReferenceKindHint`.
        // issue #444: `references --kind type_reference` は compile-time な型位置エッジを
        // 列挙する正当なクエリ（C#/Java の継承リスト・宣言型・generic 制約・`is`/`as`/`instanceof`・
        // XML-doc `cref`）。以前は `WriteGraphReferenceKindHint` が "unknown reference kind" と
        // 警告していたが、その偽警告を出さずに成功しなければならない。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_type_reference_kind");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Target.cs"),
                """
                public class TargetBase { }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Consumer.cs"),
                """
                public class Consumer : TargetBase
                {
                }
                """);

            var (indexExitCode, _, _) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["TargetBase", "--db", dbPath, "--kind", "type_reference", "--lang", "csharp", "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.DoesNotContain("not a known reference kind", stderr);
            Assert.DoesNotContain("WARN:", stderr);
            Assert.Contains("type_reference", stdout);
            Assert.Contains("TargetBase", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_AcceptsAugmentationKind_WithoutUnknownKindWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_augmentation_kind");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "a.ts"),
                """
                interface Widget { a: number }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "b.ts"),
                """
                interface Widget { b: string }
                """);

            var (indexExitCode, _, _) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Widget", "--db", dbPath, "--kind", "augmentation", "--lang", "typescript", "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.DoesNotContain("not a known reference kind", stderr);
            Assert.DoesNotContain("WARN:", stderr);
            Assert.Contains("augmentation", stdout);
            Assert.Contains("Widget", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_JsonTypeReferenceKind_EmitsNoStderrWarning()
    {
        // issue #444 JSON path: the stderr "unknown reference kind" hint is suppressed for
        // `--json`, but the fix also straightens the validation set so `type_reference` is
        // accepted everywhere without relying on JSON suppression.
        // issue #444 JSON 経路: `--json` のときは stderr のヒント自体が抑制されるが、
        // 今回の修正で検証集合も整理されたため、JSON 抑制に頼らずとも `type_reference` が
        // 受理されることを確認する。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_type_reference_kind_json");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "User.cs"),
                """
                public class User { }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Consumer.cs"),
                """
                public class Consumer : User
                {
                }
                """);

            var (indexExitCode, _, _) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["User", "--db", dbPath, "--kind", "type_reference", "--json", "--lang", "csharp", "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("\"reference_kind\":\"type_reference\"", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_JsonZeroResults_WithMissingGraphTable_ReturnsDegradedPayload()
    {
        var (projectRoot, readOnlyUri) = CreateReadOnlyMissingGraphTableDb("cdidx_callers_zero_json_missing_graph");
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Run", "--db", readOnlyUri, "--json", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.GetProperty("exact_index_available").GetBoolean());
            Assert.Contains("symbol_references table missing", json.GetProperty("degraded_reason").GetString());
            Assert.Equal(0, json.GetProperty("callers").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_CSharpScopeVariantsShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_csharp_scope_workspace");
        try
        {
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/ternary.cs",
                """
                public class Dispatcher
                {
                    private string Select(bool isUpdate)
                        => isUpdate
                            ? RunUpdateMode()
                            : RunFullScan();

                    private string RunUpdateMode() => "update";
                    private string RunFullScan() => "full";
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/allman-block-property.cs",
                """
                public class AllmanCalc
                {
                    public int ComputeAllman() => 42;

                    public int WrapAllman
                    {
                        get { return ComputeAllman(); }
                    }
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/multiline-expression-property.cs",
                """
                public class ExpressionCalc
                {
                    public int ComputeExpression() => 42;
                    public int WrapExpression
                        => ComputeExpression();
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/same-line-brace-property.cs",
                """
                public class BraceCalc
                {
                    public int ComputeBrace() => 42;

                    public int WrapBrace {
                        get { return ComputeBrace(); }
                    }
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/allman-comment-property.cs",
                """
                public class CommentBlockCalc
                {
                    public int ComputeCommentBlock() => 42;

                    public int WrapCommentBlock
                    /* some multi-line
                       block comment */
                    {
                        get { return ComputeCommentBlock(); }
                    }
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/expression-comment-property.cs",
                """
                public class CommentExpressionCalc
                {
                    public int ComputeCommentExpression() => 42;

                    public int WrapCommentExpression
                    /* multi-line
                       comment */
                        => ComputeCommentExpression();
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/multiline-switch-arm.cs",
                """
                public class SwitchArm
                {
                    public string Read(object value)
                    {
                        return value switch
                        {
                            string text
                                => text.Trim(),
                            _ => ""
                        };
                    }
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            void AssertCaller(
                string query,
                string path,
                string callerKind,
                string callerName,
                int? firstLine = null,
                bool exact = true)
            {
                var args = new List<string>
                {
                    query,
                    "--db", Path.Combine(projectRoot, ".cdidx", "codeindex.db"),
                    "--json",
                    "--lang", "csharp",
                };
                if (exact)
                    args.Add("--exact-name");
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                    [.. args],
                    _jsonOptions));
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(path, json.GetProperty("path").GetString());
                Assert.Equal(callerKind, json.GetProperty("caller_kind").GetString());
                Assert.Equal(callerName, json.GetProperty("caller_name").GetString());
                Assert.Equal(query, json.GetProperty("callee_name").GetString());
                Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
                if (exact)
                    Assert.True(json.GetProperty("exact_index_available").GetBoolean());
                else
                    Assert.Equal("name_discovery", json.GetProperty("graph_evidence_confidence").GetString());
                if (firstLine is not null)
                {
                    Assert.Equal(firstLine.Value, json.GetProperty("first_line").GetInt32());
                }
            }

            // These issue #233 regression shapes exercise one immutable graph. Unique callees
            // keep every assertion isolated while avoiding a full CLI indexing pass per shape.
            // issue #233 の各回帰形状は同一の不変 graph で検証する。callee を固有名にして
            // アサーションを分離しつつ、形状ごとの CLI full index を避ける。
            AssertCaller("RunUpdateMode", "src/ternary.cs", "function", "Select", firstLine: 5);
            AssertCaller("ComputeAllman", "src/allman-block-property.cs", "property", "WrapAllman");
            AssertCaller("ComputeExpression", "src/multiline-expression-property.cs", "property", "WrapExpression", firstLine: 5);
            AssertCaller("ComputeBrace", "src/same-line-brace-property.cs", "property", "WrapBrace");
            AssertCaller("ComputeCommentBlock", "src/allman-comment-property.cs", "property", "WrapCommentBlock");
            AssertCaller("ComputeCommentExpression", "src/expression-comment-property.cs", "property", "WrapCommentExpression");
            AssertCaller("Trim", "src/multiline-switch-arm.cs", "function", "Read", exact: false);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_CSharpCompactSameLineTypeBody_PrefersInnermostMethodContainer()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_csharp_compact_same_line_type_body");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace N;
                enum Color { Red }
                class C { int N => 0; void M() { var x = global::N.Color.Red; } }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--include-member-reads"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("function", json.GetProperty("caller_kind").GetString());
            Assert.Equal("M", json.GetProperty("caller_name").GetString());
            Assert.Equal("Red", json.GetProperty("callee_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallees_JsonZeroResults_WithMissingGraphTable_ReturnsDegradedPayload()
    {
        var (projectRoot, readOnlyUri) = CreateReadOnlyMissingGraphTableDb("cdidx_callees_zero_json_missing_graph");
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["Run", "--db", readOnlyUri, "--json", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.GetProperty("exact_index_available").GetBoolean());
            Assert.Contains("symbol_references table missing", json.GetProperty("degraded_reason").GetString());
            Assert.Equal(0, json.GetProperty("callees").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_StaleSqlGraphCountAndResultsShareFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["dbo.fn_Target", "--db", dbPath, "--json", "--lang", "sql", "--exact", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("degraded_reason").GetString());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());

            var (resultsExitCode, resultsStdout, resultsStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["dbo.fn_Target", "--db", dbPath, "--json", "--lang", "sql"],
                _jsonOptions));

            using var resultsDocument = ParseJsonOutput(resultsStdout);
            var resultsJson = resultsDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, resultsExitCode);
            Assert.Equal(string.Empty, resultsStderr);
            Assert.Equal("fn_Target", resultsJson.GetProperty("symbol_name").GetString());
            Assert.False(resultsJson.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", resultsJson.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallersAndCallees_StaleSqlGraphResultsShareFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_sql_graph_contract_results");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["dbo.fn_Target", "--db", dbPath, "--json", "--lang", "sql"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("fn_Target", json.GetProperty("callee_name").GetString());
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());

            var (calleesExitCode, calleesStdout, calleesStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["dbo.usp_Caller", "--db", dbPath, "--json", "--lang", "sql"],
                _jsonOptions));

            using var calleesDocument = ParseJsonOutput(calleesStdout);
            var calleesJson = calleesDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, calleesExitCode);
            Assert.Equal(string.Empty, calleesStderr);
            Assert.Equal("dbo.usp_Caller", calleesJson.GetProperty("caller_name").GetString());
            Assert.False(calleesJson.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", calleesJson.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_MixedRepoPureCSharpResultsAndCountShareFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_mixed_sql_graph_contract_results");
        try
        {
            var dbPath = CreateMixedSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["N", "--db", dbPath, "--json", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("N", json.GetProperty("callee_name").GetString());
            Assert.False(json.TryGetProperty("sql_graph_contract_ready", out _));
            Assert.False(json.TryGetProperty("sql_graph_contract_degraded_reason", out _));

            var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["N", "--db", dbPath, "--json", "--exact", "--count"],
                _jsonOptions));

            using var countDocument = ParseJsonOutput(countStdout);
            var countJson = countDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            Assert.Equal(1, countJson.GetProperty("count").GetInt32());
            Assert.Equal(1, countJson.GetProperty("files").GetInt32());
            Assert.False(countJson.TryGetProperty("sql_graph_contract_ready", out _));
            Assert.False(countJson.TryGetProperty("sql_graph_contract_degraded_reason", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactCountJson_MixedRepoStaleSqlGraphContractIncludesDegradedStateWhenCountContainsSql()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_mixed_sql_graph_contract_count");
        try
        {
            var dbPath = CreateMixedSqlGraphContractCountFixtureDb(projectRoot);
            DowngradeMixedSqlGraphContractCountRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Target", "--db", dbPath, "--json", "--exact", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.Equal(2, json.GetProperty("files").GetInt32());
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactCountJson_MixedRepoStaleSqlGraphContractIncludesDegradedStateWhenCountContainsSql()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_mixed_sql_graph_contract_count");
        try
        {
            var dbPath = CreateMixedSqlGraphContractCountFixtureDb(projectRoot);
            DowngradeMixedSqlGraphContractCountRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Target", "--db", dbPath, "--json", "--exact", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.Equal(2, json.GetProperty("files").GetInt32());
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallees_CountJson_MixedRepoStaleSqlGraphContractIncludesDegradedStateWhenCountContainsSql()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callees_mixed_sql_graph_contract_count");
        try
        {
            var dbPath = CreateMixedSqlGraphContractCountFixtureDb(projectRoot);
            DowngradeMixedSqlGraphContractCountRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["Caller", "--db", dbPath, "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.Equal(2, json.GetProperty("files").GetInt32());
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallees_ExactJson_NormalizesBracketedSqlCallerNames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callees_sql_exact_bracketed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/sql_exact_bracketed_callee_targets.sql",
                "sql",
                """
                CREATE PROCEDURE [dbo].[fn_Target]
                AS
                BEGIN
                    SELECT 1;
                END
                GO

                CREATE PROCEDURE [sales].[fn_Target]
                AS
                BEGIN
                    EXEC [sales].[fn_Target];
                    EXEC fn_Target;
                END
                GO
                """);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkSqlGraphContractReady();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["sales.fn_Target", "--db", dbPath, "--json", "--lang", "sql", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("[sales].[fn_Target]", json.GetProperty("caller_name").GetString());
            Assert.Equal("fn_Target", json.GetProperty("callee_name").GetString());
            Assert.Equal(2, json.GetProperty("reference_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_UnsupportedLanguageWithoutMatches_PrintsGraphSupportHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_refs");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["MissingSymbol", "--db", dbPath, "--lang", "text", "--allow-unknown-lang"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("No references found.", stderr);
            Assert.Contains("call-graph queries are not indexed for 'text'", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_CountJsonKeepsSubscribeRowsVisibleByDefault()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_references_subscribe_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Publisher.cs", "csharp",
                """
                using System;

                public class Publisher
                {
                    public event EventHandler? Changed;
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Subscriber.cs", "csharp",
                """
                using System;

                public class Subscriber
                {
                    public void Hook(Publisher publisher)
                    {
                        publisher.Changed += OnChanged;
                    }

                    private void OnChanged(object? sender, EventArgs e) { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Changed", "--db", dbPath, "--json", "--count", "--lang", "csharp", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_JsonKeepsSubscribeRowsVisibleByDefault()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_subscribe_default");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Publisher.cs", "csharp",
                """
                using System;

                public class Publisher
                {
                    public event EventHandler? Changed;
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Subscriber.cs", "csharp",
                """
                using System;

                public class Subscriber
                {
                    public void Hook(Publisher publisher)
                    {
                        publisher.Changed += OnChanged;
                    }

                    private void OnChanged(object? sender, EventArgs e) { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Changed", "--db", dbPath, "--json", "--lang", "csharp", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Hook", json.GetProperty("caller_name").GetString());
            Assert.Equal("Changed", json.GetProperty("callee_name").GetString());
            Assert.Equal("subscribe", json.GetProperty("reference_kind").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
            // #501: every grouped caller row carries `reference_kinds` +
            // `has_mixed_reference_kinds`, even when the row is single-kind, so AI
            // clients never have to guess whether the field was omitted vs empty.
            // #501: グループ化された caller 行は single-kind でも必ず `reference_kinds` /
            // `has_mixed_reference_kinds` を返すため、AI クライアントは「未出力」と
            // 「空配列」を判別せずに済む。
            var kinds = json.GetProperty("reference_kinds").EnumerateArray().Select(k => k.GetString()).ToArray();
            Assert.Equal(new[] { "subscribe" }, kinds);
            Assert.False(json.GetProperty("has_mixed_reference_kinds").GetBoolean());

            var (rawExitCode, rawStdout, rawStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Changed", "--db", dbPath, "--json", "--lang", "csharp", "--exact", "--raw-kinds"],
                _jsonOptions));
            using var rawDocument = ParseJsonOutput(rawStdout);
            Assert.Equal(CommandExitCodes.Success, rawExitCode);
            Assert.Equal(string.Empty, rawStderr);
            Assert.Equal("subscribe", rawDocument.RootElement.GetProperty("reference_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_JsonKeepsRazorEventBindingsVisibleByDefault()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_razor_event_binding");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Pages"));
            File.WriteAllText(
                Path.Combine(projectRoot, "Pages", "User.razor"),
                """
                <button @onclick="HandleClick">Save</button>
                <button @onclick="@HandleClick">Save explicit</button>

                @code {
                    void HandleClick() { }
                }
                """);

            var (indexExitCode, _, _) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["HandleClick", "--db", dbPath, "--json", "--lang", "csharp", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("HandleClick", json.GetProperty("callee_name").GetString());
            Assert.Equal("subscribe", json.GetProperty("reference_kind").GetString());

            var (canonicalFilterExitCode, canonicalFilterStdout, canonicalFilterStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["HandleClick", "--db", dbPath, "--kind", "subscribe", "--json", "--lang", "csharp", "--exact"],
                _jsonOptions));
            using var canonicalFilterDocument = ParseJsonOutput(canonicalFilterStdout);
            Assert.Equal(CommandExitCodes.Success, canonicalFilterExitCode);
            Assert.Equal(string.Empty, canonicalFilterStderr);
            Assert.Equal("subscribe", canonicalFilterDocument.RootElement.GetProperty("reference_kind").GetString());

            var (rawFilterExitCode, rawFilterStdout, rawFilterStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["HandleClick", "--db", dbPath, "--kind", "razor_event_binding", "--raw-kinds", "--json", "--lang", "csharp", "--exact"],
                _jsonOptions));
            using var rawFilterDocument = ParseJsonOutput(rawFilterStdout);
            Assert.Equal(CommandExitCodes.Success, rawFilterExitCode);
            Assert.Equal(string.Empty, rawFilterStderr);
            Assert.Equal("razor_event_binding", rawFilterDocument.RootElement.GetProperty("reference_kind").GetString());

            var (referencesExitCode, referencesStdout, referencesStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["HandleClick", "--db", dbPath, "--kind", "razor_event_binding", "--lang", "csharp", "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, referencesExitCode);
            Assert.DoesNotContain("not a known reference kind", referencesStderr);
            Assert.Contains("razor_event_binding", referencesStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_SurfacesMixedReferenceKindsWhenContainerMixesCallAndSubscribe()
    {
        // #501: a single container that reaches the same callee via both `call` and
        // `subscribe` must not collapse to a lone summary label. The grouped row
        // must expose every distinct kind in JSON (`reference_kinds` /
        // `has_mixed_reference_kinds`) and the human renderer must join them with
        // `+` so operators see the mixed semantics at a glance.
        // #501: 同一コンテナが同じ callee に対して `call` と `subscribe` の両方を持つ場合、
        // グループ化された caller 行は要約ラベル 1 つに潰さず、JSON では `reference_kinds`
        // と `has_mixed_reference_kinds` で distinct kind をすべて返し、人間向け出力は
        // `+` で連結して混在していることが一目で分かるようにする。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_mixed_kind");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/MixedOwner.cs", "csharp",
                """
                using System;

                public class MixedOwner
                {
                    public event EventHandler? Changed;

                    public void SetupAndFire()
                    {
                        Changed += OnChanged;
                        Changed(this, EventArgs.Empty);
                    }

                    private void OnChanged(object? sender, EventArgs e) { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (jsonExitCode, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Changed", "--db", dbPath, "--json", "--lang", "csharp", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(jsonStdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, jsonExitCode);
            Assert.Equal(string.Empty, jsonStderr);
            Assert.Equal("SetupAndFire", json.GetProperty("caller_name").GetString());
            Assert.Equal("Changed", json.GetProperty("callee_name").GetString());
            Assert.Equal(2, json.GetProperty("reference_count").GetInt32());
            Assert.True(json.GetProperty("has_mixed_reference_kinds").GetBoolean());
            var kinds = json.GetProperty("reference_kinds").EnumerateArray().Select(k => k.GetString()).ToArray();
            Assert.Equal(new[] { "call", "subscribe" }, kinds);
            Assert.Equal(1, json.GetProperty("reference_kind_counts").GetProperty("call").GetInt32());
            Assert.Equal(1, json.GetProperty("reference_kind_counts").GetProperty("subscribe").GetInt32());
            Assert.Equal("subscribe", json.GetProperty("reference_kind").GetString());

            var (humanExitCode, humanStdout, humanStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Changed", "--db", dbPath, "--lang", "csharp", "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("call, subscribe", humanStdout);
            Assert.Contains("SetupAndFire", humanStdout);
            Assert.Contains("-> Changed (2 refs)", humanStdout);
            Assert.Contains("(1 callers in 1 files)", humanStderr);

            var (rawExitCode, rawStdout, rawStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Changed", "--db", dbPath, "--json", "--lang", "csharp", "--exact", "--raw-kinds"],
                _jsonOptions));
            using var rawDocument = ParseJsonOutput(rawStdout);
            var rawKinds = rawDocument.RootElement.GetProperty("reference_kinds").EnumerateArray().Select(k => k.GetString()).ToArray();
            Assert.Equal(CommandExitCodes.Success, rawExitCode);
            Assert.Equal(string.Empty, rawStderr);
            Assert.Equal(new[] { "call", "subscribe" }, rawKinds);
            Assert.Equal("subscribe", rawDocument.RootElement.GetProperty("reference_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_Json_CSharpTopLevelStatementsUseSyntheticTopLevelCaller()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_csharp_toplevel");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Program.cs", "csharp",
                """
                using System;

                Console.WriteLine("boot");

                void Run()
                {
                    Console.WriteLine("inside");
                }

                Run();
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Run", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("function", json.GetProperty("caller_kind").GetString());
            Assert.Equal("<top-level>", json.GetProperty("caller_name").GetString());
            Assert.Equal("Run", json.GetProperty("callee_name").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_WithExplicitKind_CSharpTopLevelStatementsUseSyntheticTopLevelCaller()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_csharp_toplevel_kind");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Program.cs", "csharp",
                """
                using System;

                Console.WriteLine("boot");

                void Run()
                {
                    Console.WriteLine("inside");
                }

                Run();
                """);
            MarkGraphAndFoldReady(dbPath);

            var (humanExitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Run", "--db", dbPath, "--lang", "csharp", "--exact-name", "--kind", "call"],
                _jsonOptions));
            var (jsonExitCode, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Run", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--kind", "call"],
                _jsonOptions));

            using var document = ParseJsonOutput(jsonStdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Equal(CommandExitCodes.Success, jsonExitCode);
            Assert.Contains("(1 callers in 1 files)", stderr);
            Assert.Equal(string.Empty, jsonStderr);
            Assert.Contains("function", stdout);
            Assert.Contains("<top-level>", stdout);
            Assert.Equal("function", json.GetProperty("caller_kind").GetString());
            Assert.Equal("<top-level>", json.GetProperty("caller_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_HumanOutput_ShowsReferenceKindPerRow()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_human_reference_kind");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/BaseWidget.cs", "csharp",
                """
                public class BaseWidget
                {
                    public BaseWidget() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/DerivedWidget.cs", "csharp",
                """
                public class DerivedWidget : BaseWidget
                {
                    public DerivedWidget() : base() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Factory.cs", "csharp",
                """
                public class Factory
                {
                    public BaseWidget Make() => new BaseWidget();
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["BaseWidget", "--db", dbPath, "--lang", "csharp"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("call         function   DerivedWidget", stdout);
            Assert.Contains("src/DerivedWidget.cs:3  -> BaseWidget (1 refs)", stdout);
            Assert.Contains("instantiate  function   Make", stdout);
            Assert.Contains("src/Factory.cs:3  -> BaseWidget (1 refs)", stdout);
            Assert.Contains("(2 callers in 2 files)", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallees_JsonKeepsSubscribeRowsVisibleByDefault()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callees_subscribe_default");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Publisher.cs", "csharp",
                """
                using System;

                public class Publisher
                {
                    public event EventHandler? Changed;
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Subscriber.cs", "csharp",
                """
                using System;

                public class Subscriber
                {
                    public void Hook(Publisher publisher)
                    {
                        publisher.Changed += OnChanged;
                    }

                    private void OnChanged(object? sender, EventArgs e) { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["Hook", "--db", dbPath, "--json", "--lang", "csharp", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Hook", json.GetProperty("caller_name").GetString());
            Assert.Equal("Changed", json.GetProperty("callee_name").GetString());
            Assert.Equal("subscribe", json.GetProperty("reference_kind").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpEnumMembersReturnIndexedReferences()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_references");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public class Outer
                {
                    public enum First { None }
                }

                public enum Nested
                {
                    A = 1,
                    B = A
                }

                public class UsesEnum
                {
                    public void Use()
                    {
                        _ = Nested.A;
                        _ = Outer.First.None;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["A", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/cases.cs", json.GetProperty("path").GetString());
            Assert.Equal("A", json.GetProperty("symbol_name").GetString());
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal("function", json.GetProperty("container_kind").GetString());
            Assert.Equal("Use", json.GetProperty("container_name").GetString());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_ReturnsPersistedHdlGraphEdges_Issue4742()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_hdl_references");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var fixtures = new[]
            {
                (
                    Path: "src/top.v",
                    Language: "verilog",
                    Content: """
                        module fifo;
                        endmodule
                        module verilog_top;
                            fifo u_fifo ();
                        endmodule
                        """,
                    SymbolName: "fifo",
                    ReferenceKind: "instantiate",
                    ContainerName: "verilog_top"),
                (
                    Path: "src/top.sv",
                    Language: "systemverilog",
                    Content: """
                        package util_pkg;
                        endpackage
                        module systemverilog_top;
                            import util_pkg::*;
                        endmodule
                        """,
                    SymbolName: "util_pkg",
                    ReferenceKind: "import",
                    ContainerName: "systemverilog_top"),
                (
                    Path: "src/top.vhd",
                    Language: "vhdl",
                    Content: """
                        entity Child is
                        end Child;
                        entity VhdlTop is
                        end VhdlTop;
                        architecture structural of VhdlTop is
                        begin
                            u_child : entity work.Child;
                        end structural;
                        """,
                    SymbolName: "Child",
                    ReferenceKind: "instantiate",
                    ContainerName: "structural"),
            };

            foreach (var fixture in fixtures)
                TestProjectHelper.InsertIndexedFile(dbPath, fixture.Path, fixture.Language, fixture.Content);
            MarkGraphAndFoldReady(dbPath);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                new DbWriter(db).MarkHdlGraphContractReady();

            foreach (var fixture in fixtures)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                    [fixture.SymbolName, "--db", dbPath, "--json", "--lang", fixture.Language, "--exact"],
                    _jsonOptions));

                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(fixture.Path, json.GetProperty("path").GetString());
                Assert.Equal(fixture.Language, json.GetProperty("lang").GetString());
                Assert.Equal(fixture.SymbolName, json.GetProperty("symbol_name").GetString());
                Assert.Equal(fixture.ReferenceKind, json.GetProperty("reference_kind").GetString());
                Assert.Equal(fixture.ContainerName, json.GetProperty("container_name").GetString());
                Assert.True(json.GetProperty("exact_index_available").GetBoolean());
                Assert.False(json.TryGetProperty("graph_degraded", out _));
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactCountJson_LargeMixedCandidateSetStillMarksEnumMemberGap()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_large_mixed_exact_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < 70; i++)
            {
                TestProjectHelper.InsertIndexedFile(dbPath, $"src/Worker{i}.cs", "csharp",
                    $$"""
                    namespace Demo;

                    public class Worker{{i}}
                    {
                        public void Ready() { }
                    }
                    """);
            }

            TestProjectHelper.InsertIndexedFile(dbPath, "src/Status.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_CSharpEnumMember_ReturnsIndexedCaller()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_enum_member_gap");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Nested
                {
                    A = 1,
                    B = A
                }

                public class UsesEnum
                {
                    public Nested Value => Nested.A;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["A", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--include-member-reads"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/cases.cs", json.GetProperty("path").GetString());
            Assert.Equal("property", json.GetProperty("caller_kind").GetString());
            Assert.Equal("Value", json.GetProperty("caller_name").GetString());
            Assert.Equal("A", json.GetProperty("callee_name").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_ZeroResultsWithoutOverride_UsesZeroSchema()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_references_exact_zero_schema");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public class Worker
                {
                    public void Ready() { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("references").GetArrayLength());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_CSharpSemicolonRecordKeepsOutsideSameLineCallsOnParent_Issue5228()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_record_reference_boundaries_5228");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                using System;
                using System.Collections.Generic;
                public sealed class AAttribute : Attribute { public AAttribute(Type value) { } }
                public static class Target { public static int Create() => 1; }
                public class Outer
                {
                    public event System.Action Changed = Target.Create; public record Á; public static int Value = Target.Create();
                }
                public class GenericOuter
                {
                    public record R<
                        [A(typeof(Dictionary<string, int>))] T>(T Value)
                    ; public static int Value = Target.Create();
                }
                public class MultilineOuter
                {
                    public record S; public void Following() { Target.Create();
                    }
                }
                public class MultilineTypeOuter
                {
                    public record T; public class FollowingType { public static int Value = Target.Create();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Create", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var references = ParseJsonLines(stdout)
                .Select(document => document.RootElement)
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(
                ["class", "class", "class", "function", "class"],
                references.Select(reference =>
                    reference.GetProperty("container_kind").GetString()).ToArray());
            Assert.Equal(
                ["Outer", "Outer", "GenericOuter", "Following", "FollowingType"],
                references.Select(reference =>
                    reference.GetProperty("container_name").GetString()).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_WithResults_StaysCleanWhenEnumMembersAlsoExist()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_references_enum_member_success_metadata");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public class Worker
                {
                    public void A() { }

                    public void Use()
                    {
                        A();
                    }
                }

                public enum Status
                {
                    A
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["A", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/cases.cs", json.GetProperty("path").GetString());
            Assert.Equal("A", json.GetProperty("symbol_name").GetString());
            Assert.Equal("Use", json.GetProperty("container_name").GetString());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_WithoutLang_MixedCallableAndEnumMember_ReturnsPrimaryHit()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_references_exact_mixed_without_lang");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "web/app.js", "javascript",
                """
                function Ready() {}

                Ready();
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/status.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("web/app.js", json.GetProperty("path").GetString());
            Assert.Equal("javascript", json.GetProperty("lang").GetString());
            Assert.Equal("Ready", json.GetProperty("symbol_name").GetString());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_WithResults_StayCleanWhenEnumMembersAlsoExist()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_enum_member_success_metadata");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public class Worker
                {
                    public void A() { }

                    public void Use()
                    {
                        A();
                    }
                }

                public enum Status
                {
                    A
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["A", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/cases.cs", json.GetProperty("path").GetString());
            Assert.Equal("Use", json.GetProperty("caller_name").GetString());
            Assert.Equal("A", json.GetProperty("callee_name").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_ZeroResultsWithoutOverride_UsesZeroSchema()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callers_exact_zero_schema");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public class Worker
                {
                    public void Ready() { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("callers").GetArrayLength());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallees_ExactJson_CSharpEnumMember_UsesZeroSchema()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callees_enum_member_gap");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Nested
                {
                    A = 1,
                    B = A
                }

                public class UsesEnum
                {
                    public Nested Value => Nested.A;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["A", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("callees").GetArrayLength());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallees_ExactJson_ZeroResultsWithoutOverride_UsesZeroSchema()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callees_exact_zero_schema");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public class Worker
                {
                    public void Ready() { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("callees").GetArrayLength());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallees_ExactJson_WithResults_StayCleanWhenEnumMembersAlsoExist()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_callees_enum_member_success_metadata");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public class Worker
                {
                    public void A()
                    {
                        B();
                    }

                    public void B() { }
                }

                public enum Status
                {
                    A
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["A", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/cases.cs", json.GetProperty("path").GetString());
            Assert.Equal("A", json.GetProperty("caller_name").GetString());
            Assert.Equal("B", json.GetProperty("callee_name").GetString());
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CrossLanguageMixedHitDoesNotForceCSharpGraphLanguage()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_references_cross_language_mixed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "web/app.js", "javascript",
                """
                export function Ready() {}

                Ready();
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/status.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("web/app.js", json.GetProperty("path").GetString());
            Assert.Equal("javascript", json.GetProperty("lang").GetString());
            Assert.Equal("Ready", json.GetProperty("symbol_name").GetString());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpNonEnumQualifiedMemberAccessDoesNotLeakAsEnumMemberReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_false_positive");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum EnumHolder
                {
                    A = 1
                }

                public static class Values
                {
                    public static int Alpha = 1;
                }

                public class UsesValues
                {
                    public int Read()
                    {
                        return Values.A;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["A", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("references").GetArrayLength());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpEnumMemberRepeatedAliasNamesUseNearestAliasScope()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_repeated_alias_scope");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo
                {
                    public enum Status
                    {
                        Ready
                    }

                    public static class Values
                    {
                        public static int Ready = 1;
                    }
                }

                namespace B
                {
                    using Alias = Demo.Values;

                    public class UsesValues
                    {
                        public int Read()
                        {
                            return Alias.Ready;
                        }
                    }
                }

                namespace C
                {
                    using Alias = Demo.Status;

                    public class UsesEnum
                    {
                        public Demo.Status Read()
                        {
                            return Alias.Ready;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--kind", "member_read"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(35, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSiblingAliasMemberReadsRemainDeterministic_Issue4894()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_alias_rebinding");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo
                {
                    public enum Status
                    {
                        Ready
                    }

                    public static class Values
                    {
                        public static int Ready = 1;
                    }
                }

                namespace B
                {
                    using Alias = Demo.Status;

                    public class UsesEnum
                    {
                        public Demo.Status Read()
                        {
                            return Alias.Ready;
                        }
                    }
                }

                namespace C
                {
                    using Alias = Demo.Values;

                    public class UsesValues
                    {
                        public int Read()
                        {
                            return Alias.Ready;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--kind", "member_read"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(35, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpInstancePropertyShadowDoesNotHideStaticEnumReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_static_method_instance_property");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Holder Status { get; } = new();

                    public static Demo.Status Read()
                    {
                        return Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(19, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpPropertyAccessorLocalShadowingDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_property_accessor_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Value
                    {
                        get
                        {
                            Holder Status = new();
                            _ = Status.Ready;
                            return Demo.Status.Ready;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Value", json.GetProperty("container_name").GetString());
            Assert.Equal("property", json.GetProperty("container_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpGetterLocalShadowingDoesNotLeakIntoSetter()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_property_accessor_scope_end");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Status Value
                    {
                        get
                        {
                            Holder Status = new();
                            _ = Status.Ready;
                            return Demo.Status.Ready;
                        }
                        set
                        {
                            _ = Status.Ready;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--limit", "10"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal([21, 25], rows.Select(row => row.RootElement.GetProperty("line").GetInt32()).OrderBy(line => line).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpOutDeclarationShadowingDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_out_declaration_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    private static bool TryGet(out Holder holder)
                    {
                        holder = new Holder();
                        return true;
                    }

                    public Demo.Status Read()
                    {
                        if (TryGet(out Holder Status))
                        {
                            _ = Status.Ready;
                        }

                        return Demo.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpCatchShadowingDoesNotLeakAfterCatchBlock()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_catch_scope_end");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                using System;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Status Read()
                    {
                        try
                        {
                            throw new Exception();
                        }
                        catch (Exception Status)
                        {
                            _ = Status.Message;
                        }

                        return Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStatementShadowingDoesNotLeakAfterUsingBlock()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_using_scope_end");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                using System;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder : IDisposable
                {
                    public int Ready { get; set; }

                    public void Dispose()
                    {
                    }
                }

                public sealed class Uses
                {
                    public Status Read(bool flag)
                    {
                        if (flag)
                        {
                            using (Holder Status = new())
                            {
                                _ = Status.Ready;
                            }
                        }

                        return Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryBoundariesAndVisualBasicQuerySyntaxShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_query_boundary_workspace");
        try
        {
            var csharpRoot = Path.Combine(projectRoot, "src", "csharp");
            var visualBasicRoot = Path.Combine(projectRoot, "src", "vb");
            Directory.CreateDirectory(csharpRoot);
            Directory.CreateDirectory(visualBasicRoot);

            File.WriteAllText(
                Path.Combine(csharpRoot, "range-member-select.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace QueryFixtures.MemberSelect;

                public enum MemberSelectStatus
                {
                    MemberSelectReady
                }

                public sealed class Holder
                {
                    public int MemberSelectReady { get; set; }
                    public int select { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        return from MemberSelectStatus in items
                               orderby MemberSelectStatus.select, items.Count()
                               select MemberSelectStatus.MemberSelectReady;
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(csharpRoot, "parenthesized-group-by.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace QueryFixtures.ParenthesizedGroupBy;

                public enum GroupByStatus
                {
                    GroupByReady
                }

                public static class Sink
                {
                    public static GroupByStatus Pick(object left, GroupByStatus right) => right;
                }

                public sealed class Holder
                {
                    public int GroupByReady { get; set; }
                }

                public sealed class Uses
                {
                    public GroupByStatus Read(IEnumerable<Holder> items)
                    {
                        return Sink.Pick(from GroupByStatus in items group(GroupByStatus.GroupByReady) by items.Count(), GroupByStatus.GroupByReady);
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(csharpRoot, "await-keyword-local.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;
                using System.Threading.Tasks;

                namespace QueryFixtures.AwaitKeywordLocal;

                public enum AwaitStatus
                {
                    AwaitReady
                }

                public sealed class Holder
                {
                    public int AwaitReady { get; set; }
                }

                public sealed class Uses
                {
                    public async Task<IEnumerable<int>> Read(IEnumerable<Holder> items)
                    {
                        static async Task<int> select(IEnumerable<Holder> xs) => await Task.FromResult(xs.Count());
                        return from AwaitStatus in items
                               orderby await select(items), items.Count()
                               select AwaitStatus.AwaitReady;
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(csharpRoot, "nullable-type-suffix.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace QueryFixtures.NullableTypeSuffix;

                public enum NullableStatus
                {
                    NullableReady
                }

                public static class Sink
                {
                    public static NullableStatus Pick(object left, NullableStatus right) => right;
                }

                public sealed class Uses
                {
                    public NullableStatus Read(IEnumerable<object> items, object value)
                    {
                        return Sink.Pick(from NullableStatus in items
                                         let cast = value as NullableStatus?
                                         select(NullableStatus.NullableReady),
                                         NullableStatus.NullableReady);
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(csharpRoot, "ternary-order-by.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace QueryFixtures.TernaryOrderBy;

                public enum TernaryStatus
                {
                    TernaryReady
                }

                public static class Sink
                {
                    public static TernaryStatus Pick(object left, TernaryStatus right) => right;
                }

                public sealed class Uses
                {
                    public TernaryStatus Read(IEnumerable<object> items, bool flag, int left, int right)
                    {
                        return Sink.Pick(from TernaryStatus in items
                                         orderby (flag ? left : right)
                                         select(TernaryStatus.TernaryReady),
                                         TernaryStatus.TernaryReady);
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(csharpRoot, "keyword-named-local.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace QueryFixtures.KeywordNamedLocal;

                public enum KeywordLocalStatus
                {
                    KeywordLocalReady
                }

                public static class Sink
                {
                    public static KeywordLocalStatus Pick(object left, KeywordLocalStatus right) => right;
                }

                public sealed class Uses
                {
                    public KeywordLocalStatus Read(IEnumerable<object> items)
                    {
                        const int Select = 1;
                        return Sink.Pick(from KeywordLocalStatus in items
                                         orderby (Select)
                                         select(KeywordLocalStatus.KeywordLocalReady),
                                         KeywordLocalStatus.KeywordLocalReady);
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(visualBasicRoot, "query-syntax.vb"),
                """
                Imports System.Collections.Generic
                Imports System.Linq

                Namespace QueryFixtures.VisualBasic
                    Public Module QueryHelpers
                        Public Function VisualBasicQueryCall(value As Integer) As Integer
                            Return value
                        End Function
                    End Module

                    Public NotInheritable Class Uses
                        Public Function Read(items As IEnumerable(Of Integer)) As IEnumerable(Of Integer)
                            Return From item In items
                                   Select QueryHelpers.VisualBasicQueryCall(item)
                        End Function
                    End Class
                End Namespace
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            void AssertNoReferences(string query)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(query, dbPath, "csharp");
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, json.GetProperty("count").GetInt32());
                Assert.Empty(json.GetProperty("references").EnumerateArray());
            }

            JsonDocument RunSingleReference(string query, string language = "csharp")
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(query, dbPath, language);
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                return Assert.Single(ParseJsonLines(stdout));
            }

            AssertNoReferences("MemberSelectReady");
            AssertNoReferences("AwaitReady");

            using var groupByDocument = RunSingleReference("GroupByReady");
            var groupByRow = groupByDocument.RootElement;
            Assert.Equal("GroupByReady", groupByRow.GetProperty("symbol_name").GetString());
            Assert.Equal("Read", groupByRow.GetProperty("container_name").GetString());
            Assert.Contains("GroupByStatus.GroupByReady", groupByRow.GetProperty("context").GetString(), StringComparison.Ordinal);

            using var nullableDocument = RunSingleReference("NullableReady");
            var nullableRow = nullableDocument.RootElement;
            Assert.Equal("NullableReady", nullableRow.GetProperty("symbol_name").GetString());
            Assert.Equal("Read", nullableRow.GetProperty("container_name").GetString());
            Assert.Contains("NullableStatus.NullableReady", nullableRow.GetProperty("context").GetString(), StringComparison.Ordinal);

            using var ternaryDocument = RunSingleReference("TernaryReady");
            var ternaryRow = ternaryDocument.RootElement;
            Assert.Equal("TernaryReady", ternaryRow.GetProperty("symbol_name").GetString());
            Assert.Equal("Read", ternaryRow.GetProperty("container_name").GetString());

            using var keywordLocalDocument = RunSingleReference("KeywordLocalReady");
            var keywordLocalRow = keywordLocalDocument.RootElement;
            Assert.Equal("KeywordLocalReady", keywordLocalRow.GetProperty("symbol_name").GetString());
            Assert.Equal("Read", keywordLocalRow.GetProperty("container_name").GetString());

            using var visualBasicDocument = RunSingleReference("VisualBasicQueryCall", "vb");
            var visualBasicRow = visualBasicDocument.RootElement;
            Assert.Equal("VisualBasicQueryCall", visualBasicRow.GetProperty("symbol_name").GetString());
            Assert.Equal("call", visualBasicRow.GetProperty("reference_kind").GetString());
            Assert.Equal(14, visualBasicRow.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpOrderByCommaBoundariesShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_orderby_comma_boundaries");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum AnonymousStatus
                {
                    AnonymousReady
                }

                public sealed class AnonymousHolder
                {
                    public int AnonymousReady { get; set; }
                }

                public enum NestedStatus
                {
                    NestedReady
                }

                public sealed class NestedHolder
                {
                    public int NestedReady { get; set; }
                }

                public enum ParenthesizedStatus
                {
                    ParenthesizedReady
                }

                public sealed class ParenthesizedHolder
                {
                    public int ParenthesizedReady { get; set; }
                }

                public enum ScopeStatus
                {
                    ScopeReady
                }

                public sealed class ScopeHolder
                {
                    public int ScopeReady { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> ReadAnonymous(IEnumerable<AnonymousHolder> items)
                    {
                        return from AnonymousStatus in items
                               orderby new { X = AnonymousStatus.AnonymousReady, Y = items.Count() }, items.Count()
                               select AnonymousStatus.AnonymousReady;
                    }

                    public IEnumerable<int> ReadNested(IEnumerable<NestedHolder> items, IEnumerable<int> others)
                    {
                        return from NestedStatus in items
                               let nested = from x in others select x
                               orderby items.Count(), nested.Count()
                               select NestedStatus.NestedReady;
                    }

                    public IEnumerable<int> ReadParenthesized(IEnumerable<ParenthesizedHolder> items)
                    {
                        static int select(IEnumerable<ParenthesizedHolder> xs) => xs.Count();
                        return from ParenthesizedStatus in items
                               orderby select(items), items.Count()
                               select ParenthesizedStatus.ParenthesizedReady;
                    }

                    public IEnumerable<int> ReadScope(IEnumerable<ScopeHolder> items)
                    {
                        return from ScopeStatus in items
                               orderby ScopeStatus, items.Count()
                               select ScopeStatus.ScopeReady;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertNoReferences("AnonymousReady");
            AssertNoReferences("NestedReady");
            AssertNoReferences("ParenthesizedReady");
            AssertNoReferences("ScopeReady");

            void AssertNoReferences(string query)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(query, dbPath, "csharp");
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.True(json.TryGetProperty("count", out var count), stdout);
                Assert.Equal(0, count.GetInt32());
                Assert.Empty(json.GetProperty("references").EnumerateArray());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpQueryKeywordNamedLocalFunctionInSelectExpressionPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_keyword_local_function");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(IEnumerable<int> left, Status right) => right;
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<Holder> items)
                    {
                        static int from(IEnumerable<Holder> xs) => xs.Count();
                        return Sink.Pick(from Status in items select from(items), Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Ready", dbPath, "csharp");

            var rows = ParseJsonLines(stdout);
            var row = Assert.Single(rows).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
            Assert.Equal(26, row.GetProperty("line").GetInt32());
            Assert.Contains("Status.Ready", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpParenthesizedQueryTerminalsShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_parenthesized_query_terminals");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "terminal-select.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<Holder> items)
                    {
                        return Sink.Pick(from Status in items select(Status.Ready), Status.Ready);
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "group-by.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public static class Sink
                {
                    public static object Pick(object left, Status right) => right;
                }

                public sealed class Uses
                {
                    public object Read(IEnumerable<Holder> items)
                    {
                        return Sink.Pick(from Status in items group Status.Ready by items.Count(), Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertSingleReference("src/terminal-select.cs", expectedLine: null);
            AssertSingleReference("src/group-by.cs", expectedLine: 25);

            void AssertSingleReference(string path, int? expectedLine)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    "Ready", dbPath, "csharp", true, "--path", path);
                var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
                Assert.Equal("Read", row.GetProperty("container_name").GetString());
                Assert.Contains("Status.Ready", row.GetProperty("context").GetString(), StringComparison.Ordinal);
                if (expectedLine.HasValue)
                    Assert.Equal(expectedLine.Value, row.GetProperty("line").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpOrderByTernaryOperatorsShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_orderby_ternary_operators");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum GreaterStatus
                {
                    GreaterReady
                }

                public sealed class GreaterHolder
                {
                    public int GreaterReady { get; set; }
                }

                public enum LessStatus
                {
                    LessReady
                }

                public sealed class LessHolder
                {
                    public int LessReady { get; set; }
                }

                public enum BangStatus
                {
                    BangReady
                }

                public sealed class BangHolder
                {
                    public int BangReady { get; set; }
                }

                public enum MultilineStatus
                {
                    MultilineReady
                }

                public sealed class MultilineHolder
                {
                    public int MultilineReady { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> ReadGreater(IEnumerable<GreaterHolder> items)
                    {
                        static int select(IEnumerable<GreaterHolder> xs) => xs.Count();
                        return from GreaterStatus in items
                               orderby items.Count() > select(items) ? 1 : 0, items.Count()
                               select GreaterStatus.GreaterReady;
                    }

                    public IEnumerable<int> ReadLess(IEnumerable<LessHolder> items)
                    {
                        static int select(IEnumerable<LessHolder> xs) => xs.Count();
                        return from LessStatus in items
                               orderby items.Count() < select(items) ? 1 : 0, items.Count()
                               select LessStatus.LessReady;
                    }

                    public IEnumerable<int> ReadBang(IEnumerable<BangHolder> items)
                    {
                        static bool select(IEnumerable<BangHolder> xs) => xs.Any();
                        return from BangStatus in items
                               orderby ! select(items) ? 1 : 0, items.Count()
                               select BangStatus.BangReady;
                    }

                    public IEnumerable<int> ReadMultiline(IEnumerable<MultilineHolder> items)
                    {
                        static int select(IEnumerable<MultilineHolder> xs) => xs.Count();
                        return from MultilineStatus in items
                               orderby items.Count() >
                                       select
                                       (items) ? 1 : 0, items.Count()
                               select MultilineStatus.MultilineReady;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertNoReferences("GreaterReady");
            AssertNoReferences("LessReady");
            AssertNoReferences("BangReady");
            AssertNoReferences("MultilineReady");

            void AssertNoReferences(string query)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(query, dbPath, "csharp");
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, json.GetProperty("count").GetInt32());
                Assert.Empty(json.GetProperty("references").EnumerateArray());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpCommentSeparatedAwaitBeforeQueryKeywordNamedLocalFunctionInOrderByDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_await_local_function_comment_gap");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;
                using System.Threading.Tasks;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public async Task<IEnumerable<int>> Read(IEnumerable<Holder> items)
                    {
                        static async Task<int> select(IEnumerable<Holder> xs) => await Task.FromResult(xs.Count());
                        return from Status in items
                               orderby await select /*comment*/ (items), items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Ready", dbPath, "csharp");

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Empty(json.GetProperty("references").EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpPostfixQueryTerminalsShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_postfix_query_terminals");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "null-forgiving.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                    public static Holder? Maybe(Holder value) => value;
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<Holder> items)
                    {
                        return Sink.Pick(from Status in items
                                         let alias = Sink.Maybe(Status)!
                                         select(Status.Ready),
                                         Status.Ready);
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "increment.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<object> items, int counter)
                    {
                        return Sink.Pick(from Status in items
                                         let n = counter++
                                         select(Status.Ready),
                                         Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertSingleReference("src/null-forgiving.cs");
            AssertSingleReference("src/increment.cs");

            void AssertSingleReference(string path)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    "Ready", dbPath, "csharp", true, "--path", path);
                var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
                Assert.Equal("Read", row.GetProperty("container_name").GetString());
                Assert.Contains("Status.Ready", row.GetProperty("context").GetString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpThrowExpressionOrderByBoundariesShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_throw_expression_orderby_boundaries");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum SelectStatus
                {
                    SelectReady
                }

                public sealed class SelectHolder
                {
                    public int SelectReady { get; set; }
                }

                public enum GroupStatus
                {
                    GroupReady
                }

                public sealed class GroupHolder
                {
                    public int GroupReady { get; set; }
                }

                public enum MultilineGroupStatus
                {
                    MultilineGroupReady
                }

                public sealed class MultilineGroupHolder
                {
                    public int MultilineGroupReady { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> ReadSelect(IEnumerable<SelectHolder> items)
                    {
                        static System.Exception select(IEnumerable<SelectHolder> xs) => new System.Exception(xs.Count().ToString());
                        return from SelectStatus in items
                               orderby items.Count() > 0 ? throw select(items) : 0, items.Count()
                               select SelectStatus.SelectReady;
                    }

                    public IEnumerable<int> ReadGroup(IEnumerable<GroupHolder> items)
                    {
                        static System.Exception group(IEnumerable<GroupHolder> xs) => new System.Exception(xs.Count().ToString());
                        return from GroupStatus in items
                               orderby items.Count() > 0 ? throw group(items) : 0, items.Count()
                               select GroupStatus.GroupReady;
                    }

                    public IEnumerable<int> ReadMultilineGroup(IEnumerable<MultilineGroupHolder> items)
                    {
                        static System.Exception group(IEnumerable<MultilineGroupHolder> xs) => new System.Exception(xs.Count().ToString());
                        return from MultilineGroupStatus in items
                               orderby items.Count() > 0 ? throw
                                       group
                                       (items) : null, items.Count()
                               select MultilineGroupStatus.MultilineGroupReady;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertNoReferences("SelectReady");
            AssertNoReferences("GroupReady");
            AssertNoReferences("MultilineGroupReady");

            void AssertNoReferences(string query)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(query, dbPath, "csharp");
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, json.GetProperty("count").GetInt32());
                Assert.Empty(json.GetProperty("references").EnumerateArray());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_StylesheetAndSqlFixturesShareIndexedWorkspace()
    {
        // These independent non-C# reference contracts share one CLI index lifecycle. Distinct
        // SQL sentinels keep every former fixture count and line assertion independently useful.
        // 独立した非 C# reference 契約で CLI index lifecycle を共有し、固有 SQL sentinel により
        // 従来の fixture ごとの件数・行 assertion の診断性を維持する。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_stylesheet_sql_references");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "styles"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "styles", "theme.scss"),
                """
                $primary: #3366cc;
                $spacing-base: 8px;

                @mixin rounded($radius) {
                  border-radius: $radius;
                }

                %button-base {
                  padding: 4px;
                }

                .button {
                  color: $primary;
                  padding: $spacing-base * 2;
                  @include rounded(4px);
                }

                .card {
                  @extend %button-base;
                  border: 1px solid $primary;
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "merge-hints.sql"),
                """
                MERGE INTO #merge_audit_log
                WITH (INDEX(ix_merge_audit_log), HOLDLOCK) AS t
                USING merge_staging_log AS s
                ON t.id = s.id
                WHEN MATCHED THEN
                    UPDATE SET action = s.action;
                MERGE #merge_archive_log
                WITH (HOLDLOCK) AS u
                USING merge_staging_archive AS v
                ON u.id = v.id
                WHEN MATCHED THEN
                    UPDATE SET action = v.action;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "non-ascii.sql"),
                """
                SELECT * FROM ユーザー;
                INSERT INTO ユーザー (id) VALUES (1);
                UPDATE ユーザー SET id = 2;
                DELETE FROM ユーザー;
                TRUNCATE TABLE ユーザー;
                CALL ユーザー;
                EXEC ユーザー;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "double-quoted-dynamic.sql"),
                """
                SET @sql = "SELECT * FROM quoted_visible_users";
                EXECUTE IMMEDIATE @sql;
                SELECT * FROM "quoted_visible_users";
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "temp-body-boundary.sql"),
                """
                CREATE PROCEDURE dbo.ReadTemp AS
                BEGIN
                    SELECT * FROM #body_later_temp;
                END;
                GO
                CREATE PROCEDURE dbo.EstablishTemp AS
                BEGIN
                    SELECT id INTO #body_later_temp FROM body_users;
                END;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "escaped-single-quotes.sql"),
                """
                SELECT 'abc\' FROM escaped_phantom';
                SELECT 'abc'' FROM escaped_still_phantom';
                SELECT * FROM escaped_users # comment with escaped_comment_phantom;
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            string RunSuccessfulReferences(string query, string language = "sql")
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(query, dbPath, language);
                Assert.True(
                    exitCode == CommandExitCodes.Success,
                    $"references query '{query}' failed with exit code {exitCode}: {stderr}");
                Assert.True(
                    string.IsNullOrEmpty(stderr),
                    $"references query '{query}' wrote stderr: {stderr}");
                return stdout;
            }

            var primaryRows = ParseJsonLines(RunSuccessfulReferences("$primary", "css"));
            var spacingRows = ParseJsonLines(RunSuccessfulReferences("spacing-base", "css"));
            var buttonRows = ParseJsonLines(RunSuccessfulReferences("%button-base", "css"));
            var radiusRows = ParseJsonLines(RunSuccessfulReferences("radius", "css"));

            Assert.Equal(2, primaryRows.Count);
            Assert.All(primaryRows, row => Assert.Equal("primary", row.RootElement.GetProperty("symbol_name").GetString()));
            Assert.All(primaryRows, row => Assert.Equal("call", row.RootElement.GetProperty("reference_kind").GetString()));

            var spacingRow = Assert.Single(spacingRows);
            Assert.Equal("spacing-base", spacingRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("call", spacingRow.RootElement.GetProperty("reference_kind").GetString());

            var buttonRow = Assert.Single(buttonRows);
            Assert.Equal("%button-base", buttonRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("call", buttonRow.RootElement.GetProperty("reference_kind").GetString());

            var radiusRow = Assert.Single(radiusRows);
            Assert.Equal("radius", radiusRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("call", radiusRow.RootElement.GetProperty("reference_kind").GetString());

            foreach (var expectedName in new[]
            {
                "#merge_audit_log",
                "merge_staging_log",
                "#merge_archive_log",
                "merge_staging_archive",
            })
            {
                var row = Assert.Single(ParseJsonLines(RunSuccessfulReferences(expectedName)));
                Assert.Equal(expectedName, row.RootElement.GetProperty("symbol_name").GetString());
                Assert.Equal("reference", row.RootElement.GetProperty("reference_kind").GetString());
            }

            var nonAsciiRows = ParseJsonLines(RunSuccessfulReferences("ユーザー"));
            Assert.Equal(7, nonAsciiRows.Count);
            Assert.Equal(5, nonAsciiRows.Count(row => row.RootElement.GetProperty("reference_kind").GetString() == "reference"));
            Assert.Equal(2, nonAsciiRows.Count(row => row.RootElement.GetProperty("reference_kind").GetString() == "call"));
            Assert.All(nonAsciiRows, row => Assert.Equal("ユーザー", row.RootElement.GetProperty("symbol_name").GetString()));

            var quotedRows = ParseJsonLines(RunSuccessfulReferences("quoted_visible_users"));
            var quotedRow = Assert.Single(quotedRows);
            Assert.Equal("quoted_visible_users", quotedRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal(3, quotedRow.RootElement.GetProperty("line").GetInt32());

            var bodyRows = ParseJsonLines(RunSuccessfulReferences("#body_later_temp"));
            var bodyRow = Assert.Single(bodyRows);
            Assert.Equal("#body_later_temp", bodyRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal(8, bodyRow.RootElement.GetProperty("line").GetInt32());

            using var escapedPhantomDocument = ParseJsonOutput(RunSuccessfulReferences("escaped_phantom"));
            Assert.Equal(0, escapedPhantomDocument.RootElement.GetProperty("count").GetInt32());

            var escapedRows = ParseJsonLines(RunSuccessfulReferences("escaped_users"));
            var escapedRow = Assert.Single(escapedRows);
            Assert.Equal("escaped_users", escapedRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal(3, escapedRow.RootElement.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_SqlModifierPrefixedObjectsResolveRealNames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_modifier_prefixed_objects");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                SELECT TOP (10) * FROM top_users;
                UPDATE TOP (10) audit_log SET action = 'done';
                DELETE TOP (5) FROM audit_log;
                SELECT * FROM ONLY public.users;
                UPDATE ONLY public.users SET active = true;
                SELECT * FROM LATERAL fn_users(42);
                MERGE TOP (5) audit_log AS t USING staging_log AS s ON t.id = s.id WHEN MATCHED THEN UPDATE SET action = s.action;
                INSERT TOP (10) INTO inserted_log (action) VALUES ('done');
                INSERT TOP (2) INTO #inserted_batch (action) VALUES ('queued');
                SELECT * FROM #inserted_batch;
                MERGE TOP (5) #batch_log AS u USING staging_batch AS v ON u.id = v.id WHEN MATCHED THEN UPDATE SET action = v.action;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (auditExitCode, auditStdout, auditStderr) = RunReferencesInProcess("audit_log", dbPath);
            var (insertedExitCode, insertedStdout, insertedStderr) = RunReferencesInProcess("inserted_log", dbPath);
            var (insertedBatchExitCode, insertedBatchStdout, insertedBatchStderr) = RunReferencesInProcess("#inserted_batch", dbPath);
            var (batchExitCode, batchStdout, batchStderr) = RunReferencesInProcess("#batch_log", dbPath);
            var (batchSourceExitCode, batchSourceStdout, batchSourceStderr) = RunReferencesInProcess("staging_batch", dbPath);
            var (topUsersExitCode, topUsersStdout, topUsersStderr) = RunReferencesInProcess("top_users", dbPath);
            var (usersExitCode, usersStdout, usersStderr) = RunReferencesInProcess("users", dbPath);
            var (fnExitCode, fnStdout, fnStderr) = RunReferencesInProcess("fn_users", dbPath);
            var (topExitCode, topStdout, topStderr) = RunReferencesInProcess("TOP", dbPath);
            var (onlyExitCode, onlyStdout, onlyStderr) = RunReferencesInProcess("ONLY", dbPath);
            var (lateralExitCode, lateralStdout, lateralStderr) = RunReferencesInProcess("LATERAL", dbPath);

            var auditRows = ParseJsonLines(auditStdout);
            var insertedRows = ParseJsonLines(insertedStdout);
            var insertedBatchRows = ParseJsonLines(insertedBatchStdout);
            var batchRows = ParseJsonLines(batchStdout);
            var batchSourceRows = ParseJsonLines(batchSourceStdout);
            var topUsersRows = ParseJsonLines(topUsersStdout);
            var usersRows = ParseJsonLines(usersStdout);
            var fnRows = ParseJsonLines(fnStdout);
            using var topDocument = ParseJsonOutput(topStdout);
            using var onlyDocument = ParseJsonOutput(onlyStdout);
            using var lateralDocument = ParseJsonOutput(lateralStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, auditExitCode);
            Assert.Equal(string.Empty, auditStderr);
            Assert.Equal(3, auditRows.Count);
            Assert.All(auditRows, row => Assert.Equal("audit_log", row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, insertedExitCode);
            Assert.Equal(string.Empty, insertedStderr);
            var insertedRow = Assert.Single(insertedRows);
            Assert.Equal("inserted_log", insertedRow.RootElement.GetProperty("symbol_name").GetString());

            Assert.Equal(CommandExitCodes.Success, insertedBatchExitCode);
            Assert.Equal(string.Empty, insertedBatchStderr);
            Assert.Equal(2, insertedBatchRows.Count);
            Assert.All(insertedBatchRows, row => Assert.Equal("#inserted_batch", row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, batchExitCode);
            Assert.Equal(string.Empty, batchStderr);
            var batchRow = Assert.Single(batchRows);
            Assert.Equal("#batch_log", batchRow.RootElement.GetProperty("symbol_name").GetString());

            Assert.Equal(CommandExitCodes.Success, batchSourceExitCode);
            Assert.Equal(string.Empty, batchSourceStderr);
            var batchSourceRow = Assert.Single(batchSourceRows);
            Assert.Equal("staging_batch", batchSourceRow.RootElement.GetProperty("symbol_name").GetString());

            Assert.Equal(CommandExitCodes.Success, topUsersExitCode);
            Assert.Equal(string.Empty, topUsersStderr);
            var topUsersRow = Assert.Single(topUsersRows);
            Assert.Equal("top_users", topUsersRow.RootElement.GetProperty("symbol_name").GetString());

            Assert.Equal(CommandExitCodes.Success, usersExitCode);
            Assert.Equal(string.Empty, usersStderr);
            Assert.Equal(2, usersRows.Count);
            Assert.All(usersRows, row => Assert.Equal("users", row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, fnExitCode);
            Assert.Equal(string.Empty, fnStderr);
            var fnRow = Assert.Single(fnRows);
            Assert.Equal("fn_users", fnRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("call", fnRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, topExitCode);
            Assert.Equal(string.Empty, topStderr);
            Assert.Equal(0, topDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, onlyExitCode);
            Assert.Equal(string.Empty, onlyStderr);
            Assert.Equal(0, onlyDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, lateralExitCode);
            Assert.Equal(string.Empty, lateralStderr);
            Assert.Equal(0, lateralDocument.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_SqlTruncateTargetsShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_truncate_targets");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "targets.sql"),
                """
                TRUNCATE TABLE ONLY public.users;
                TRUNCATE TABLE audit_log, archived_log;
                TRUNCATE TABLE [dbo].[users], `analytics`.`logs`, "public"."accounts";
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "temp-targets.sql"),
                """
                TRUNCATE TABLE #truncate_temp;
                SELECT * FROM #truncate_temp;
                SELECT * FROM #future_temp;
                TRUNCATE TABLE #future_temp;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var targetPathArgs = new[] { "--path", "sql/targets.sql" };
            var (usersExitCode, usersStdout, usersStderr) = RunReferencesInProcess("users", dbPath, "sql", true, targetPathArgs);
            var (archivedExitCode, archivedStdout, archivedStderr) = RunReferencesInProcess("archived_log", dbPath, "sql", true, targetPathArgs);
            var (logsExitCode, logsStdout, logsStderr) = RunReferencesInProcess("logs", dbPath, "sql", true, targetPathArgs);
            var (accountsExitCode, accountsStdout, accountsStderr) = RunReferencesInProcess("accounts", dbPath, "sql", true, targetPathArgs);
            var (onlyExitCode, onlyStdout, onlyStderr) = RunReferencesInProcess("ONLY", dbPath, "sql", true, targetPathArgs);
            var (qualifiedExitCode, qualifiedStdout, qualifiedStderr) = RunReferencesInProcess("public.users", dbPath, "sql", true, targetPathArgs);
            var (mangledBracketExitCode, mangledBracketStdout, mangledBracketStderr) = RunReferencesInProcess("dbo].[users", dbPath, "sql", true, targetPathArgs);
            var (mangledBacktickExitCode, mangledBacktickStdout, mangledBacktickStderr) = RunReferencesInProcess("analytics`.`logs", dbPath, "sql", true, targetPathArgs);
            var (mangledDoubleQuoteExitCode, mangledDoubleQuoteStdout, mangledDoubleQuoteStderr) = RunReferencesInProcess("public\".\"accounts", dbPath, "sql", true, targetPathArgs);

            var usersRows = ParseJsonLines(usersStdout);
            var archivedRows = ParseJsonLines(archivedStdout);
            var logsRows = ParseJsonLines(logsStdout);
            var accountsRows = ParseJsonLines(accountsStdout);
            using var onlyDocument = ParseJsonOutput(onlyStdout);
            using var qualifiedDocument = ParseJsonOutput(qualifiedStdout);
            using var mangledBracketDocument = ParseJsonOutput(mangledBracketStdout);
            using var mangledBacktickDocument = ParseJsonOutput(mangledBacktickStdout);
            using var mangledDoubleQuoteDocument = ParseJsonOutput(mangledDoubleQuoteStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, usersExitCode);
            Assert.Equal(string.Empty, usersStderr);
            Assert.Equal(2, usersRows.Count);

            Assert.Equal(CommandExitCodes.Success, archivedExitCode);
            Assert.Equal(string.Empty, archivedStderr);
            Assert.Single(archivedRows);

            Assert.Equal(CommandExitCodes.Success, logsExitCode);
            Assert.Equal(string.Empty, logsStderr);
            Assert.Single(logsRows);

            Assert.Equal(CommandExitCodes.Success, accountsExitCode);
            Assert.Equal(string.Empty, accountsStderr);
            Assert.Single(accountsRows);

            Assert.Equal(CommandExitCodes.Success, onlyExitCode);
            Assert.Equal(string.Empty, onlyStderr);
            Assert.Equal(0, onlyDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, qualifiedExitCode);
            Assert.Equal(string.Empty, qualifiedStderr);
            Assert.Equal(1, qualifiedDocument.RootElement.GetProperty("line").GetInt32());
            Assert.Equal("users", qualifiedDocument.RootElement.GetProperty("symbol_name").GetString());

            Assert.Equal(CommandExitCodes.Success, mangledBracketExitCode);
            Assert.Equal(string.Empty, mangledBracketStderr);
            Assert.Equal(0, mangledBracketDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, mangledBacktickExitCode);
            Assert.Equal(string.Empty, mangledBacktickStderr);
            Assert.Equal(0, mangledBacktickDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, mangledDoubleQuoteExitCode);
            Assert.Equal(string.Empty, mangledDoubleQuoteStderr);
            Assert.Equal(0, mangledDoubleQuoteDocument.RootElement.GetProperty("count").GetInt32());

            var (truncateExitCode, truncateStdout, truncateStderr) = RunReferencesInProcess(
                "#truncate_temp", dbPath, "sql", true, "--path", "sql/temp-targets.sql");
            var (futureExitCode, futureStdout, futureStderr) = RunReferencesInProcess(
                "#future_temp", dbPath, "sql", true, "--path", "sql/temp-targets.sql");
            var truncateRows = ParseJsonLines(truncateStdout);
            var futureRows = ParseJsonLines(futureStdout);

            Assert.Equal(CommandExitCodes.Success, truncateExitCode);
            Assert.Equal(string.Empty, truncateStderr);
            Assert.Equal(2, truncateRows.Count);
            Assert.All(truncateRows, row => Assert.Equal(
                "#truncate_temp",
                row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, futureExitCode);
            Assert.Equal(string.Empty, futureStderr);
            var futureRow = Assert.Single(futureRows);
            Assert.Equal("#future_temp", futureRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal(4, futureRow.RootElement.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_SqlUsingAndMergeSourcesShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_using_merge_sources");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "delete-using.sql"),
                """
                DELETE FROM audit_log USING staging_log, archived_log
                WHERE audit_log.id = staging_log.id;
                DELETE FROM public.audit_log USING staging.stage_log, [archive].[archived_log], "public"."source"
                WHERE audit_log.id = stage_log.id;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "using-matcher.sql"),
                """
                CREATE INDEX idx_users_name ON users USING btree (name);
                ALTER TABLE users ALTER COLUMN name TYPE text USING lower(name);
                MERGE INTO audit_log AS t
                USING staging_log AS s
                ON t.id = s.id
                WHEN MATCHED THEN
                    UPDATE SET action = s.action;
                MERGE audit_log_archive AS t
                USING staging_archive AS s
                ON t.id = s.id
                WHEN MATCHED THEN
                    UPDATE SET action = s.action;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "target-hint.sql"),
                """
                MERGE INTO audit_log WITH (INDEX(ix_audit_log), HOLDLOCK) AS t
                USING staging_log AS s
                ON t.id = s.id
                WHEN MATCHED THEN
                    UPDATE SET action = s.action;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "temp-target.sql"),
                """
                MERGE #audit_log AS t
                USING staging_log AS s
                ON t.id = s.id
                WHEN MATCHED THEN
                    UPDATE SET action = s.action;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var deletePathArgs = new[] { "--path", "sql/delete-using.sql" };
            var (stagingExitCode, stagingStdout, stagingStderr) = RunReferencesInProcess("staging_log", dbPath, "sql", true, deletePathArgs);
            var (archivedExitCode, archivedStdout, archivedStderr) = RunReferencesInProcess("archived_log", dbPath, "sql", true, deletePathArgs);
            var (stageExitCode, stageStdout, stageStderr) = RunReferencesInProcess("stage_log", dbPath, "sql", true, deletePathArgs);
            var (sourceExitCode, sourceStdout, sourceStderr) = RunReferencesInProcess("source", dbPath, "sql", true, deletePathArgs);
            var (qualifiedTargetExitCode, qualifiedTargetStdout, qualifiedTargetStderr) = RunReferencesInProcess("public.audit_log", dbPath, "sql", true, deletePathArgs);
            var (qualifiedSourceExitCode, qualifiedSourceStdout, qualifiedSourceStderr) = RunReferencesInProcess("staging.stage_log", dbPath, "sql", true, deletePathArgs);
            var (mangledBracketExitCode, mangledBracketStdout, mangledBracketStderr) = RunReferencesInProcess("archive].[archived_log", dbPath, "sql", true, deletePathArgs);
            var (mangledDoubleQuoteExitCode, mangledDoubleQuoteStdout, mangledDoubleQuoteStderr) = RunReferencesInProcess("public\".\"source", dbPath, "sql", true, deletePathArgs);

            var stagingRows = ParseJsonLines(stagingStdout);
            var archivedRows = ParseJsonLines(archivedStdout);
            var stageRows = ParseJsonLines(stageStdout);
            var sourceRows = ParseJsonLines(sourceStdout);
            using var qualifiedTargetDocument = ParseJsonOutput(qualifiedTargetStdout);
            using var qualifiedSourceDocument = ParseJsonOutput(qualifiedSourceStdout);
            using var mangledBracketDocument = ParseJsonOutput(mangledBracketStdout);
            using var mangledDoubleQuoteDocument = ParseJsonOutput(mangledDoubleQuoteStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, stagingExitCode);
            Assert.Equal(string.Empty, stagingStderr);
            Assert.Single(stagingRows);

            Assert.Equal(CommandExitCodes.Success, archivedExitCode);
            Assert.Equal(string.Empty, archivedStderr);
            Assert.Equal(2, archivedRows.Count);

            Assert.Equal(CommandExitCodes.Success, stageExitCode);
            Assert.Equal(string.Empty, stageStderr);
            Assert.Single(stageRows);

            Assert.Equal(CommandExitCodes.Success, sourceExitCode);
            Assert.Equal(string.Empty, sourceStderr);
            Assert.Single(sourceRows);

            Assert.Equal(CommandExitCodes.Success, qualifiedTargetExitCode);
            Assert.Equal(string.Empty, qualifiedTargetStderr);
            Assert.Equal(3, qualifiedTargetDocument.RootElement.GetProperty("line").GetInt32());
            Assert.Equal("audit_log", qualifiedTargetDocument.RootElement.GetProperty("symbol_name").GetString());

            Assert.Equal(CommandExitCodes.Success, qualifiedSourceExitCode);
            Assert.Equal(string.Empty, qualifiedSourceStderr);
            Assert.Equal(3, qualifiedSourceDocument.RootElement.GetProperty("line").GetInt32());
            Assert.Equal("stage_log", qualifiedSourceDocument.RootElement.GetProperty("symbol_name").GetString());

            Assert.Equal(CommandExitCodes.Success, mangledBracketExitCode);
            Assert.Equal(string.Empty, mangledBracketStderr);
            Assert.Equal(0, mangledBracketDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, mangledDoubleQuoteExitCode);
            Assert.Equal(string.Empty, mangledDoubleQuoteStderr);
            Assert.Equal(0, mangledDoubleQuoteDocument.RootElement.GetProperty("count").GetInt32());

            AssertRows("sql/using-matcher.sql", "staging_log", 1, "reference");
            AssertRows("sql/using-matcher.sql", "audit_log_archive", 1, "reference");
            AssertRows("sql/using-matcher.sql", "staging_archive", 1, "reference");
            AssertNoRows("sql/using-matcher.sql", "btree");
            AssertRows("sql/using-matcher.sql", "lower", 1, "call");

            AssertRows("sql/target-hint.sql", "staging_log", 1, "reference", expectedLine: 2);
            AssertRows("sql/temp-target.sql", "#audit_log", 1, "reference");
            AssertRows("sql/temp-target.sql", "staging_log", 1, "reference");

            void AssertRows(
                string path,
                string query,
                int expectedCount,
                string expectedKind,
                int? expectedLine = null)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    query, dbPath, "sql", true, "--path", path);
                var rows = ParseJsonLines(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(expectedCount, rows.Count);
                Assert.All(rows, row => Assert.Equal(
                    query,
                    row.RootElement.GetProperty("symbol_name").GetString()));
                Assert.All(rows, row => Assert.Equal(
                    expectedKind,
                    row.RootElement.GetProperty("reference_kind").GetString()));
                if (expectedLine.HasValue)
                    Assert.All(rows, row => Assert.Equal(
                        expectedLine.Value,
                        row.RootElement.GetProperty("line").GetInt32()));
            }

            void AssertNoRows(string path, string query)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    query, dbPath, "sql", true, "--path", path);
                using var document = ParseJsonOutput(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_SqlLineEndCommentBoundariesShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_line_end_comments");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "multiline.sql"),
                """
                DELETE FROM audit_log -- trailing comment
                USING staging_log
                WHERE audit_log.id = staging_log.id;

                MERGE INTO audit_log -- trailing comment
                USING staging_merge AS s
                ON audit_log.id = s.id
                WHEN MATCHED THEN
                    UPDATE SET action = s.action;

                SELECT id INTO #comment_temp -- trailing comment
                FROM users;
                SELECT * FROM #comment_temp;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "unfinished-prefixes.sql"),
                """
                SELECT id INTO -- trailing comment
                    #comment_temp
                FROM users;
                SELECT * FROM #comment_temp;

                DELETE FROM audit_log USING staging_log, -- trailing comment
                    archived_log
                WHERE audit_log.id = staging_log.id;

                MERGE INTO audit_log WITH (INDEX(ix_audit_log), -- trailing comment
                    HOLDLOCK) AS t
                USING staging_merge AS s
                ON t.id = s.id
                WHEN MATCHED THEN
                    UPDATE SET action = s.action;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "target-prefixes.sql"),
                """
                INSERT INTO -- trailing comment
                    audit_log (action) VALUES ('x');

                UPDATE -- trailing comment
                    #update_temp SET action = 'x';
                SELECT * FROM #update_temp;

                DELETE FROM -- trailing comment
                    #delete_temp;
                SELECT * FROM #delete_temp;

                TRUNCATE TABLE -- trailing comment
                    #truncate_temp;
                SELECT * FROM #truncate_temp;

                CREATE TABLE -- trailing comment
                    #create_temp (id int);
                SELECT * FROM #create_temp;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertRows("sql/multiline.sql", "staging_log", 1, "reference");
            AssertRows("sql/multiline.sql", "staging_merge", 1, "reference");
            AssertRows("sql/multiline.sql", "#comment_temp", 2);

            AssertRows("sql/unfinished-prefixes.sql", "#comment_temp", 2);
            AssertRows("sql/unfinished-prefixes.sql", "archived_log", 1, "reference");
            AssertRows("sql/unfinished-prefixes.sql", "staging_merge", 1, "reference");

            AssertRows("sql/target-prefixes.sql", "audit_log", 1, "reference");
            AssertRows("sql/target-prefixes.sql", "#update_temp", 2);
            AssertRows("sql/target-prefixes.sql", "#delete_temp", 2);
            AssertRows("sql/target-prefixes.sql", "#truncate_temp", 2);
            AssertRows("sql/target-prefixes.sql", "#create_temp", 1);

            void AssertRows(string path, string query, int expectedCount, string? expectedKind = null)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    query, dbPath, "sql", true, "--path", path);
                var rows = ParseJsonLines(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(expectedCount, rows.Count);
                Assert.All(rows, row => Assert.Equal(
                    query,
                    row.RootElement.GetProperty("symbol_name").GetString()));
                if (expectedKind is not null)
                    Assert.All(rows, row => Assert.Equal(
                        expectedKind,
                        row.RootElement.GetProperty("reference_kind").GetString()));
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_SqlBareDollarIdentifiersStayWhole()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_bare_dollar_identifier");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                SELECT * FROM my$table;
                INSERT INTO my$table (id) VALUES (1);
                UPDATE my$table SET id = 2;
                DELETE FROM my$table;
                TRUNCATE TABLE my$table;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (dollarExitCode, dollarStdout, dollarStderr) = RunReferencesInProcess("my$table", dbPath);
            var (prefixExitCode, prefixStdout, prefixStderr) = RunReferencesInProcess("my", dbPath);

            var dollarRows = ParseJsonLines(dollarStdout);
            using var prefixDocument = ParseJsonOutput(prefixStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, dollarExitCode);
            Assert.Equal(string.Empty, dollarStderr);
            Assert.Equal(5, dollarRows.Count);
            Assert.All(dollarRows, row => Assert.Equal("my$table", row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, prefixExitCode);
            Assert.Equal(string.Empty, prefixStderr);
            Assert.Equal(0, prefixDocument.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_SqlSemicolonlessTempReadsShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_semicolonless_temp_reads");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "set-declare.sql"),
                """
                SELECT * FROM #future_temp;
                SELECT id INTO #set_temp FROM users
                SET @count = (SELECT COUNT(*) FROM #set_temp);
                SELECT id INTO #declare_temp FROM users
                DECLARE @first_id INT = (SELECT TOP (1) id FROM #declare_temp);
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "if-while.sql"),
                """
                SELECT id INTO #if_temp FROM users
                IF EXISTS (SELECT 1) SELECT * FROM #if_temp;
                SELECT id INTO #while_temp FROM users
                WHILE 1 = 0 SELECT * FROM #while_temp;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertNoRows("sql/set-declare.sql", "#future_temp");
            AssertRows("sql/set-declare.sql", "#set_temp", 2);
            AssertRows("sql/set-declare.sql", "#declare_temp", 2);
            AssertRows("sql/if-while.sql", "#if_temp", 2);
            AssertRows("sql/if-while.sql", "#while_temp", 2);

            void AssertRows(string path, string query, int expectedCount)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    query, dbPath, "sql", true, "--path", path);
                var rows = ParseJsonLines(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(expectedCount, rows.Count);
                Assert.All(rows, row => Assert.Equal(
                    query,
                    row.RootElement.GetProperty("symbol_name").GetString()));
            }

            void AssertNoRows(string path, string query)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    query, dbPath, "sql", true, "--path", path);
                using var document = ParseJsonOutput(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_SqlNonCodeRegionsShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_non_code_regions");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "double-quoted.sql"),
                """
                SET @sql = "SELECT id INTO #temp_users FROM users";
                SELECT * FROM #temp_users;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "dollar-body.sql"),
                """
                DO $$BEGIN END$$; SELECT * FROM users; DO $$BEGIN END$$;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "multiline-single-quoted.sql"),
                """
                SELECT 'abc''
                still escaped \'
                FROM phantom
                INTO #temp_users
                ';
                SELECT * FROM users;
                SELECT * FROM #temp_users;
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "comments-and-dollar-bodies.sql"),
                """
                SELECT * FROM users /* comment
                FROM phantom */;
                UPDATE audit_log SET action = 'done';
                DO $$
                BEGIN
                  EXECUTE $$SELECT * FROM phantom$$;
                END
                $$;
                DO $body$
                BEGIN
                  UPDATE phantom SET action = 'nope';
                END
                $body$;
                SELECT * FROM accounts;
                DELETE FROM archived_accounts;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertNoRows("sql/double-quoted.sql", "#temp_users");
            AssertRows("sql/dollar-body.sql", "users", 1, expectedLine: 1);
            AssertRows("sql/multiline-single-quoted.sql", "users", 1, expectedLine: 6);
            AssertNoRows("sql/multiline-single-quoted.sql", "#temp_users");
            AssertNoRows("sql/comments-and-dollar-bodies.sql", "phantom");
            AssertRows("sql/comments-and-dollar-bodies.sql", "accounts", 1, expectedLine: 14);

            void AssertRows(string path, string query, int expectedCount, int expectedLine)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    query, dbPath, "sql", true, "--path", path);
                var rows = ParseJsonLines(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(expectedCount, rows.Count);
                Assert.All(rows, row => Assert.Equal(
                    query,
                    row.RootElement.GetProperty("symbol_name").GetString()));
                Assert.All(rows, row => Assert.Equal(
                    expectedLine,
                    row.RootElement.GetProperty("line").GetInt32()));
            }

            void AssertNoRows(string path, string query)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    query, dbPath, "sql", true, "--path", path);
                using var document = ParseJsonOutput(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
                Assert.Empty(document.RootElement.GetProperty("references").EnumerateArray());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpNullableSuffixesShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_nullable_suffixes");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "array-rank.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<object> items, object value)
                    {
                        return Sink.Pick(from Status in items
                                         let cast = value as Status[,]?
                                         select(Status.Ready),
                                         Status.Ready);
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "tuple.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<object> items, object value)
                    {
                        return Sink.Pick(from Status in items
                                         let cast = value as (int Left, int Right)?
                                         select(Status.Ready),
                                         Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertSingleReference("src/array-rank.cs");
            AssertSingleReference("src/tuple.cs");

            void AssertSingleReference(string path)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    "Ready", dbPath, "csharp", true, "--path", path);
                var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
                Assert.Equal("Read", row.GetProperty("container_name").GetString());
                Assert.Contains("Status.Ready", row.GetProperty("context").GetString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpCastedLocalSelectCallsShareIndexedWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_casted_local_select_calls");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "object-cast.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        static object select(IEnumerable<Holder> xs) => xs.Count();
                        return from Status in items
                               orderby (object)select(items), items.Count()
                               select Status.Ready;
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "simple-cast.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class CustomType
                {
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<Holder> items)
                    {
                        static CustomType select(IEnumerable<Holder> xs) => new();
                        return Sink.Pick(from Status in items
                                         orderby (CustomType)select(items), items.Count()
                                         select Status.Ready,
                                         Demo.Status.Ready);
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "multiline-cast.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class CustomType
                {
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<Holder> items)
                    {
                        static CustomType select(IEnumerable<Holder> xs) => new();
                        return Sink.Pick(from Status in items
                                         orderby (CustomType)
                                                 select(items), items.Count()
                                         select Status.Ready,
                                         Demo.Status.Ready);
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "lowercase-alias.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;
                using customType = Demo.CustomType;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class CustomType
                {
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<object> items)
                    {
                        static customType select(IEnumerable<object> xs) => new();
                        return Sink.Pick(from Status in items
                                         orderby (customType)select(items)
                                         select Status.Ready,
                                         Demo.Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (objectExitCode, objectStdout, objectStderr) = RunReferencesInProcess(
                "Ready", dbPath, "csharp", true, "--path", "src/object-cast.cs");
            using var objectDocument = ParseJsonOutput(objectStdout);
            Assert.Equal(CommandExitCodes.Success, objectExitCode);
            Assert.Equal(string.Empty, objectStderr);
            Assert.Equal(0, objectDocument.RootElement.GetProperty("count").GetInt32());

            AssertSingleReference("src/simple-cast.cs");
            AssertSingleReference("src/multiline-cast.cs");
            AssertSingleReference("src/lowercase-alias.cs");

            void AssertSingleReference(string path)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    "Ready", dbPath, "csharp", true, "--path", path);
                var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
                Assert.Equal("Read", row.GetProperty("container_name").GetString());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpParenthesizedCoalesceOrderByBeforeParenthesizedTerminalSelectPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_coalesce_before_parenthesized_select");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<object> items, int? left, int right)
                    {
                        return Sink.Pick(from Status in items
                                         orderby (left ?? right)
                                         select(Status.Ready),
                                         Demo.Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Ready", dbPath, "csharp");

            var rows = ParseJsonLines(stdout);
            var row = Assert.Single(rows).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
            Assert.Equal("Read", row.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpParenthesizedQualifiedMemberAccessBeforeParenthesizedTerminalSelectPreservesOnlyRealEnumReferences()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_qualified_member_access_before_parenthesized_select");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<object> items)
                    {
                        return Sink.Pick(from Status in items
                                         orderby (Demo.Status.Ready)
                                         select(Status.Ready),
                                         Demo.Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Ready", dbPath, "csharp");

            var rows = ParseJsonLines(stdout);
            var parsedRows = rows.Select(document => document.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, parsedRows.Count);
            Assert.All(parsedRows, row => Assert.Equal("Read", row.GetProperty("container_name").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpParenthesizedKeywordNamedParameterBeforeParenthesizedTerminalSelectPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_keyword_named_parameter_before_parenthesized_select");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<object> items, int Select)
                    {
                        return Sink.Pick(from Status in items
                                         orderby (Select)
                                         select(Status.Ready),
                                         Demo.Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Ready", dbPath, "csharp");

            var rows = ParseJsonLines(stdout);
            var row = Assert.Single(rows).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
            Assert.Equal("Read", row.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpParenthesizedUppercaseConstantBeforeParenthesizedTerminalSelectPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_uppercase_constant_before_parenthesized_select");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<object> items)
                    {
                        const int READY = 1;
                        return Sink.Pick(from Status in items
                                         orderby (READY)
                                         select(Status.Ready),
                                         Demo.Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Ready", dbPath, "csharp");

            var rows = ParseJsonLines(stdout);
            var row = Assert.Single(rows).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
            Assert.Equal("Read", row.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpParenthesizedTerminalSelectAfterGenericClosePreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_terminal_select_after_generic_close");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<object> items)
                    {
                        return Sink.Pick(from Status in items where Status is List<int> select(Status.Ready), Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Ready", dbPath, "csharp");

            var rows = ParseJsonLines(stdout);
            var row = Assert.Single(rows).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
            Assert.Equal("Read", row.GetProperty("container_name").GetString());
            Assert.Contains("Status.Ready", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpNestedQueryBeforeParenthesizedOrderByCommaPreservesOnlyTrailingEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_nested_query_parenthesized_orderby_collision");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(object left, Status right) => right;
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<Holder> items, IEnumerable<int> others)
                    {
                        return Sink.Pick(from Status in items
                                         let nested = from x in others select x
                                         orderby(items.Count()), nested.Count()
                                         select Status.Ready,
                                         Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Ready", dbPath, "csharp");

            var rows = ParseJsonLines(stdout);
            var row = Assert.Single(rows).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
            Assert.Equal("Read", row.GetProperty("container_name").GetString());
            Assert.Contains("Status.Ready", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpTerminalSelectIdentifierNamedDescendingPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_terminal_select_descending_identifier");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static object Pick(object left, Status right) => right;
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public object Read(IEnumerable<Holder> items)
                    {
                        var descending = 1;
                        return Sink.Pick(from Status in items select descending, Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Ready", dbPath, "csharp");

            var rows = ParseJsonLines(stdout);
            var row = Assert.Single(rows).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
            Assert.Equal(26, row.GetProperty("line").GetInt32());
            Assert.Contains("Status.Ready", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpLambdaParameterScopesShareDatabaseFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_lambda_parameter_scopes");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/ordinary.cs", "csharp",
                """
                using System;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read()
                    {
                        Func<Holder, int> f = Status => Status.Ready;
                        return Demo.Status.Ready;
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/same-line.cs", "csharp",
                """
                using System;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read()
                    {
                        Func<Holder, int> f = Status => Status.Ready; return Demo.Status.Ready;
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/parenthesized.cs", "csharp",
                """
                using System;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Demo.Status Pick(Demo.Status left, Func<Holder, int> right) => left;
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read()
                    {
                        return Sink.Pick(Demo.Status.Ready, (Holder Status) => Status.Ready);
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            AssertSingleReference("src/ordinary.cs", expectedLine: 20);
            AssertSingleReference("src/same-line.cs", expectedLine: null);
            AssertSingleReference("src/parenthesized.cs", expectedLine: null);

            void AssertSingleReference(string path, int? expectedLine)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    "Ready", dbPath, "csharp", true, "--path", path);
                var json = Assert.Single(ParseJsonLines(stdout)).RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
                Assert.Equal("Read", json.GetProperty("container_name").GetString());
                if (expectedLine.HasValue)
                    Assert.Equal(expectedLine.Value, json.GetProperty("line").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableScopesShareDatabaseFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_query_range_variable_scopes");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/after-query.cs", "csharp",
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(IEnumerable<Holder> items)
                    {
                        _ = from Status in items
                            select Status.Ready;

                        return Demo.Status.Ready;
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/query-argument.cs", "csharp",
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Demo.Status Pick(IEnumerable<int> left, Demo.Status right) => right;
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(IEnumerable<Holder> items)
                    {
                        return Sink.Pick(from Status in items select Status.Ready, Demo.Status.Ready);
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            AssertSingleReference("src/after-query.cs", expectedLine: 23);
            AssertSingleReference("src/query-argument.cs", expectedLine: null);

            void AssertSingleReference(string path, int? expectedLine)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    "Ready", dbPath, "csharp", true, "--path", path);
                var json = Assert.Single(ParseJsonLines(stdout)).RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
                Assert.Equal("Read", json.GetProperty("container_name").GetString());
                if (expectedLine.HasValue)
                    Assert.Equal(expectedLine.Value, json.GetProperty("line").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpForeachShadowingScopesShareDatabaseFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_foreach_shadowing_scopes");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/embedded.cs", "csharp",
                """
                using System.Collections.Generic;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(IEnumerable<Holder> items)
                    {
                        foreach (var Status in items)
                            _ = Status.Ready;

                        return Demo.Status.Ready;
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/same-line.cs", "csharp",
                """
                using System.Collections.Generic;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(IEnumerable<Holder> items)
                    {
                        foreach (var Status in items) _ = Status.Ready; return Demo.Status.Ready;
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/else-branch.cs", "csharp",
                """
                using System.Collections.Generic;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(IEnumerable<Holder> items, bool flag)
                    {
                        foreach (var Status in items)
                            if (flag)
                                _ = 0;
                            else
                                _ = Status.Ready;

                        return Demo.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            AssertSingleReference("src/embedded.cs", expectedLine: 22);
            AssertSingleReference("src/same-line.cs", expectedLine: null);
            AssertSingleReference("src/else-branch.cs", expectedLine: null);

            void AssertSingleReference(string path, int? expectedLine)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    "Ready", dbPath, "csharp", true, "--path", path);
                var json = Assert.Single(ParseJsonLines(stdout)).RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
                Assert.Equal("Read", json.GetProperty("container_name").GetString());
                if (expectedLine.HasValue)
                    Assert.Equal(expectedLine.Value, json.GetProperty("line").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpLaterLocalShadowingDoesNotSuppressEarlierReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_local_order");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Before()
                    {
                        _ = Status.Ready;
                        Holder Status = new();
                        return Demo.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--limit", "10"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal([17, 19], rows.Select(row => row.RootElement.GetProperty("line").GetInt32()).OrderBy(line => line).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpDeclarationPatternStatementsShareDatabaseFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_declaration_pattern_statements");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/if.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(object value)
                    {
                        if (value is Holder Status)
                        {
                            _ = Status.Ready;
                        }

                        return Demo.Status.Ready;
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/multiline-if.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(object value)
                    {
                        if (
                            value is Holder Status)
                        {
                            _ = Status.Ready;
                        }

                        return Demo.Status.Ready;
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/multiline-while.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(object value)
                    {
                        while (
                            value is Holder Status)
                        {
                            _ = Status.Ready;
                            break;
                        }

                        return Demo.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            AssertSingleReference("src/if.cs", expectedLine: 22);
            AssertSingleReference("src/multiline-if.cs", expectedLine: 23);
            AssertSingleReference("src/multiline-while.cs", expectedLine: 24);

            void AssertSingleReference(string path, int expectedLine)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    "Ready", dbPath, "csharp", true, "--path", path);
                var json = Assert.Single(ParseJsonLines(stdout)).RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
                Assert.Equal(expectedLine, json.GetProperty("line").GetInt32());
                Assert.Equal("Read", json.GetProperty("container_name").GetString());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpLambdaScopedDeclarationPatternVariableDoesNotLeakIntoOuterIfBody()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_lambda_scoped_declaration_pattern_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace RealNs;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public RealNs.Status Read(object[] values)
                    {
                        if (values.Any(value => value is Holder RealNs))
                        {
                            return RealNs.Status.Ready;
                        }

                        return RealNs.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var first = ParseJsonOutput(stdout);
            var lines = ParseJsonLines(stdout)
                .Select(document => document.RootElement.GetProperty("line").GetInt32())
                .OrderBy(line => line)
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", first.RootElement.GetProperty("reference_kind").GetString());
            Assert.Equal([19, 22], lines);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpNestedLambdaScopedDeclarationPatternVariableDoesNotLeakIntoOuterIfBody()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_nested_lambda_scoped_declaration_pattern_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace RealNs;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public RealNs.Status Read(object[] values)
                    {
                        if (values.Any(value => value is Holder RealNs && values.Any(other => other is Holder Other)))
                        {
                            return RealNs.Status.Ready;
                        }

                        return RealNs.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var first = ParseJsonOutput(stdout);
            var lines = ParseJsonLines(stdout)
                .Select(document => document.RootElement.GetProperty("line").GetInt32())
                .OrderBy(line => line)
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", first.RootElement.GetProperty("reference_kind").GetString());
            Assert.Equal([19, 22], lines);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSwitchCaseDeclarationPatternVariableDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_switch_case_declaration_pattern_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(object value)
                    {
                        switch (value)
                        {
                            case Holder Status:
                                _ = Status.Ready;
                                break;
                        }

                        return Demo.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(24, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpConditionalExpressionDeclarationPatternVariableDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_conditional_expression_declaration_pattern_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(object value)
                    {
                        return value is Holder Status
                            ? (Demo.Status)Status.Ready
                            : Demo.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(19, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpRecursivePatternVariableDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_recursive_pattern_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(object value)
                    {
                        if (value is Holder { Ready: > 0 } Status)
                        {
                            _ = Status.Ready;
                        }

                        return Demo.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(22, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpMultiLineRecursivePatternVariableDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_multiline_recursive_pattern_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(object value)
                    {
                        if (value is Holder
                            {
                                Ready: > 0
                            } Status)
                        {
                            _ = Status.Ready;
                        }

                        return Demo.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(25, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableGenericSelectorDoesNotLeakEnumReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_generic_selector");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public static class Sink
                {
                    public static int Wrap<TLeft, TRight>(int value) => value;
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        return from Status in items
                               select Sink.Wrap<int, int>(Status.Ready);
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("references").GetArrayLength());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableShiftSelectorPreservesOnlyTrailingEnumReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_shift_selector");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public static class Sink
                {
                    public static IEnumerable<int> Pick(IEnumerable<int> left, Status right) => left;
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        return Sink.Pick(
                            from Status in items
                            select (Status.Ready << 1) >> (1 + Status.Ready),
                            Status.Ready);
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(28, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpGenericTypePatternsShareDatabaseFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_generic_type_patterns");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/designation.cs", "csharp",
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public int Read(IEnumerable<Holder> items)
                    {
                        return (from Status in items
                                select Status is Dictionary<int, int> dict ? Status.Ready : 0).First();
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/without-designation.cs", "csharp",
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public int Read(IEnumerable<Holder> items)
                    {
                        return (from Status in items
                                select Status is Dictionary<int, int> ? Status.Ready : 0).First();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            AssertNoReferences("src/designation.cs");
            AssertNoReferences("src/without-designation.cs");

            void AssertNoReferences(string path)
            {
                var (exitCode, stdout, stderr) = RunReferencesInProcess(
                    "Ready", dbPath, "csharp", true, "--path", path);
                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, json.GetProperty("count").GetInt32());
                Assert.Equal(0, json.GetProperty("references").GetArrayLength());
                Assert.True(json.GetProperty("exact_index_available").GetBoolean());
                Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableGenericNullComparisonsDoNotLeakEnumReferences()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_generic_as_null");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public int ReadNotEqual(IEnumerable<Holder> items)
                    {
                        return (from Status in items
                                select Status as Dictionary<int, int> != null ? Status.Ready : 0).First();
                    }

                    public int ReadEqual(IEnumerable<Holder> items)
                    {
                        return (from Status in items
                                select Status as Dictionary<int, int> == null ? Status.Ready : 0).First();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("references").GetArrayLength());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableGenericAsNullComparisonPreservesLaterEnumReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_generic_as_null_preserves_later_reference");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public static class Sink
                {
                    public static Status Pick(int left, Status right) => right;
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Status Read(IEnumerable<Holder> items)
                    {
                        return Sink.Pick(
                            (from Status in items
                             select Status as Dictionary<int, int> != null ? Status.Ready : 0).First(),
                            Status.Ready);
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(28, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableTupleGenericSelectorDoesNotLeakEnumReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_tuple_generic_selector");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public static class Sink
                {
                    public static int Wrap<T>(int value) => value;
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        return from Status in items
                               select Sink.Wrap<(int, List<int>)>(Status.Ready);
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("references").GetArrayLength());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpRecursivePatternCaseVariableDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_recursive_pattern_case_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status Read(object value)
                    {
                        switch (value)
                        {
                            case Holder { Ready: > 0 } Status:
                                _ = Status.Ready;
                                break;
                        }

                        return Demo.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(24, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionPatternVariablesKeepOnlyGenuineReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_switch_expression_pattern_collisions");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public Demo.Status ReadMultiLineComment(object value)
                    {
                        return value switch
                        {
                            Holder /* trivia
                                      when comment */ Status when Status.Ready > 0 => Demo.Status.Ready,
                            _ => Demo.Status.Ready
                        };
                    }

                    public Demo.Status ReadGuard(object value)
                    {
                        return value switch
                        {
                            Holder Status when Status.Ready > 0 => Demo.Status.Ready,
                            _ => Demo.Status.Ready
                        };
                    }

                    public Demo.Status ReadRecursive(object value)
                    {
                        return value switch
                        {
                            Holder { Ready: > 0 } Status => (Demo.Status)Status.Ready,
                            _ => Demo.Status.Ready
                        };
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var first = ParseJsonOutput(stdout);
            var firstJson = first.RootElement;
            var rowsByContainer = ParseJsonLines(stdout)
                .Select(document => (
                    Column: document.RootElement.GetProperty("column").GetInt32(),
                    ContainerName: document.RootElement.GetProperty("container_name").GetString()))
                .GroupBy(row => row.ContainerName)
                .ToDictionary(group => group.Key!, group => group.Select(row => row.Column).ToArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", firstJson.GetProperty("reference_kind").GetString());
            Assert.Equal([83, 30], rowsByContainer["ReadMultiLineComment"]);
            Assert.Equal([64, 30], rowsByContainer["ReadGuard"]);
            Assert.Equal([30], rowsByContainer["ReadRecursive"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpStaticLambdaScopedDeclarationPatternVariableDoesNotLeakIntoOuterIfBody()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_static_lambda_declaration_pattern_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace RealNs;

                public enum Status
                {
                    Ready
                }

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public RealNs.Status Read(object[] values)
                    {
                        if (values.Any(static value => value is Holder RealNs))
                        {
                            return RealNs.Status.Ready;
                        }

                        return RealNs.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var first = ParseJsonOutput(stdout);
            var lines = ParseJsonLines(stdout)
                .Select(document => document.RootElement.GetProperty("line").GetInt32())
                .OrderBy(line => line)
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", first.RootElement.GetProperty("reference_kind").GetString());
            Assert.Equal([19, 22], lines);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpDottedValueReceiverChainDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_dotted_value_receiver_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace RealNs;

                public enum Status
                {
                    Ready
                }

                namespace Test;

                public sealed class ReadyHolder
                {
                    public int Ready { get; set; }
                }

                public sealed class NamespaceLike
                {
                    public ReadyHolder Status { get; } = new();
                }

                public sealed class Uses
                {
                    public global::RealNs.Status Read(NamespaceLike RealNs)
                    {
                        _ = RealNs.Status.Ready;
                        return global::RealNs.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(25, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpGlobalQualifiedEnumMemberSurvivesConflictingTypeName()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_global_qualified");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Status
                {
                    Ready
                }

                namespace Other;

                public static class Status
                {
                    public static int Value = 1;
                }

                public class Uses
                {
                    public Demo.Status Read()
                    {
                        return global::Demo.Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(19, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpGlobalQualifiedEnumMemberSurvivesPropertyShadowing()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_global_property_shadow");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                enum Color
                {
                    Red
                }

                class C
                {
                    int Color => 0;

                    void M()
                    {
                        var x = global::Color.Red;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(12, json.GetProperty("line").GetInt32());
            Assert.Equal("M", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpCompactSameLineTypeBody_PrefersInnermostMethodContainer()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_references_compact_same_line_type_body");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace N;
                enum Color { Red }
                class C { int N => 0; void M() { var x = global::N.Color.Red; } }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal("function", json.GetProperty("container_kind").GetString());
            Assert.Equal("M", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpGlobalQualifiedEnumMemberSurvivesConflictingUsingAlias()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_global_alias_shadow");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo
                {
                    public enum Color
                    {
                        Red
                    }
                }

                namespace Shadow
                {
                    public static class Demo
                    {
                        public static int Red => 0;
                    }
                }

                using Demo = Shadow;

                class C
                {
                    Demo.Color M()
                    {
                        return global::Demo.Color.Red;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("member_read", json.GetProperty("reference_kind").GetString());
            Assert.Equal(23, json.GetProperty("line").GetInt32());
            Assert.Equal("M", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpLogicalConstantMemberPatternDoesNotLeakAcrossFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_constant_member_pattern_cross_file");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Defs;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using Defs;

                class Demo
                {
                    void Run(Color value)
                    {
                        switch (value)
                        {
                            case Color.Red or Color.Blue:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpMixedLogicalPatternKeepsTypeHead()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_mixed_logical_type_pattern");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                enum Color { Red, Blue }
                class Point {}

                class Demo
                {
                    bool Match1(object value) => value is Color.Red or Point;
                    bool Match2(object value) => value is Point or Color.Red;

                    void Run1(object value)
                    {
                        switch (value)
                        {
                            case Color.Red or Point:
                                break;
                        }
                    }

                    void Run2(object value)
                    {
                        switch (value)
                        {
                            case Point or Color.Red:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Point", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(4, rows.Count);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlQuotedTvfCallsStayVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_quoted_tvf_calls");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/repro.sql", "sql",
                """
                SELECT * FROM [dbo].[fn_GetUserStats](42);
                SELECT * FROM `fn_GetUserStats`(42);
                SELECT * FROM dbo.fn_GetUserStats(42);
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["fn_GetUserStats", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Equal("fn_GetUserStats", row.GetProperty("symbol_name").GetString());
                Assert.Equal("call", row.GetProperty("reference_kind").GetString());
            });
            Assert.Contains(rows, row => row.GetProperty("line").GetInt32() == 1);
            Assert.Contains(rows, row => row.GetProperty("line").GetInt32() == 2);
            Assert.Contains(rows, row => row.GetProperty("line").GetInt32() == 3);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_NonExactJson_CSharpMultiLineUsingStaticConstantPatternKeepsRows()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_multiline_constant_pattern_refs");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is
                        Red
                        or
                        Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--path", "src/Use.cs"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.Equal([8, 10], rows.Select(row => row.GetProperty("line").GetInt32()).ToArray());
            Assert.All(rows, row => Assert.Equal("Match", row.GetProperty("container_name").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpMultiLineCaseTypePatternsKeepFirstAndLaterHeads()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_multiline_case_type_patterns");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class Point {}
                class Shape {}

                class Demo
                {
                    void Run(object value)
                    {
                        switch (value)
                        {
                            case
                                Point:
                                break;
                            case Point or
                                Shape:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (pointExitCode, pointStdout, pointStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Point", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var pointRows = ParseJsonLines(pointStdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, pointExitCode);
            Assert.Equal(string.Empty, pointStderr);
            Assert.Equal(2, pointRows.Count);
            Assert.Equal([13, 15], pointRows.Select(row => row.GetProperty("line").GetInt32()).OrderBy(line => line).ToArray());
            Assert.All(pointRows, row => Assert.Equal("Run", row.GetProperty("container_name").GetString()));

            var (shapeExitCode, shapeStdout, shapeStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Shape", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var shapeRows = ParseJsonLines(shapeStdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, shapeExitCode);
            Assert.Equal(string.Empty, shapeStderr);
            var shapeRow = Assert.Single(shapeRows);
            Assert.Equal(16, shapeRow.GetProperty("line").GetInt32());
            Assert.Equal("Run", shapeRow.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpCommentSeparatedMultiLineTypePatternsKeepRows()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_comment_separated_multiline_type_patterns");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class Point {}

                class Demo
                {
                    bool Match(object value) => value is
                        // formatting-only comment
                        Point;

                    void Run(object value)
                    {
                        switch (value)
                        {
                            case
                                // formatting-only comment
                                Point:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Point", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var rows = ParseJsonLines(stdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.Equal([9, 17], rows.Select(row => row.GetProperty("line").GetInt32()).OrderBy(line => line).ToArray());
            Assert.Equal(["Match", "Run"], rows.Select(row => row.GetProperty("container_name").GetString()).OrderBy(name => name).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpStandaloneNotLineMultiLineTypePatternsKeepRows()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_standalone_not_line_multiline_type_patterns");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class Point {}

                class Demo
                {
                    bool Match(object value) => value is
                        not
                        Point;

                    void Run(object value)
                    {
                        switch (value)
                        {
                            case
                                not
                                Point:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Point", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var rows = ParseJsonLines(stdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.Equal([9, 17], rows.Select(row => row.GetProperty("line").GetInt32()).OrderBy(line => line).ToArray());
            Assert.Equal(["Match", "Run"], rows.Select(row => row.GetProperty("container_name").GetString()).OrderBy(name => name).ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpNonTypeCaseLabelsDoNotEmitPhantomTypeReferences()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_non_type_case_labels");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class Demo
                {
                    void Run(int value)
                    {
                        switch (value)
                        {
                            case > 0:
                                Target();
                                break;
                        }
                    }

                    void Target() {}
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Target", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var rows = ParseJsonLines(stdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var row = Assert.Single(rows);
            Assert.Equal("call", row.GetProperty("reference_kind").GetString());
            Assert.Equal(10, row.GetProperty("line").GetInt32());
            Assert.Equal("Run", row.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_NonExactJson_CSharpMultiLineCaseUsingStaticConstantPatternKeepsRows()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_multiline_case_constant_pattern_refs");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    void Run(object value)
                    {
                        switch (value)
                        {
                            case
                                Red
                                or
                                Red:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--path", "src/Use.cs"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.Equal([12, 14], rows.Select(row => row.GetProperty("line").GetInt32()).OrderBy(line => line).ToArray());
            Assert.All(rows, row => Assert.Equal("Run", row.GetProperty("container_name").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_NonExactJson_CSharpCommentSeparatedMultiLineCaseUsingStaticConstantPatternKeepsRows()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_comment_separated_multiline_case_constant_pattern_refs");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    void Run(object value)
                    {
                        switch (value)
                        {
                            case
                                // formatting-only comment
                                Red
                                or
                                Red:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--path", "src/Use.cs"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.Equal([13, 15], rows.Select(row => row.GetProperty("line").GetInt32()).OrderBy(line => line).ToArray());
            Assert.All(rows, row => Assert.Equal("Run", row.GetProperty("container_name").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpCommentSeparatedMultiLineCaseUsingStaticConstantPattern_StaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_comment_separated_multiline_case_constant_pattern_exact_suppressed");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Defs.cs"),
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Use.cs"),
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    void Run(object value)
                    {
                        switch (value)
                        {
                            case
                                // formatting-only comment
                                Red
                                or
                                Red:
                                break;
                        }
                    }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (nonExactExitCode, nonExactStdout, nonExactStderr) = RunReferencesInProcess(
                "Red", dbPath, "csharp", false, "--path", "src/Use.cs", "--limit", "100");
            var nonExactRows = ParseJsonLines(nonExactStdout)
                .Select(document => document.RootElement)
                .ToList();

            var (exactExitCode, exactStdout, exactStderr) = RunReferencesInProcess("Red", dbPath, "csharp");
            using var exactDocument = ParseJsonOutput(exactStdout);

            var (countExitCode, countStdout, countStderr) = RunReferencesInProcess(
                "Red", dbPath, "csharp", true, "--count");
            using var countDocument = ParseJsonOutput(countStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, nonExactExitCode);
            Assert.Equal(string.Empty, nonExactStderr);
            Assert.Equal(2, nonExactRows.Count);
            Assert.Equal([13, 15], nonExactRows.Select(row => row.GetProperty("line").GetInt32()).OrderBy(line => line).ToArray());

            Assert.Equal(CommandExitCodes.Success, exactExitCode);
            Assert.Equal(string.Empty, exactStderr);
            Assert.Equal(0, exactDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            Assert.Equal(0, countDocument.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpBlankLineSeparatedMultiLineCaseUsingStaticConstantPattern_StaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_blank_line_multiline_case_constant_pattern_exact_suppressed");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Defs.cs"),
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Use.cs"),
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    void Run(object value)
                    {
                        switch (value)
                        {
                            case

                                Red
                                or
                                Red:
                                break;
                        }
                    }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (nonExactExitCode, nonExactStdout, nonExactStderr) = RunReferencesInProcess(
                "Red", dbPath, "csharp", false, "--path", "src/Use.cs", "--limit", "100");
            var nonExactRows = ParseJsonLines(nonExactStdout)
                .Select(document => document.RootElement)
                .ToList();

            var (exactExitCode, exactStdout, exactStderr) = RunReferencesInProcess("Red", dbPath, "csharp");
            using var exactDocument = ParseJsonOutput(exactStdout);

            var (countExitCode, countStdout, countStderr) = RunReferencesInProcess(
                "Red", dbPath, "csharp", true, "--count");
            using var countDocument = ParseJsonOutput(countStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, nonExactExitCode);
            Assert.Equal(string.Empty, nonExactStderr);
            Assert.Equal(2, nonExactRows.Count);
            Assert.Equal([13, 15], nonExactRows.Select(row => row.GetProperty("line").GetInt32()).OrderBy(line => line).ToArray());

            Assert.Equal(CommandExitCodes.Success, exactExitCode);
            Assert.Equal(string.Empty, exactStderr);
            Assert.Equal(0, exactDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            Assert.Equal(0, countDocument.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_CSharpLongMultiLineCaseUsingStaticConstantPattern_NonExactKeepsRows_ExactSuppressesAll()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_long_multiline_case_constant_pattern_suppressed");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Defs.cs"),
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            var sourceBuilder = new System.Text.StringBuilder();
            sourceBuilder.AppendLine("using static Probe.Color;");
            sourceBuilder.AppendLine();
            sourceBuilder.AppendLine("namespace Probe;");
            sourceBuilder.AppendLine();
            sourceBuilder.AppendLine("class Demo");
            sourceBuilder.AppendLine("{");
            sourceBuilder.AppendLine("    void Run(object value)");
            sourceBuilder.AppendLine("    {");
            sourceBuilder.AppendLine("        switch (value)");
            sourceBuilder.AppendLine("        {");
            sourceBuilder.AppendLine("            case");
            for (var index = 0; index < 70; index++)
            {
                sourceBuilder.Append("                Red");
                sourceBuilder.AppendLine(index == 69 ? ":" : string.Empty);
                if (index < 69)
                    sourceBuilder.AppendLine("                or");
            }
            sourceBuilder.AppendLine("                break;");
            sourceBuilder.AppendLine("        }");
            sourceBuilder.AppendLine("    }");
            sourceBuilder.AppendLine("}");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Use.cs"), sourceBuilder.ToString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (nonExactExitCode, nonExactStdout, nonExactStderr) = RunReferencesInProcess(
                "Red", dbPath, "csharp", false, "--path", "src/Use.cs", "--limit", "100");
            var nonExactRows = ParseJsonLines(nonExactStdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, nonExactExitCode);
            Assert.Equal(string.Empty, nonExactStderr);
            Assert.Equal(70, nonExactRows.Count);
            Assert.Equal(Enumerable.Range(0, 70).Select(index => 12 + (index * 2)).ToArray(), nonExactRows.Select(row => row.GetProperty("line").GetInt32()).OrderBy(line => line).ToArray());
            Assert.All(nonExactRows, row => Assert.Equal("Run", row.GetProperty("container_name").GetString()));

            var (exactExitCode, exactStdout, exactStderr) = RunReferencesInProcess("Red", dbPath, "csharp");
            using var exactDocument = ParseJsonOutput(exactStdout);

            var (countExitCode, countStdout, countStderr) = RunReferencesInProcess(
                "Red", dbPath, "csharp", true, "--count");
            using var countDocument = ParseJsonOutput(countStdout);

            Assert.Equal(CommandExitCodes.Success, exactExitCode);
            Assert.Equal(string.Empty, exactStderr);
            Assert.Equal(0, exactDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            Assert.Equal(0, countDocument.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQualifiedMultiLineCaseLogicalConstantPattern_StaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_qualified_multiline_case_constant_pattern_suppressed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                namespace Probe;

                class Demo
                {
                    void Run(object value)
                    {
                        switch (value)
                        {
                            case Color.Red or
                                Color.Blue:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            foreach (var symbolName in new[] { "Red", "Blue" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                    [symbolName, "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                    _jsonOptions));

                using var document = ParseJsonOutput(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpQualifiedConstantPatternSameFileEnumMemberSitesUseMemberRead_Issue4894()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_qualified_constant_pattern_same_file_enum_member_sites_suppressed");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Use.cs"),
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }

                public class Red {}

                class Demo
                {
                    void Run(object value)
                    {
                        switch (value)
                        {
                            case Color.Red or Color.Blue:
                                break;
                        }
                    }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (referencesExitCode, referencesStdout, referencesStderr) = RunReferencesInProcess(
                "Red", dbPath, "csharp", true, "--kind", "member_read");
            using var referencesDocument = ParseJsonOutput(referencesStdout);

            var (countExitCode, countStdout, countStderr) = RunReferencesInProcess(
                "Red", dbPath, "csharp", true, "--kind", "member_read", "--count");
            using var countDocument = ParseJsonOutput(countStdout);

            var (callersExitCode, callersStdout, callersStderr) = RunCallersInProcess("Red", dbPath, "csharp");
            using var callersDocument = ParseJsonOutput(callersStdout);
            var (includedCallersExitCode, includedCallersStdout, includedCallersStderr) = RunCallersInProcess(
                "Red", dbPath, "csharp", true, "--include-member-reads");
            using var includedCallersDocument = ParseJsonOutput(includedCallersStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, referencesExitCode);
            Assert.Equal(string.Empty, referencesStderr);
            Assert.Equal("member_read", referencesDocument.RootElement.GetProperty("reference_kind").GetString());
            Assert.Equal("Run", referencesDocument.RootElement.GetProperty("container_name").GetString());

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            Assert.Equal(1, countDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, callersExitCode);
            Assert.Equal(string.Empty, callersStderr);
            Assert.Equal(0, callersDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, includedCallersExitCode);
            Assert.Equal(string.Empty, includedCallersStderr);
            Assert.Equal("member_read", includedCallersDocument.RootElement.GetProperty("reference_kind").GetString());
            Assert.Equal("Run", includedCallersDocument.RootElement.GetProperty("caller_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpLogicalPatternsKeepLaterTypeHeads()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_logical_type_pattern_all_heads");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class Outer
                {
                    public class Red {}
                    public class Blue {}
                }

                class Demo
                {
                    bool Match(object value) => value is Outer.Red or Outer.Blue;

                    void Run(object value)
                    {
                        switch (value)
                        {
                            case Outer.Red or Outer.Blue:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Blue", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionTypePatternsEmitOnlyGenuineTypeHeads()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_switch_expression_type_patterns");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class Point {}
                class Shape {}
                enum Color { Red }

                class Demo
                {
                    int Match(object value) => value switch
                    {
                        Point => 1,
                        Point or Shape => 2,
                        Color.Red => 3,
                        _ => 0,
                    };
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (pointExitCode, pointStdout, pointStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Point", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var pointRows = ParseJsonLines(pointStdout);

            Assert.Equal(CommandExitCodes.Success, pointExitCode);
            Assert.Equal(string.Empty, pointStderr);
            Assert.Equal(2, pointRows.Count);

            var (shapeExitCode, shapeStdout, shapeStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Shape", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var shapeRows = ParseJsonLines(shapeStdout);

            Assert.Equal(CommandExitCodes.Success, shapeExitCode);
            Assert.Equal(string.Empty, shapeStderr);
            Assert.Single(shapeRows);

            var (redExitCode, redStdout, redStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            using var redDocument = ParseJsonOutput(redStdout);
            var redJson = redDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, redExitCode);
            Assert.Equal(string.Empty, redStderr);
            Assert.Equal("member_read", redJson.GetProperty("reference_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionGenericTypePatternsKeepOuterTypeAndArguments()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_switch_expression_generic_type_patterns");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class Point {}
                class Shape {}
                class Wrapper<TLeft, TRight> {}

                class Demo
                {
                    int Match(object value) => value switch
                    {
                        Wrapper<Point, Shape> => 1,
                        _ => 0,
                    };
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (wrapperExitCode, wrapperStdout, wrapperStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Wrapper", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var wrapperRows = ParseJsonLines(wrapperStdout);

            var (pointExitCode, pointStdout, pointStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Point", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var pointRows = ParseJsonLines(pointStdout);

            var (shapeExitCode, shapeStdout, shapeStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Shape", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var shapeRows = ParseJsonLines(shapeStdout);

            Assert.Equal(CommandExitCodes.Success, wrapperExitCode);
            Assert.Equal(string.Empty, wrapperStderr);
            Assert.Single(wrapperRows);

            Assert.Equal(CommandExitCodes.Success, pointExitCode);
            Assert.Equal(string.Empty, pointStderr);
            Assert.Single(pointRows);

            Assert.Equal(CommandExitCodes.Success, shapeExitCode);
            Assert.Equal(string.Empty, shapeStderr);
            Assert.Single(shapeRows);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionGenericDeclarationPatternWhenGuardKeepsArmHead()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_switch_expression_generic_when_guard");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class Wrapper<TLeft, TRight> {}
                class Point { public int X { get; init; } }
                class Shape {}

                class Demo
                {
                    int Match(object value, int limit) => value switch
                    {
                        Wrapper<Point, Shape> p when p is Wrapper<Point, Shape> && limit > p.GetHashCode() => 1,
                        _ => 0,
                    };
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (wrapperExitCode, wrapperStdout, wrapperStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Wrapper", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var wrapperRows = ParseJsonLines(wrapperStdout)
                .Select(document => (
                    Line: document.RootElement.GetProperty("line").GetInt32(),
                    Column: document.RootElement.GetProperty("column").GetInt32(),
                    ContainerName: document.RootElement.GetProperty("container_name").GetString()))
                .OrderBy(row => row.Line)
                .ThenBy(row => row.Column)
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, wrapperExitCode);
            Assert.Equal(string.Empty, wrapperStderr);
            Assert.Equal([11, 11], wrapperRows.Select(row => row.Line).ToArray());
            Assert.Equal([9, 43], wrapperRows.Select(row => row.Column).ToArray());
            Assert.All(wrapperRows, row => Assert.Equal("Match", row.ContainerName));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionFunctionWhenGuardKeepsArmHead()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_switch_expression_function_when_guard");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class Wrapper<TLeft, TRight> {}
                class Point {}
                class Shape {}

                class Demo
                {
                    static bool Check(object value) => true;

                    int Match(object value) => value switch
                    {
                        Wrapper<Point, Shape> p when Check(p) => 1,
                        _ => 0,
                    };
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (wrapperExitCode, wrapperStdout, wrapperStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Wrapper", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var wrapperRows = ParseJsonLines(wrapperStdout)
                .Select(document => (
                    Line: document.RootElement.GetProperty("line").GetInt32(),
                    Column: document.RootElement.GetProperty("column").GetInt32(),
                    ContainerName: document.RootElement.GetProperty("container_name").GetString()))
                .OrderBy(row => row.Line)
                .ThenBy(row => row.Column)
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, wrapperExitCode);
            Assert.Equal(string.Empty, wrapperStderr);
            Assert.Equal([13], wrapperRows.Select(row => row.Line).ToArray());
            Assert.Equal([9], wrapperRows.Select(row => row.Column).ToArray());
            Assert.All(wrapperRows, row => Assert.Equal("Match", row.ContainerName));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionLaterArmAfterWhenGuardStillEmitsTypeHead()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_switch_expression_later_arm_after_when_guard");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class Point {}
                class Shape {}

                class Demo
                {
                    int Match(object value) => value switch
                    {
                        Point p when p.GetHashCode() > 0 => 1,
                        Shape => 2,
                        _ => 0,
                    };
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (shapeExitCode, shapeStdout, shapeStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Shape", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var shapeRows = ParseJsonLines(shapeStdout)
                .Select(document => document.RootElement.GetProperty("container_name").GetString())
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, shapeExitCode);
            Assert.Equal(string.Empty, shapeStderr);
            Assert.Equal(["Match"], shapeRows);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpVerbatimPatternTypesSurviveBareTokenFilter()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_verbatim_pattern_types");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Probe;

                class @not {}
                class @default {}

                class Demo
                {
                    bool MatchNot(object value) => value is @not;
                    bool MatchDefault(object value) => value is @default;
                    bool Guard(object value) => value is not null;
                    bool TypeOfNot() => typeof(@not) == typeof(@not);
                    bool TypeOfDefault() => typeof(@default) == typeof(@default);

                    void Run(object value)
                    {
                        switch (value)
                        {
                            case @not:
                                break;
                            case @default:
                                break;
                            case default:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (notExitCode, notStdout, notStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["not", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var notRows = ParseJsonLines(notStdout);

            var (defaultExitCode, defaultStdout, defaultStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["default", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var defaultRows = ParseJsonLines(defaultStdout);

            Assert.Equal(CommandExitCodes.Success, notExitCode);
            Assert.Equal(string.Empty, notStderr);
            Assert.Equal(4, notRows.Count);

            Assert.Equal(CommandExitCodes.Success, defaultExitCode);
            Assert.Equal(string.Empty, defaultStderr);
            Assert.Equal(4, defaultRows.Count);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticConstantPatternsDoNotLeakAcrossFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_constant_pattern_cross_file");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Point {}

                class Demo
                {
                    bool Match(object value) => value is Red or Blue or Point;

                    void Run(object value)
                    {
                        switch (value)
                        {
                            case Red:
                                break;
                            case Red or Blue:
                                break;
                            case Red or Point:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (redExitCode, redStdout, redStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            using var redDocument = ParseJsonOutput(redStdout);

            var (blueExitCode, blueStdout, blueStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Blue", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            using var blueDocument = ParseJsonOutput(blueStdout);

            var (redCountExitCode, redCountStdout, redCountStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));
            using var redCountDocument = ParseJsonOutput(redCountStdout);

            Assert.Equal(CommandExitCodes.Success, redExitCode);
            Assert.Equal(string.Empty, redStderr);
            Assert.Equal(0, redDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, blueExitCode);
            Assert.Equal(string.Empty, blueStderr);
            Assert.Equal(0, blueDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, redCountExitCode);
            Assert.Equal(string.Empty, redCountStderr);
            Assert.Equal(0, redCountDocument.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpGlobalUsingStaticConstantPatternsDoNotLeakAcrossFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_global_using_static_constant_pattern_cross_file");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/GlobalUsings.cs", "csharp",
                """
                global using static Probe.Color;
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is Red or Blue;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (redExitCode, redStdout, redStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            using var redDocument = ParseJsonOutput(redStdout);

            var (redCountExitCode, redCountStdout, redCountStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));
            using var redCountDocument = ParseJsonOutput(redCountStdout);

            Assert.Equal(CommandExitCodes.Success, redExitCode);
            Assert.Equal(string.Empty, redStderr);
            Assert.Equal(0, redDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, redCountExitCode);
            Assert.Equal(string.Empty, redCountStderr);
            Assert.Equal(0, redCountDocument.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpGlobalUsingNamespaceSameNameTypePatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_global_using_namespace_same_name_type_pattern_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/GlobalUsings.cs", "csharp",
                """
                global using RealTypes;
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/RealRed.cs", "csharp",
                """
                namespace RealTypes;

                public class Red {}
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Red", row.GetProperty("symbol_name").GetString());
            Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
            Assert.Contains("value is Red", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionLaterArmAfterWhenGuardStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_switch_expression_later_arm_after_when");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                namespace Probe;

                class Point {}
                class Shape {}

                class Demo
                {
                    int Match(object value) => value switch
                    {
                        Point p when p.GetHashCode() > 0 => 1,
                        Shape => 2,
                        _ => 0,
                    };
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Shape", dbPath, "csharp");
            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Shape", row.GetProperty("symbol_name").GetString());
            Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
            Assert.Contains("Shape => 2", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionLaterArmsAfterRelationalPatternsStayVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_switch_expression_later_arm_after_relational");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                namespace Probe;

                class Point { public int X { get; init; } }
                class Shape {}

                class Demo
                {
                    int MatchLess(object value) => value switch
                    {
                        Point { X: < 0 } => 1,
                        Shape => 2,
                        _ => 0,
                    };

                    int MatchGreater(object value) => value switch
                    {
                        Point { X: > 0 } => 1,
                        Shape => 2,
                        _ => 0,
                    };
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Shape", dbPath, "csharp");
            var rows = ParseJsonLines(stdout).Select(document => document.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Equal("Shape", row.GetProperty("symbol_name").GetString());
                Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
                Assert.Contains("Shape => 2", row.GetProperty("context").GetString(), StringComparison.Ordinal);
            });
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionLaterGenericArmsStayVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_switch_expression_later_generic_arm");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                namespace Probe;

                class Point { public int X { get; init; } }
                class Shape {}
                class Wrapper<TLeft, TRight> {}

                class Demo
                {
                    int MatchAfterGuard(object value) => value switch
                    {
                        Point p when p.GetHashCode() > 0 => 1,
                        Wrapper<Point, Shape> => 2,
                        _ => 0,
                    };

                    int MatchAfterRelational(object value) => value switch
                    {
                        Point { X: < 0 } => 1,
                        Wrapper<Point, Shape> => 2,
                        _ => 0,
                    };
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Wrapper", dbPath, "csharp");
            var rows = ParseJsonLines(stdout).Select(document => document.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Equal("Wrapper", row.GetProperty("symbol_name").GetString());
                Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
                Assert.Contains("Wrapper<Point, Shape> => 2", row.GetProperty("context").GetString(), StringComparison.Ordinal);
            });
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpCrossFileSameNamespaceTypePatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_cross_file_same_namespace_type_pattern_visible");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Use.cs"),
                """
                using static Probe.Color;

                namespace Probe;

                enum Color { Red }

                class Demo
                {
                    bool Match(object value) => value is Red;
                    void ProbeType() { _ = typeof(Red); }
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Other.cs"),
                """
                namespace Probe;

                class Red {}
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Red", dbPath, "csharp");
            var rows = ParseJsonLines(stdout).Select(line => line.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString()));
            Assert.Contains(rows, row => row.GetProperty("context").GetString()!.Contains("value is Red", StringComparison.Ordinal));
            Assert.Contains(rows, row => row.GetProperty("context").GetString()!.Contains("typeof(Red)", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingAliasDoesNotRescueUnqualifiedTypePattern()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_alias_does_not_rescue_unqualified_type_pattern");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Shadow.cs", "csharp",
                """
                namespace Shadow;

                public class Red {}
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;
                using Shadow = Probe;

                namespace Real;

                class Demo
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpUsingNamespaceImportPatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_namespace_import_pattern_visible");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Repro.cs"),
                """
                using static Probe.Color;
                using RealTypes;

                namespace Probe
                {
                    enum Color { Red }

                    class Demo
                    {
                        bool Match(object value) => value is Red;
                    }
                }

                namespace RealTypes
                {
                    class Red {}
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Red", dbPath, "csharp");
            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
            Assert.Contains("value is Red", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpCrossFileFileTypeDoesNotRescueUnqualifiedTypePattern()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_cross_file_file_type_does_not_rescue_unqualified_type_pattern");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FileLocal.cs", "csharp",
                """
                namespace Probe;

                file class Red {}
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpSameFileFileTypePatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_same_file_file_type_pattern_visible");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Repro.cs"),
                """
                using static Probe.Color;

                namespace Probe;

                public enum Color
                {
                    Red
                }

                file class Red {}

                class Demo
                {
                    bool Match(object value) => value is Red;
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Red", dbPath, "csharp");
            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
            Assert.Contains("value is Red", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticConstantPatternsIgnoreTriviaAroundKeywords()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_constant_pattern_ignores_trivia");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool MatchTab(object value) => value is	Red;
                    bool MatchComment(object value) => value is/*comment*/Red;

                    void Run(object value)
                    {
                        switch (value)
                        {
                            case	Red:
                                break;
                            case/*comment*/Red:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticConstantPatternsUseMatchedColumnOnSharedLine()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_constant_pattern_column_sensitive");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/GlobalUsings.cs", "csharp",
                """
                global using static Probe.Color;

                namespace Probe;

                enum Color { Red, Blue }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                namespace Probe;

                class Demo
                {
                    string Run(object value) => nameof(Red) + (value is Red).ToString();
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var rows = ParseJsonLines(stdout).Select(line => line.RootElement).ToList();
            var row = Assert.Single(rows);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
            Assert.Equal(40, row.GetProperty("column").GetInt32());
            Assert.Contains("nameof(Red)", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticConstantPatternsPreserveTypeAliasPatterns()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_constant_pattern_type_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe
                {
                    public enum Color
                    {
                        Red
                    }

                    namespace Real
                    {
                        public class Red {}
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;
                using Red = Probe.Real.Red;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
            Assert.Contains("value is Red", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticConstantPatternsPreserveTypeAliasPatternAcrossNamespaces()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_constant_pattern_type_alias_across_namespaces");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }

                namespace Shapes
                {
                    public class Red {}
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;
                using Red = Probe.Shapes.Red;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
            Assert.Contains("value is Red", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpUsingStaticConstantPatternsStaySuppressedWhenContextClamped()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_constant_pattern_clamped");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Defs.cs"),
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Use.cs"),
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value)
                    {
                        return value is Red;
                    }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess(
                "Red", dbPath, "csharp", true, "--max-line-width", "8");

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticSameNamespaceTypePatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_same_namespace_type_pattern_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }

                public class Red {}
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Red", row.GetProperty("symbol_name").GetString());
            Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
            Assert.Contains("value is Red", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticSameNamespaceTypeofStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_same_namespace_typeof_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }

                public class Red {}
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    void Match()
                    {
                        _ = typeof(Red);
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));
            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Red", row.GetProperty("symbol_name").GetString());
            Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
            Assert.Contains("typeof(Red)", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void RunReferences_ExactJson_CSharpUsingStaticNestedSameNameTypePatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_nested_same_name_type_pattern_visible");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                """
                using static Probe.Color;

                namespace Probe;

                enum Color
                {
                    Red
                }

                class Outer
                {
                    class Red {}

                    bool Match(object value) => value is Red;

                    void Run()
                    {
                        _ = typeof(Red);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunReferencesInProcess("Red", dbPath, "csharp");

            var rows = ParseJsonLines(stdout).Select(doc => doc.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row => row.GetProperty("context").GetString()!.Contains("value is Red", StringComparison.Ordinal));
            Assert.Contains(rows, row => row.GetProperty("context").GetString()!.Contains("typeof(Red)", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticTopLevelSameNameTypePatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_top_level_same_name_type_pattern_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }

                public class Red {}
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is Red;

                    bool Switch(object value) => value switch
                    {
                        Red => true,
                        _ => false,
                    };
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout).Select(doc => doc.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row => row.GetProperty("context").GetString()!.Contains("value is Red", StringComparison.Ordinal));
            Assert.Contains(rows, row => row.GetProperty("context").GetString()!.Contains("Red => true", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticTopLevelSameNameTypePatternStaysSuppressedWithoutType()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_top_level_same_name_type_pattern_suppressed_without_type");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticInheritedProtectedNestedTypePatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_inherited_protected_nested_type_pattern_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }

                public class Base
                {
                    protected class Red {}
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Derived : Base
                {
                    bool Match(object value) => value is Red;

                    void ProbeType()
                    {
                        _ = typeof(Red);
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout).Select(doc => doc.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row => row.GetProperty("context").GetString()!.Contains("value is Red", StringComparison.Ordinal));
            Assert.Contains(rows, row => row.GetProperty("context").GetString()!.Contains("typeof(Red)", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticInheritedConstantOnlyPatternStaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_inherited_constant_only_pattern_suppressed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }

                public class Base {}
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Derived : Base
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticImplementedInterfaceNestedTypeStaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_interface_nested_type_suppressed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }

                public interface IBase
                {
                    public class Red {}
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Derived : IBase
                {
                    bool Match(object value) => value is Red;

                    void ProbeType()
                    {
                        _ = typeof(Red);
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactCountJson_CSharpUsingStaticImplementedInterfaceNestedTypeStaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_interface_nested_type_count_suppressed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }

                public interface IBase
                {
                    public class Red {}
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Derived : IBase
                {
                    bool Match(object value) => value is Red;

                    void ProbeType()
                    {
                        _ = typeof(Red);
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticInheritedProtectedNestedTypeViaTypeAliasPatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_inherited_protected_nested_type_type_alias_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace BaseNs;

                public class Base
                {
                    protected class Red {}
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using BaseAlias = BaseNs.Base;
                using static Probe.Color;

                namespace Probe;

                public enum Color
                {
                    Red
                }

                class Derived : BaseAlias
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout).Select(doc => doc.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Contains("value is Red", rows[0].GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactCountJson_CSharpUsingStaticInheritedProtectedNestedTypeViaTypeAliasPatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_inherited_protected_nested_type_type_alias_count_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace BaseNs;

                public class Base
                {
                    protected class Red {}
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using BaseAlias = BaseNs.Base;
                using static Probe.Color;

                namespace Probe;

                public enum Color
                {
                    Red
                }

                class Derived : BaseAlias
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticInheritedProtectedNestedTypeViaNamespaceAliasPatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_inherited_protected_nested_type_namespace_alias_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace BaseNs;

                public class Base
                {
                    protected class Red {}
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using NsAlias = BaseNs;
                using static Probe.Color;

                namespace Probe;

                public enum Color
                {
                    Red
                }

                class Derived : NsAlias.Base
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout).Select(doc => doc.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Contains("value is Red", rows[0].GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactCountJson_CSharpUsingStaticInheritedProtectedNestedTypeViaNamespaceAliasPatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_inherited_protected_nested_type_namespace_alias_count_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace BaseNs;

                public class Base
                {
                    protected class Red {}
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using NsAlias = BaseNs;
                using static Probe.Color;

                namespace Probe;

                public enum Color
                {
                    Red
                }

                class Derived : NsAlias.Base
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticInheritedNestedTypeViaConstructedGenericTypeAliasPatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_inherited_nested_type_generic_type_alias_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Repro.cs", "csharp",
                """
                using static Probe.Color;
                using AliasBase = Probe.Base<int>;

                namespace Probe;

                enum Color { Red }

                class Base<T>
                {
                    public class Red {}
                }

                class Derived : AliasBase
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout).Select(doc => doc.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Contains("value is Red", rows[0].GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactCountJson_CSharpUsingStaticInheritedNestedTypeViaConstructedGenericTypeAliasPatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_inherited_nested_type_generic_type_alias_count_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Repro.cs", "csharp",
                """
                using static Probe.Color;
                using AliasBase = Probe.Base<int>;

                namespace Probe;

                enum Color { Red }

                class Base<T>
                {
                    public class Red {}
                }

                class Derived : AliasBase
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticInheritedNestedTypeViaGlobalConstructedGenericTypeAliasPatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_inherited_nested_type_global_generic_type_alias_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/GlobalUsings.cs", "csharp",
                """
                global using AliasBase = Probe.Base<int>;
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Repro.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                enum Color { Red }

                class Base<T>
                {
                    public class Red {}
                }

                class Derived : AliasBase
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout).Select(doc => doc.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Contains("value is Red", rows[0].GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactCountJson_CSharpUsingStaticInheritedNestedTypeViaGlobalConstructedGenericTypeAliasPatternStaysVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_inherited_nested_type_global_generic_type_alias_count_visible");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/GlobalUsings.cs", "csharp",
                """
                global using AliasBase = Probe.Base<int>;
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Repro.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                enum Color { Red }

                class Base<T>
                {
                    public class Red {}
                }

                class Derived : AliasBase
                {
                    bool Match(object value) => value is Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticMultilineLogicalConstantPatternStaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_multiline_logical_constant_pattern_suppressed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is
                        Red or Blue;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            foreach (var symbolName in new[] { "Red", "Blue" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                    [symbolName, "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                    _jsonOptions));

                using var document = ParseJsonOutput(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactCountJson_CSharpUsingStaticMultilineLogicalConstantPatternStaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_multiline_logical_constant_pattern_count_suppressed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is
                        Red or Blue;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            foreach (var symbolName in new[] { "Red", "Blue" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                    [symbolName, "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                    _jsonOptions));

                using var document = ParseJsonOutput(stdout);
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, json.GetProperty("count").GetInt32());
                Assert.Equal(0, json.GetProperty("files").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticLongMultilineCaseConstantPatternStaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_long_multiline_case_constant_pattern_suppressed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    void Run(object value)
                    {
                        switch (value)
                        {
                            case
                                Red
                                or
                                Red
                                or
                                Red
                                or
                                Red:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactCountJson_CSharpUsingStaticLongMultilineCaseConstantPatternStaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_long_multiline_case_constant_pattern_count_suppressed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    void Run(object value)
                    {
                        switch (value)
                        {
                            case
                                Red
                                or
                                Red
                                or
                                Red
                                or
                                Red:
                                break;
                        }
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpGlobalQualifiedUsingAliasNameDoesNotCreateReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_global_alias_name_invalid");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Color
                {
                    Red
                }

                using Color = Demo.Color;

                class C
                {
                    void M()
                    {
                        _ = global::Color.Red;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactCountJson_PathScopeDoesNotInheritOutOfScopeEnumMemberMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_references_scoped_js");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "web/app.js", "javascript",
                """
                function Ready() {
                }

                Ready();
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "cs/status.cs", "csharp",
                """
                public enum Status { Ready }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "javascript", "--exact-name", "--path", "web/", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactOnReadOnlyLegacyDb_WarnsAboutMissingIndex()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_exact_warn");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/session.py", "python", "def login(user, password):\n    return Run(user)\n");
            DropGraphExactFallbackIndexes(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Run", "--db", readOnlyUri, "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("src/session.py:2:12", stdout);
            Assert.Contains("WARN: --exact graph query ran without the supporting index", stderr);
            Assert.Contains("idx_symbol_refs_name_nocase_file", stderr);
            Assert.Contains("re-index with `cdidx index <projectPath>`", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallees_ExactJsonOnReadOnlyLegacyDb_IncludesExactIndexSignal()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_exact_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/session.py", "python", "def login(user, password):\n    return Run(user)\n");
            DropGraphExactFallbackIndexes(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["login", "--db", readOnlyUri, "--exact", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Run", json.GetProperty("callee_name").GetString());
            Assert.False(json.GetProperty("exact_index_available").GetBoolean());
            Assert.Contains("idx_symbol_refs_container_nocase_kind", json.GetProperty("degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactZeroHumanOutput_PrintsExactZeroHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_refs_exact_zero");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                public class App
                {
                    public void HandleRequest() { }
                    public void HandleRequestAsync() { HandleRequest(); }
                }
                """);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
            }

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Handle", "--db", dbPath, "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("No references found.", stderr);
            Assert.Contains("--exact found 0 matches, but substring matching would return 1", stderr);
            Assert.Contains("`HandleRequest`", stderr);
            Assert.Contains("Drop --exact or use the exact indexed name.", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactWithoutGraphTable_DoesNotClaimSlowButCorrect()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_missing_graph");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Run", "--db", dbPath, "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.DoesNotContain("Results are correct but may be slow", stderr);
            Assert.Contains("symbol_references table missing", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactCountWithoutGraphTable_WarnsCountIsDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_missing_graph_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Run", "--db", dbPath, "--exact", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("0", stdout.Trim());
            Assert.DoesNotContain("Results are correct but may be slow", stderr);
            Assert.Contains("count result is degraded, not authoritative", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_JavaModuleInfoDirectivesReturnModuleEdges()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_java_module_references");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "module-info.java"),
                """
                module com.example.app {
                    requires java.base;
                    requires transitive java.logging;
                    uses com.example.spi.MyService;
                    provides com.example.spi.MyService with com.example.impl.DefaultService;
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            var (javaBaseExitCode, javaBaseStdout, javaBaseStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["java.base", "--db", dbPath, "--json", "--lang", "java", "--exact-name"],
                _jsonOptions));
            var javaBaseRows = ParseJsonLines(javaBaseStdout)
                .Select(document => document.RootElement)
                .ToList();

            var (javaLoggingExitCode, javaLoggingStdout, javaLoggingStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["java.logging", "--db", dbPath, "--json", "--lang", "java", "--exact-name"],
                _jsonOptions));
            var javaLoggingRows = ParseJsonLines(javaLoggingStdout)
                .Select(document => document.RootElement)
                .ToList();

            var (serviceExitCode, serviceStdout, serviceStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["com.example.spi.MyService", "--db", dbPath, "--json", "--lang", "java", "--exact-name"],
                _jsonOptions));
            var serviceRows = ParseJsonLines(serviceStdout)
                .Select(document => document.RootElement)
                .ToList();

            var (implementationExitCode, implementationStdout, implementationStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["com.example.impl.DefaultService", "--db", dbPath, "--json", "--lang", "java", "--exact-name"],
                _jsonOptions));
            var implementationRows = ParseJsonLines(implementationStdout)
                .Select(document => document.RootElement)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, javaBaseExitCode);
            Assert.Equal(string.Empty, javaBaseStderr);
            var javaBaseRow = Assert.Single(javaBaseRows);
            Assert.Equal("type_reference", javaBaseRow.GetProperty("reference_kind").GetString());
            Assert.Equal("com.example.app", javaBaseRow.GetProperty("container_name").GetString());

            Assert.Equal(CommandExitCodes.Success, javaLoggingExitCode);
            Assert.Equal(string.Empty, javaLoggingStderr);
            var javaLoggingRow = Assert.Single(javaLoggingRows);
            Assert.Equal("type_reference", javaLoggingRow.GetProperty("reference_kind").GetString());
            Assert.Equal("com.example.app", javaLoggingRow.GetProperty("container_name").GetString());

            Assert.Equal(CommandExitCodes.Success, serviceExitCode);
            Assert.Equal(string.Empty, serviceStderr);
            Assert.Equal(2, serviceRows.Count);
            Assert.All(serviceRows, row =>
            {
                Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
                Assert.Equal("com.example.app", row.GetProperty("container_name").GetString());
            });

            Assert.Equal(CommandExitCodes.Success, implementationExitCode);
            Assert.Equal(string.Empty, implementationStderr);
            var implementationRow = Assert.Single(implementationRows);
            Assert.Equal("type_reference", implementationRow.GetProperty("reference_kind").GetString());
            Assert.Equal("com.example.app", implementationRow.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticSingleLineConstantPattern_StaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_single_line_constant_pattern_suppressed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is Red or Blue;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            foreach (var symbolName in new[] { "Red", "Blue" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                    [symbolName, "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                    _jsonOptions));

                using var document = ParseJsonOutput(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpUsingStaticMultilineConstantPattern_StaysSuppressed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_multiline_constant_pattern_suppressed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is
                        Red or Blue;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            foreach (var symbolName in new[] { "Red", "Blue" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                    [symbolName, "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                    _jsonOptions));

                using var document = ParseJsonOutput(stdout);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_FuzzyJson_CSharpUsingStaticConstantPattern_RemainsSearchable()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_using_static_fuzzy_constant_pattern_searchable");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Defs.cs", "csharp",
                """
                namespace Probe;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                using static Probe.Color;

                namespace Probe;

                class Demo
                {
                    bool Match(object value) => value is Red or Blue;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Re", "--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Red", document.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("type_reference", document.RootElement.GetProperty("reference_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
