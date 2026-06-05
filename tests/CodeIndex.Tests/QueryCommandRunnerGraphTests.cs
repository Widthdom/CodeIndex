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

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public void RunReferences_RejectsNegativeOrNonNumericMaxLineWidthValue(string invalidValue)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_references_invalid_max_line_width_{invalidValue}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["target", "--db", dbPath, "--max-line-width", invalidValue, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--max-line-width requires an integer between 0 and 4096", stderr);
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
                "int Login(int user)");
            AssertBodyExcerpt(
                QueryCommandRunner.RunCallers,
                ["Run", "--db", dbPath, "--json", "--body", "--snippet-lines", "2"],
                "int Login(int user)");
            AssertBodyExcerpt(
                QueryCommandRunner.RunCallees,
                ["Login", "--db", dbPath, "--json", "--body", "--snippet-lines", "1"],
                "int Run(int user)");

            var (impactExitCode, impactStdout, impactStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Run", "--db", dbPath, "--json", "--body", "--snippet-lines", "2"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, impactExitCode);
            Assert.Equal(string.Empty, impactStderr);
            using var impactDocument = ParseJsonOutput(impactStdout);
            var impactCaller = impactDocument.RootElement.GetProperty("callers")[0];
            Assert.Contains("int Login(int user)", impactCaller.GetProperty("body_content").GetString());
            Assert.Equal(2, CountLines(impactCaller.GetProperty("body_content").GetString()!));
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
            using (var db = new DbContext(dbPath))
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

            using (var db = new DbContext(dbPath))
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
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            var references = ParseJsonLines(stdout).Select(line => line.RootElement).ToList();
            Assert.Single(references);
            var reference = references[0];
            Assert.Equal("Red", reference.GetProperty("symbol_name").GetString());
            Assert.Equal("call", reference.GetProperty("reference_kind").GetString());
            Assert.Equal("Match", reference.GetProperty("container_name").GetString());
            Assert.Contains("value is Color.Red or Color.Blue;", reference.GetProperty("context").GetString());

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
    public void RunReferences_JsonClampsLongSingleLineContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_long_line");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longLine = "const x = 0; " + new string('a', 320) + " target(); " + new string('b', 320);
            using (var db = new DbContext(dbPath))
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
    public void RunReferences_ExactJson_CSharpInterpolatedRawStringPreservesCallSite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_csharp_interpolated_raw");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "app.cs"),
                """"
                public class App
                {
                    private string Run() => "ok";

                    public string Render()
                    {
                        return $"""
                            value = {Run()}
                            literal = function main()
                        """;
                    }
                }
                """");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Run", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Run", json.GetProperty("symbol_name").GetString());
            Assert.Equal("src/app.cs", json.GetProperty("path").GetString());
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Render", json.GetProperty("container_name").GetString());
            Assert.Equal(8, json.GetProperty("line").GetInt32());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpNestedInterpolatedStringInsideRawInterpolationPreservesCallSite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_csharp_nested_interpolated_raw");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "app.cs"),
                """"
                public class App
                {
                    private string Run() => "ok";

                    public string Render()
                    {
                        return $"""
                            value = {$"{Run()}"}
                            literal = function main()
                        """;
                    }
                }
                """");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Run", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Run", json.GetProperty("symbol_name").GetString());
            Assert.Equal("src/app.cs", json.GetProperty("path").GetString());
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Render", json.GetProperty("container_name").GetString());
            Assert.Equal(8, json.GetProperty("line").GetInt32());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_Json_CSharpInterpolatedVerbatimStringEscapedBracesDoNotCreatePhantomReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_csharp_escaped_verbatim_braces");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "app.cs"),
                """
                public class App
                {
                    public string Render()
                    {
                        return $@"{{Run()}}";
                    }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Run", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("references").GetArrayLength());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
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
    public void RunCallers_ExactJson_FindsTernaryContinuationCallSite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_csharp_ternary");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "dispatcher.cs"),
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

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["RunUpdateMode", "--db", Path.Combine(projectRoot, ".cdidx", "codeindex.db"), "--json", "--exact", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/dispatcher.cs", json.GetProperty("path").GetString());
            // With #233 fixed, the expression-bodied `Select` method spans its declaration
            // through the terminating ';' (multi-line ternary on the RHS of `=>`), so the
            // RunUpdateMode call at line 5 attributes to Select, not the enclosing class.
            // #233 修正により、`=>` で始まる式本体メソッド `Select` の範囲が宣言行から
            // 末尾 `;` までに広がり、line 5 の RunUpdateMode 呼び出しは外側クラスではなく
            // Select に帰属する。
            Assert.Equal("function", json.GetProperty("caller_kind").GetString());
            Assert.Equal("Select", json.GetProperty("caller_name").GetString());
            Assert.Equal("RunUpdateMode", json.GetProperty("callee_name").GetString());
            Assert.Equal(5, json.GetProperty("first_line").GetInt32());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_FindsCallerInsideAllmanStyleBlockBodyProperty()
    {
        // issue #233 review follow-up: Allman-style (next-line `{`) block-bodied C#
        // properties were not extracted as symbols, so accessor-internal calls fell
        // through to the enclosing class. End-to-end verify that `callers` attributes
        // the call to the property itself once the extraction regex handles this shape.
        // issue #233 のレビュー指摘: Allman スタイル（次行 `{`）の block-bodied プロパティが
        // 抽出されておらず、accessor 内部の呼び出しが外側クラスに帰属していた。抽出 regex が
        // この形を扱えるようになった後、`callers` が property に帰属することを end-to-end で確認する。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_csharp_allman_prop");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "calc.cs"),
                """
                public class Calc
                {
                    public int Compute() => 42;

                    public int Wrap
                    {
                        get { return Compute(); }
                    }
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Compute", "--db", Path.Combine(projectRoot, ".cdidx", "codeindex.db"), "--json", "--exact", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/calc.cs", json.GetProperty("path").GetString());
            Assert.Equal("property", json.GetProperty("caller_kind").GetString());
            Assert.Equal("Wrap", json.GetProperty("caller_name").GetString());
            Assert.Equal("Compute", json.GetProperty("callee_name").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_FindsCallerInsideMultiLineExpressionBodiedProperty()
    {
        // issue #233 second review follow-up: expression-bodied properties split across
        // two lines (declaration + `=> expr;` continuation) must still attribute
        // accessor-internal calls to the property through the CLI `callers` command.
        // issue #233 の再レビュー指摘: 宣言行の次行に `=> expr;` が続く multi-line 式本体
        // プロパティでも、CLI `callers` で accessor 内呼び出しが property に帰属すること。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_csharp_ml_exprprop");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "calc.cs"),
                """
                public class Calc
                {
                    public int Compute() => 42;
                    public int Wrap
                        => Compute();
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Compute", "--db", Path.Combine(projectRoot, ".cdidx", "codeindex.db"), "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/calc.cs", json.GetProperty("path").GetString());
            Assert.Equal("property", json.GetProperty("caller_kind").GetString());
            Assert.Equal("Wrap", json.GetProperty("caller_name").GetString());
            Assert.Equal("Compute", json.GetProperty("callee_name").GetString());
            Assert.Equal(5, json.GetProperty("first_line").GetInt32());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_FindsCallerInsideBraceSameLineAccessorNextLineProperty()
    {
        // issue #233 fifth review follow-up: the common Microsoft-style block-bodied
        // property (`{` on the header line, accessor on the following line) must have
        // CLI `callers` attribute the accessor call to the property itself.
        // issue #233 第5次レビュー指摘: `{` が宣言行末にあり、accessor が次行にある
        // 標準的な block-bodied property でも、CLI `callers` は accessor 内部の呼び出しを
        // property に帰属させなければならない。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_csharp_brace_same_line");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "calc.cs"),
                """
                public class Calc
                {
                    public int Compute() => 42;

                    public int Wrap {
                        get { return Compute(); }
                    }
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Compute", "--db", Path.Combine(projectRoot, ".cdidx", "codeindex.db"), "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/calc.cs", json.GetProperty("path").GetString());
            Assert.Equal("property", json.GetProperty("caller_kind").GetString());
            Assert.Equal("Wrap", json.GetProperty("caller_name").GetString());
            Assert.Equal("Compute", json.GetProperty("callee_name").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_FindsCallerInsideAllmanPropertyWithBlockComment()
    {
        // issue #233 fourth review follow-up: a multi-line /* ... */ block comment
        // between the property header line and its `{` must not prevent CLI `callers`
        // from attributing accessor-internal calls to the property itself.
        // issue #233 の 4 回目レビュー指摘: property のヘッダ行と `{` の間に複数行の
        // /* ... */ ブロックコメントが入っていても、CLI `callers` は accessor 内部の
        // 呼び出しを外側クラスではなく property に帰属させなければならない。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_csharp_allman_prop_cmt");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "calc.cs"),
                """
                public class Calc
                {
                    public int Compute() => 42;

                    public int Wrap
                    /* some multi-line
                       block comment */
                    {
                        get { return Compute(); }
                    }
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Compute", "--db", Path.Combine(projectRoot, ".cdidx", "codeindex.db"), "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/calc.cs", json.GetProperty("path").GetString());
            Assert.Equal("property", json.GetProperty("caller_kind").GetString());
            Assert.Equal("Wrap", json.GetProperty("caller_name").GetString());
            Assert.Equal("Compute", json.GetProperty("callee_name").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_FindsCallerInsideMultiLineExpressionPropertyWithBlockComment()
    {
        // issue #233 fourth review follow-up: a multi-line /* ... */ block comment
        // between the property header line and its `=>` continuation must not prevent
        // CLI `callers` from attributing the expression-body call to the property itself.
        // issue #233 の 4 回目レビュー指摘: property のヘッダ行と `=>` 継続行の間に
        // 複数行の /* ... */ ブロックコメントが入っていても、CLI `callers` は式本体の
        // 呼び出しを外側クラスではなく property に帰属させなければならない。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_csharp_ml_exprprop_cmt");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "calc.cs"),
                """
                public class Calc
                {
                    public int Compute() => 42;

                    public int Wrap
                    /* multi-line
                       comment */
                        => Compute();
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Compute", "--db", Path.Combine(projectRoot, ".cdidx", "codeindex.db"), "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/calc.cs", json.GetProperty("path").GetString());
            Assert.Equal("property", json.GetProperty("caller_kind").GetString());
            Assert.Equal("Wrap", json.GetProperty("caller_name").GetString());
            Assert.Equal("Compute", json.GetProperty("callee_name").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
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
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
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
    public void RunCallers_ExactJson_MultiLineSwitchArm_AttributesToEnclosingFunction()
    {
        // issue #233 third review follow-up: inside a switch expression whose `=>` is
        // placed on a continuation line, calls from the arm body must still attribute to
        // the enclosing function. If the switch-expression guard does not cover the
        // continuation `=>`, the pattern variable is emitted as a phantom property and
        // `callers Trim` would return caller_kind=property, caller_name=text.
        // issue #233 第3次レビュー指摘: switch expression arm の `=>` が継続行にある場合でも、
        // arm 本体の呼び出しは外側関数に帰属しなければならない。継続 `=>` まで switch-expression
        // ガードを広げないと、パターン変数が phantom property になり、`callers Trim` が
        // caller_kind=property, caller_name=text を返してしまう。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_csharp_ml_switch_arm");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "sample.cs"),
                """
                class C
                {
                    string M(object o)
                    {
                        return o switch
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["Trim", "--db", Path.Combine(projectRoot, ".cdidx", "codeindex.db"), "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/sample.cs", json.GetProperty("path").GetString());
            Assert.Equal("function", json.GetProperty("caller_kind").GetString());
            Assert.Equal("M", json.GetProperty("caller_name").GetString());
            Assert.Equal("Trim", json.GetProperty("callee_name").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
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
    public void RunReferences_ExactJson_StaleSqlGraphContractIncludesDegradedState()
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
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_JsonResults_StaleSqlGraphContractIncludesDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_sql_graph_contract_results");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["dbo.fn_Target", "--db", dbPath, "--json", "--lang", "sql"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("fn_Target", json.GetProperty("symbol_name").GetString());
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_JsonResults_StaleSqlGraphContractIncludesDegradedState()
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
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactJson_MixedRepoStaleSqlGraphContractDoesNotDegradePureCSharpQuery()
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
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCallers_ExactCountJson_MixedRepoStaleSqlGraphContractDoesNotDegradePureCSharpQuery()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callers_mixed_sql_graph_contract_count_pure_csharp");
        try
        {
            var dbPath = CreateMixedSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["N", "--db", dbPath, "--json", "--exact", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
            Assert.False(json.TryGetProperty("sql_graph_contract_ready", out _));
            Assert.False(json.TryGetProperty("sql_graph_contract_degraded_reason", out _));
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
    public void RunCallees_JsonResults_StaleSqlGraphContractIncludesDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_callees_sql_graph_contract_results");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["dbo.usp_Caller", "--db", dbPath, "--json", "--lang", "sql"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("dbo.usp_Caller", json.GetProperty("caller_name").GetString());
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
            using (var db = new DbContext(dbPath))
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
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
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
    public void RunReferences_UnsupportedLanguageWithoutMatches_PrintsGraphSupportHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_refs");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["MissingSymbol", "--db", dbPath, "--lang", "markdown"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("No references found.", stderr);
            Assert.Contains("call-graph queries are not indexed for 'markdown'", stderr);
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
            Assert.Equal("event", json.GetProperty("reference_kind").GetString());
            Assert.Equal(1, json.GetProperty("reference_count").GetInt32());
            // #501: every grouped caller row carries `reference_kinds` +
            // `has_mixed_reference_kinds`, even when the row is single-kind, so AI
            // clients never have to guess whether the field was omitted vs empty.
            // #501: グループ化された caller 行は single-kind でも必ず `reference_kinds` /
            // `has_mixed_reference_kinds` を返すため、AI クライアントは「未出力」と
            // 「空配列」を判別せずに済む。
            var kinds = json.GetProperty("reference_kinds").EnumerateArray().Select(k => k.GetString()).ToArray();
            Assert.Equal(new[] { "event" }, kinds);
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
            Assert.Equal("event", json.GetProperty("reference_kind").GetString());

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
            Assert.Equal(new[] { "event", "invoke" }, kinds);
            Assert.Equal(1, json.GetProperty("reference_kind_counts").GetProperty("call").GetInt32());
            Assert.Equal(1, json.GetProperty("reference_kind_counts").GetProperty("subscribe").GetInt32());
            Assert.Equal("event", json.GetProperty("reference_kind").GetString());

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
                ["BaseWidget", "--db", dbPath, "--lang", "csharp", "--exact"],
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
            Assert.Equal("event", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
                ["A", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
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
            Assert.Equal("invoke", json.GetProperty("reference_kind").GetString());
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
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal(35, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpLaterSiblingAliasRebindingDoesNotStealEarlierEnumScope()
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
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal(22, json.GetProperty("line").GetInt32());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableNamedLikeEnum_OrderByCommaDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_orderby_comma_collision");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        return from Status in items
                               orderby Status, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableMemberNamedSelectDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_orderby_member_named_select_collision");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                    public int select { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        return from Status in items
                               orderby Status.select, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableOrderByAnonymousTypeCommaDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_orderby_anonymous_type_collision");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        return from Status in items
                               orderby new { X = Status.Ready, Y = items.Count() }, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableNestedQueryBeforeOrderByCommaDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_nested_query_orderby_collision");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items, IEnumerable<int> others)
                    {
                        return from Status in items
                               let nested = from x in others select x
                               orderby items.Count(), nested.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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
    public void RunReferences_ExactJson_CSharpParenthesizedTerminalSelectInArgumentPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_terminal_select");
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
                    public Status Read(IEnumerable<Holder> items)
                    {
                        return Sink.Pick(from Status in items select(Status.Ready), Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpGroupByQueryInArgumentPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_group_by_argument");
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

            var rows = ParseJsonLines(stdout);
            var row = Assert.Single(rows).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Ready", row.GetProperty("symbol_name").GetString());
            Assert.Equal(25, row.GetProperty("line").GetInt32());
            Assert.Contains("Status.Ready", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpParenthesizedGroupByQueryInArgumentPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_group_by_argument");
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
                    public Status Read(IEnumerable<Holder> items)
                    {
                        return Sink.Pick(from Status in items group(Status.Ready) by items.Count(), Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryKeywordNamedLocalFunctionInParenthesizedOrderByExpressionDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_keyword_local_function");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        static int select(IEnumerable<Holder> xs) => xs.Count();
                        return from Status in items
                               orderby select(items), items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryKeywordNamedLocalFunctionAfterGreaterThanInOrderByTernaryDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_greater_than_local_function");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        static int select(IEnumerable<Holder> xs) => xs.Count();
                        return from Status in items
                               orderby items.Count() > select(items) ? 1 : 0, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryKeywordNamedLocalFunctionAfterLessThanInOrderByTernaryDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_less_than_local_function");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        static int select(IEnumerable<Holder> xs) => xs.Count();
                        return from Status in items
                               orderby items.Count() < select(items) ? 1 : 0, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryKeywordNamedLocalFunctionAfterBangInOrderByTernaryDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_bang_local_function");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        static bool select(IEnumerable<Holder> xs) => xs.Any();
                        return from Status in items
                               orderby ! select(items) ? 1 : 0, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpAwaitBeforeQueryKeywordNamedLocalFunctionInOrderByDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_await_local_function");
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
                               orderby await select(items), items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpPostfixNullForgivingBeforeParenthesizedTerminalSelectPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_terminal_select_after_null_forgiving");
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
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpThrowBeforeQueryKeywordNamedLocalFunctionInOrderByDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_throw_select_local_function");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        static System.Exception select(IEnumerable<Holder> xs) => new System.Exception(xs.Count().ToString());
                        return from Status in items
                               orderby items.Count() > 0 ? throw select(items) : 0, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpThrowBeforeGroupNamedLocalFunctionInOrderByDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_throw_group_local_function");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        static System.Exception group(IEnumerable<Holder> xs) => new System.Exception(xs.Count().ToString());
                        return from Status in items
                               orderby items.Count() > 0 ? throw group(items) : 0, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpMultilineParenthesizedSelectAfterGreaterThanDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_multiline_select_after_greater_than");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        static int select(IEnumerable<Holder> xs) => xs.Count();
                        return from Status in items
                               orderby items.Count() >
                                       select
                                       (items) ? 1 : 0, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CssScssVariableAndExtendReferences_AreVisible()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_css_scss_variable_and_extend_references");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "styles"));
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

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (primaryExitCode, primaryStdout, primaryStderr) = RunBuiltCli(["references", "$primary", "--db", dbPath, "--json", "--lang", "css", "--exact-name"]);
            var (spacingExitCode, spacingStdout, spacingStderr) = RunBuiltCli(["references", "spacing-base", "--db", dbPath, "--json", "--lang", "css", "--exact-name"]);
            var (buttonExitCode, buttonStdout, buttonStderr) = RunBuiltCli(["references", "%button-base", "--db", dbPath, "--json", "--lang", "css", "--exact-name"]);
            var (radiusExitCode, radiusStdout, radiusStderr) = RunBuiltCli(["references", "radius", "--db", dbPath, "--json", "--lang", "css", "--exact-name"]);

            var primaryRows = ParseJsonLines(primaryStdout);
            var spacingRows = ParseJsonLines(spacingStdout);
            var buttonRows = ParseJsonLines(buttonStdout);
            var radiusRows = ParseJsonLines(radiusStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, primaryExitCode);
            Assert.Equal(string.Empty, primaryStderr);
            Assert.Equal(2, primaryRows.Count);
            Assert.All(primaryRows, row => Assert.Equal("primary", row.RootElement.GetProperty("symbol_name").GetString()));
            Assert.All(primaryRows, row => Assert.Equal("call", row.RootElement.GetProperty("reference_kind").GetString()));

            Assert.Equal(CommandExitCodes.Success, spacingExitCode);
            Assert.Equal(string.Empty, spacingStderr);
            var spacingRow = Assert.Single(spacingRows);
            Assert.Equal("spacing-base", spacingRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("call", spacingRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, buttonExitCode);
            Assert.Equal(string.Empty, buttonStderr);
            var buttonRow = Assert.Single(buttonRows);
            Assert.Equal("%button-base", buttonRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("call", buttonRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, radiusExitCode);
            Assert.Equal(string.Empty, radiusStderr);
            var radiusRow = Assert.Single(radiusRows);
            Assert.Equal("radius", radiusRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("call", radiusRow.RootElement.GetProperty("reference_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
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
            var (auditExitCode, auditStdout, auditStderr) = RunBuiltCli(["references", "audit_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (insertedExitCode, insertedStdout, insertedStderr) = RunBuiltCli(["references", "inserted_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (insertedBatchExitCode, insertedBatchStdout, insertedBatchStderr) = RunBuiltCli(["references", "#inserted_batch", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (batchExitCode, batchStdout, batchStderr) = RunBuiltCli(["references", "#batch_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (batchSourceExitCode, batchSourceStdout, batchSourceStderr) = RunBuiltCli(["references", "staging_batch", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (topUsersExitCode, topUsersStdout, topUsersStderr) = RunBuiltCli(["references", "top_users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (usersExitCode, usersStdout, usersStderr) = RunBuiltCli(["references", "users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (fnExitCode, fnStdout, fnStderr) = RunBuiltCli(["references", "fn_users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (topExitCode, topStdout, topStderr) = RunBuiltCli(["references", "TOP", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (onlyExitCode, onlyStdout, onlyStderr) = RunBuiltCli(["references", "ONLY", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (lateralExitCode, lateralStdout, lateralStderr) = RunBuiltCli(["references", "LATERAL", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_SqlTruncateTargetsHandleOnlyAndMultipleTargets()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_truncate_targets");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                TRUNCATE TABLE ONLY public.users;
                TRUNCATE TABLE audit_log, archived_log;
                TRUNCATE TABLE [dbo].[users], `analytics`.`logs`, "public"."accounts";
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (usersExitCode, usersStdout, usersStderr) = RunBuiltCli(["references", "users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (archivedExitCode, archivedStdout, archivedStderr) = RunBuiltCli(["references", "archived_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (logsExitCode, logsStdout, logsStderr) = RunBuiltCli(["references", "logs", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (accountsExitCode, accountsStdout, accountsStderr) = RunBuiltCli(["references", "accounts", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (onlyExitCode, onlyStdout, onlyStderr) = RunBuiltCli(["references", "ONLY", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (qualifiedExitCode, qualifiedStdout, qualifiedStderr) = RunBuiltCli(["references", "public.users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (mangledBracketExitCode, mangledBracketStdout, mangledBracketStderr) = RunBuiltCli(["references", "dbo].[users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (mangledBacktickExitCode, mangledBacktickStdout, mangledBacktickStderr) = RunBuiltCli(["references", "analytics`.`logs", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (mangledDoubleQuoteExitCode, mangledDoubleQuoteStdout, mangledDoubleQuoteStderr) = RunBuiltCli(["references", "public\".\"accounts", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

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
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlDeleteUsingCapturesSourceReferences()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_delete_using");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                DELETE FROM audit_log USING staging_log, archived_log
                WHERE audit_log.id = staging_log.id;
                DELETE FROM public.audit_log USING staging.stage_log, [archive].[archived_log], "public"."source"
                WHERE audit_log.id = stage_log.id;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (stagingExitCode, stagingStdout, stagingStderr) = RunBuiltCli(["references", "staging_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (archivedExitCode, archivedStdout, archivedStderr) = RunBuiltCli(["references", "archived_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (stageExitCode, stageStdout, stageStderr) = RunBuiltCli(["references", "stage_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (sourceExitCode, sourceStdout, sourceStderr) = RunBuiltCli(["references", "source", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (qualifiedTargetExitCode, qualifiedTargetStdout, qualifiedTargetStderr) = RunBuiltCli(["references", "public.audit_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (qualifiedSourceExitCode, qualifiedSourceStdout, qualifiedSourceStderr) = RunBuiltCli(["references", "staging.stage_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (mangledBracketExitCode, mangledBracketStdout, mangledBracketStderr) = RunBuiltCli(["references", "archive].[archived_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (mangledDoubleQuoteExitCode, mangledDoubleQuoteStdout, mangledDoubleQuoteStderr) = RunBuiltCli(["references", "public\".\"source", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

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
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlUsingSourceMatcherSkipsDdlUsingClauses()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_using_source_matcher");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
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
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (stagingExitCode, stagingStdout, stagingStderr) = RunBuiltCli(["references", "staging_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (archiveTargetExitCode, archiveTargetStdout, archiveTargetStderr) = RunBuiltCli(["references", "audit_log_archive", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (archiveSourceExitCode, archiveSourceStdout, archiveSourceStderr) = RunBuiltCli(["references", "staging_archive", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (btreeExitCode, btreeStdout, btreeStderr) = RunBuiltCli(["references", "btree", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (lowerExitCode, lowerStdout, lowerStderr) = RunBuiltCli(["references", "lower", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var stagingRows = ParseJsonLines(stagingStdout);
            var archiveTargetRows = ParseJsonLines(archiveTargetStdout);
            var archiveSourceRows = ParseJsonLines(archiveSourceStdout);
            var lowerRows = ParseJsonLines(lowerStdout);
            using var btreeDocument = ParseJsonOutput(btreeStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, stagingExitCode);
            Assert.Equal(string.Empty, stagingStderr);
            var stagingRow = Assert.Single(stagingRows);
            Assert.Equal("staging_log", stagingRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", stagingRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, archiveTargetExitCode);
            Assert.Equal(string.Empty, archiveTargetStderr);
            var archiveTargetRow = Assert.Single(archiveTargetRows);
            Assert.Equal("audit_log_archive", archiveTargetRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", archiveTargetRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, archiveSourceExitCode);
            Assert.Equal(string.Empty, archiveSourceStderr);
            var archiveSourceRow = Assert.Single(archiveSourceRows);
            Assert.Equal("staging_archive", archiveSourceRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", archiveSourceRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, btreeExitCode);
            Assert.Equal(string.Empty, btreeStderr);
            Assert.Equal(0, btreeDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, lowerExitCode);
            Assert.Equal(string.Empty, lowerStderr);
            var lowerRow = Assert.Single(lowerRows);
            Assert.Equal("lower", lowerRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("call", lowerRow.RootElement.GetProperty("reference_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlMergeUsingWithTargetHintStillResolvesSource()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_merge_using_target_hint");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                MERGE INTO audit_log WITH (INDEX(ix_audit_log), HOLDLOCK) AS t
                USING staging_log AS s
                ON t.id = s.id
                WHEN MATCHED THEN
                    UPDATE SET action = s.action;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (stagingExitCode, stagingStdout, stagingStderr) = RunBuiltCli(["references", "staging_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var stagingRows = ParseJsonLines(stagingStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, stagingExitCode);
            Assert.Equal(string.Empty, stagingStderr);

            var stagingRow = Assert.Single(stagingRows);
            Assert.Equal("staging_log", stagingRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", stagingRow.RootElement.GetProperty("reference_kind").GetString());
            Assert.Equal(2, stagingRow.RootElement.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlMergeTempTargetWithoutIntoResolvesTargetAndSource()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_merge_temp_without_into");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                MERGE #audit_log AS t
                USING staging_log AS s
                ON t.id = s.id
                WHEN MATCHED THEN
                    UPDATE SET action = s.action;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (targetExitCode, targetStdout, targetStderr) = RunBuiltCli(["references", "#audit_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (sourceExitCode, sourceStdout, sourceStderr) = RunBuiltCli(["references", "staging_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var targetRows = ParseJsonLines(targetStdout);
            var sourceRows = ParseJsonLines(sourceStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, targetExitCode);
            Assert.Equal(string.Empty, targetStderr);
            var targetRow = Assert.Single(targetRows);
            Assert.Equal("#audit_log", targetRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", targetRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, sourceExitCode);
            Assert.Equal(string.Empty, sourceStderr);
            var sourceRow = Assert.Single(sourceRows);
            Assert.Equal("staging_log", sourceRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", sourceRow.RootElement.GetProperty("reference_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlMergeTempTargetWithMultilineHintResolvesTargetAndSource()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_merge_temp_with_multiline_hint");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                MERGE INTO #audit_log
                WITH (INDEX(ix_audit_log), HOLDLOCK) AS t
                USING staging_log AS s
                ON t.id = s.id
                WHEN MATCHED THEN
                    UPDATE SET action = s.action;
                MERGE #archive_log
                WITH (HOLDLOCK) AS u
                USING staging_archive AS v
                ON u.id = v.id
                WHEN MATCHED THEN
                    UPDATE SET action = v.action;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (targetExitCode, targetStdout, targetStderr) = RunBuiltCli(["references", "#audit_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (sourceExitCode, sourceStdout, sourceStderr) = RunBuiltCli(["references", "staging_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (archiveTargetExitCode, archiveTargetStdout, archiveTargetStderr) = RunBuiltCli(["references", "#archive_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (archiveSourceExitCode, archiveSourceStdout, archiveSourceStderr) = RunBuiltCli(["references", "staging_archive", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var targetRows = ParseJsonLines(targetStdout);
            var sourceRows = ParseJsonLines(sourceStdout);
            var archiveTargetRows = ParseJsonLines(archiveTargetStdout);
            var archiveSourceRows = ParseJsonLines(archiveSourceStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, targetExitCode);
            Assert.Equal(string.Empty, targetStderr);
            var targetRow = Assert.Single(targetRows);
            Assert.Equal("#audit_log", targetRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", targetRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, sourceExitCode);
            Assert.Equal(string.Empty, sourceStderr);
            var sourceRow = Assert.Single(sourceRows);
            Assert.Equal("staging_log", sourceRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", sourceRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, archiveTargetExitCode);
            Assert.Equal(string.Empty, archiveTargetStderr);
            var archiveTargetRow = Assert.Single(archiveTargetRows);
            Assert.Equal("#archive_log", archiveTargetRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", archiveTargetRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, archiveSourceExitCode);
            Assert.Equal(string.Empty, archiveSourceStderr);
            var archiveSourceRow = Assert.Single(archiveSourceRows);
            Assert.Equal("staging_archive", archiveSourceRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", archiveSourceRow.RootElement.GetProperty("reference_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlLineEndCommentsKeepMultilineUsingSources()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_line_end_comment_using");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
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
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (deleteExitCode, deleteStdout, deleteStderr) = RunBuiltCli(["references", "staging_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (mergeExitCode, mergeStdout, mergeStderr) = RunBuiltCli(["references", "staging_merge", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (tempExitCode, tempStdout, tempStderr) = RunBuiltCli(["references", "#comment_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var deleteRows = ParseJsonLines(deleteStdout);
            var mergeRows = ParseJsonLines(mergeStdout);
            var tempRows = ParseJsonLines(tempStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, deleteExitCode);
            Assert.Equal(string.Empty, deleteStderr);
            var deleteRow = Assert.Single(deleteRows);
            Assert.Equal("staging_log", deleteRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", deleteRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, mergeExitCode);
            Assert.Equal(string.Empty, mergeStderr);
            var mergeRow = Assert.Single(mergeRows);
            Assert.Equal("staging_merge", mergeRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", mergeRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, tempExitCode);
            Assert.Equal(string.Empty, tempStderr);
            Assert.Equal(2, tempRows.Count);
            Assert.All(tempRows, row => Assert.Equal("#comment_temp", row.RootElement.GetProperty("symbol_name").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlLineEndCommentsKeepUnfinishedPrefixes()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_line_end_comment_unfinished_prefixes");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
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
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (tempExitCode, tempStdout, tempStderr) = RunBuiltCli(["references", "#comment_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (deleteExitCode, deleteStdout, deleteStderr) = RunBuiltCli(["references", "archived_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (mergeExitCode, mergeStdout, mergeStderr) = RunBuiltCli(["references", "staging_merge", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var tempRows = ParseJsonLines(tempStdout);
            var deleteRows = ParseJsonLines(deleteStdout);
            var mergeRows = ParseJsonLines(mergeStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, tempExitCode);
            Assert.Equal(string.Empty, tempStderr);
            Assert.Equal(2, tempRows.Count);
            Assert.All(tempRows, row => Assert.Equal("#comment_temp", row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, deleteExitCode);
            Assert.Equal(string.Empty, deleteStderr);
            var deleteRow = Assert.Single(deleteRows);
            Assert.Equal("archived_log", deleteRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", deleteRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, mergeExitCode);
            Assert.Equal(string.Empty, mergeStderr);
            var mergeRow = Assert.Single(mergeRows);
            Assert.Equal("staging_merge", mergeRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", mergeRow.RootElement.GetProperty("reference_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlLineEndCommentsKeepUnfinishedTargetPrefixes()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_line_end_comment_target_prefixes");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
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
            var (auditExitCode, auditStdout, auditStderr) = RunBuiltCli(["references", "audit_log", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (updateExitCode, updateStdout, updateStderr) = RunBuiltCli(["references", "#update_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (deleteExitCode, deleteStdout, deleteStderr) = RunBuiltCli(["references", "#delete_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (truncateExitCode, truncateStdout, truncateStderr) = RunBuiltCli(["references", "#truncate_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (createExitCode, createStdout, createStderr) = RunBuiltCli(["references", "#create_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var auditRows = ParseJsonLines(auditStdout);
            var updateRows = ParseJsonLines(updateStdout);
            var deleteRows = ParseJsonLines(deleteStdout);
            var truncateRows = ParseJsonLines(truncateStdout);
            var createRows = ParseJsonLines(createStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, auditExitCode);
            Assert.Equal(string.Empty, auditStderr);
            var auditRow = Assert.Single(auditRows);
            Assert.Equal("audit_log", auditRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal("reference", auditRow.RootElement.GetProperty("reference_kind").GetString());

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(string.Empty, updateStderr);
            Assert.Equal(2, updateRows.Count);
            Assert.All(updateRows, row => Assert.Equal("#update_temp", row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, deleteExitCode);
            Assert.Equal(string.Empty, deleteStderr);
            Assert.Equal(2, deleteRows.Count);
            Assert.All(deleteRows, row => Assert.Equal("#delete_temp", row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, truncateExitCode);
            Assert.Equal(string.Empty, truncateStderr);
            Assert.Equal(2, truncateRows.Count);
            Assert.All(truncateRows, row => Assert.Equal("#truncate_temp", row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, createExitCode);
            Assert.Equal(string.Empty, createStderr);
            Assert.Single(createRows);
            Assert.All(createRows, row => Assert.Equal("#create_temp", row.RootElement.GetProperty("symbol_name").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlTruncateTempTargetsEstablishLaterReads()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_truncate_temp_target");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                TRUNCATE TABLE #truncate_temp;
                SELECT * FROM #truncate_temp;
                SELECT * FROM #future_temp;
                TRUNCATE TABLE #future_temp;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (truncateExitCode, truncateStdout, truncateStderr) = RunBuiltCli(["references", "#truncate_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (futureExitCode, futureStdout, futureStderr) = RunBuiltCli(["references", "#future_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var truncateRows = ParseJsonLines(truncateStdout);
            var futureRows = ParseJsonLines(futureStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, truncateExitCode);
            Assert.Equal(string.Empty, truncateStderr);
            Assert.Equal(2, truncateRows.Count);
            Assert.All(truncateRows, row => Assert.Equal("#truncate_temp", row.RootElement.GetProperty("symbol_name").GetString()));

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

    [Fact]
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
            var (dollarExitCode, dollarStdout, dollarStderr) = RunBuiltCli(["references", "my$table", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (prefixExitCode, prefixStdout, prefixStderr) = RunBuiltCli(["references", "my", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_SqlNonAsciiBareIdentifiersStayWhole()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_non_ascii_bare_identifier");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                SELECT * FROM ユーザー;
                INSERT INTO ユーザー (id) VALUES (1);
                UPDATE ユーザー SET id = 2;
                DELETE FROM ユーザー;
                TRUNCATE TABLE ユーザー;
                CALL ユーザー;
                EXEC ユーザー;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (symbolExitCode, symbolStdout, symbolStderr) = RunBuiltCli(["references", "ユーザー", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var rows = ParseJsonLines(symbolStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, symbolExitCode);
            Assert.Equal(string.Empty, symbolStderr);
            Assert.Equal(7, rows.Count);
            Assert.Equal(5, rows.Count(row => row.RootElement.GetProperty("reference_kind").GetString() == "reference"));
            Assert.Equal(2, rows.Count(row => row.RootElement.GetProperty("reference_kind").GetString() == "call"));
            Assert.All(rows, row => Assert.Equal("ユーザー", row.RootElement.GetProperty("symbol_name").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlSemicolonlessSetAndDeclareKeepTempReads()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_semicolonless_set_declare_temp");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                SELECT * FROM #future_temp;
                SELECT id INTO #set_temp FROM users
                SET @count = (SELECT COUNT(*) FROM #set_temp);
                SELECT id INTO #declare_temp FROM users
                DECLARE @first_id INT = (SELECT TOP (1) id FROM #declare_temp);
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (futureExitCode, futureStdout, futureStderr) = RunBuiltCli(["references", "#future_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (setExitCode, setStdout, setStderr) = RunBuiltCli(["references", "#set_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (declareExitCode, declareStdout, declareStderr) = RunBuiltCli(["references", "#declare_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            using var futureDocument = ParseJsonOutput(futureStdout);
            var setRows = ParseJsonLines(setStdout);
            var declareRows = ParseJsonLines(declareStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, futureExitCode);
            Assert.Equal(string.Empty, futureStderr);
            Assert.Equal(0, futureDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, setExitCode);
            Assert.Equal(string.Empty, setStderr);
            Assert.Equal(2, setRows.Count);
            Assert.All(setRows, row => Assert.Equal("#set_temp", row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, declareExitCode);
            Assert.Equal(string.Empty, declareStderr);
            Assert.Equal(2, declareRows.Count);
            Assert.All(declareRows, row => Assert.Equal("#declare_temp", row.RootElement.GetProperty("symbol_name").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlSemicolonlessIfAndWhileKeepTempReads()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_semicolonless_if_while_temp");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                SELECT id INTO #if_temp FROM users
                IF EXISTS (SELECT 1) SELECT * FROM #if_temp;
                SELECT id INTO #while_temp FROM users
                WHILE 1 = 0 SELECT * FROM #while_temp;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (ifExitCode, ifStdout, ifStderr) = RunBuiltCli(["references", "#if_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (whileExitCode, whileStdout, whileStderr) = RunBuiltCli(["references", "#while_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var ifRows = ParseJsonLines(ifStdout);
            var whileRows = ParseJsonLines(whileStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, ifExitCode);
            Assert.Equal(string.Empty, ifStderr);
            Assert.Equal(2, ifRows.Count);
            Assert.All(ifRows, row => Assert.Equal("#if_temp", row.RootElement.GetProperty("symbol_name").GetString()));

            Assert.Equal(CommandExitCodes.Success, whileExitCode);
            Assert.Equal(string.Empty, whileStderr);
            Assert.Equal(2, whileRows.Count);
            Assert.All(whileRows, row => Assert.Equal("#while_temp", row.RootElement.GetProperty("symbol_name").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlDoubleQuotedDynamicSqlDoesNotLeakUsersReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_double_quoted_dynamic_sql");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                SET @sql = "SELECT * FROM users";
                EXECUTE IMMEDIATE @sql;
                SELECT * FROM "users";
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (usersExitCode, usersStdout, usersStderr) = RunBuiltCli(["references", "users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var usersRows = ParseJsonLines(usersStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, usersExitCode);
            Assert.Equal(string.Empty, usersStderr);

            var usersRow = Assert.Single(usersRows);
            var json = usersRow.RootElement;
            Assert.Equal("users", json.GetProperty("symbol_name").GetString());
            Assert.Equal(3, json.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlDoubleQuotedDynamicSqlDoesNotEstablishTempTable()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_dynamic_temp_establishment");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                SET @sql = "SELECT id INTO #temp_users FROM users";
                SELECT * FROM #temp_users;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "#temp_users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_SqlTempTablesDoNotLookAheadAcrossProcedureBodies()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_temp_body_boundary");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                CREATE PROCEDURE dbo.ReadTemp AS
                BEGIN
                    SELECT * FROM #later_temp;
                END;
                GO
                CREATE PROCEDURE dbo.EstablishTemp AS
                BEGIN
                    SELECT id INTO #later_temp FROM users;
                END;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "#later_temp", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var row = Assert.Single(rows);
            var json = row.RootElement;
            Assert.Equal("#later_temp", json.GetProperty("symbol_name").GetString());
            Assert.Equal(8, json.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlSameLineDollarQuotedBodiesDoNotHideLaterReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_same_line_dollar_quoted_bodies");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                DO $$BEGIN END$$; SELECT * FROM users; DO $$BEGIN END$$;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (usersExitCode, usersStdout, usersStderr) = RunBuiltCli(["references", "users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var usersRows = ParseJsonLines(usersStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, usersExitCode);
            Assert.Equal(string.Empty, usersStderr);

            var usersRow = Assert.Single(usersRows);
            Assert.Equal("users", usersRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal(1, usersRow.RootElement.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlEscapedSingleQuotedStringsDoNotLeakPhantomReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_escaped_single_quoted_strings");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                SELECT 'abc\' FROM phantom';
                SELECT 'abc'' FROM still_phantom';
                SELECT * FROM users # comment with comment_phantom;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (phantomExitCode, phantomStdout, phantomStderr) = RunBuiltCli(["references", "phantom", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (usersExitCode, usersStdout, usersStderr) = RunBuiltCli(["references", "users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            using var phantomDocument = ParseJsonOutput(phantomStdout);
            var usersRows = ParseJsonLines(usersStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, phantomExitCode);
            Assert.Equal(string.Empty, phantomStderr);
            Assert.Equal(0, phantomDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, usersExitCode);
            Assert.Equal(string.Empty, usersStderr);
            var usersRow = Assert.Single(usersRows);
            Assert.Equal("users", usersRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal(3, usersRow.RootElement.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlMultilineSingleQuotedStringsStayOpaque()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_multiline_single_quoted_strings");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
                """
                SELECT 'abc''
                still escaped \'
                FROM phantom
                INTO #temp_users
                ';
                SELECT * FROM users;
                SELECT * FROM #temp_users;
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (usersExitCode, usersStdout, usersStderr) = RunBuiltCli(["references", "users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (tempExitCode, tempStdout, tempStderr) = RunBuiltCli(["references", "#temp_users", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            var usersRows = ParseJsonLines(usersStdout);
            using var tempDocument = ParseJsonOutput(tempStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, usersExitCode);
            Assert.Equal(string.Empty, usersStderr);
            var usersRow = Assert.Single(usersRows);
            Assert.Equal("users", usersRow.RootElement.GetProperty("symbol_name").GetString());
            Assert.Equal(6, usersRow.RootElement.GetProperty("line").GetInt32());

            Assert.Equal(CommandExitCodes.Success, tempExitCode);
            Assert.Equal(string.Empty, tempStderr);
            Assert.Equal(0, tempDocument.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_SqlNonCodeRegionsDoNotLeakPhantomReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_sql_non_code_regions");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "repro.sql"),
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
            var (phantomExitCode, phantomStdout, phantomStderr) = RunBuiltCli(["references", "phantom", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);
            var (accountsExitCode, accountsStdout, accountsStderr) = RunBuiltCli(["references", "accounts", "--db", dbPath, "--json", "--lang", "sql", "--exact-name"]);

            using var phantomDocument = ParseJsonOutput(phantomStdout);
            var accountsRows = ParseJsonLines(accountsStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, phantomExitCode);
            Assert.Equal(string.Empty, phantomStderr);
            Assert.Equal(0, phantomDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, accountsExitCode);
            Assert.Equal(string.Empty, accountsStderr);
            var accountsRow = Assert.Single(accountsRows);
            var json = accountsRow.RootElement;
            Assert.Equal("accounts", json.GetProperty("symbol_name").GetString());
            Assert.Equal(14, json.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpMultilineThrowBeforeGroupDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_multiline_throw_group_local_function");
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

                public sealed class Holder
                {
                    public int Ready { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        static System.Exception group(IEnumerable<Holder> xs) => new System.Exception(xs.Count().ToString());
                        return from Status in items
                               orderby items.Count() > 0 ? throw
                                       group
                                       (items) : null, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpNullableTypeSuffixBeforeParenthesizedTerminalSelectPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_terminal_select_after_nullable_type_suffix");
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
                    public Status Read(IEnumerable<object> items, object value)
                    {
                        return Sink.Pick(from Status in items
                                         let cast = value as Status?
                                         select(Status.Ready),
                                         Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpPostfixIncrementBeforeParenthesizedTerminalSelectPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_terminal_select_after_postfix_increment");
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpNullableArrayRankSuffixBeforeParenthesizedTerminalSelectPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_terminal_select_after_nullable_array_rank_suffix");
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
                    public Status Read(IEnumerable<object> items, object value)
                    {
                        return Sink.Pick(from Status in items
                                         let cast = value as Status[,]?
                                         select(Status.Ready),
                                         Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpNullableTupleSuffixBeforeParenthesizedTerminalSelectPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_terminal_select_after_nullable_tuple_suffix");
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpCastedLocalSelectCallInOrderByDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_casted_select_local_function");
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
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpSimpleIdentifierCastedLocalSelectCallInOrderByPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_simple_casted_select_local_function");
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
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpMultilineSimpleIdentifierCastedLocalSelectCallInOrderByPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_multiline_simple_casted_select_local_function");
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
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpParenthesizedTernaryOrderByBeforeParenthesizedTerminalSelectPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_ternary_before_parenthesized_select");
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
                    public Status Read(IEnumerable<object> items, bool flag, int left, int right)
                    {
                        return Sink.Pick(from Status in items
                                         orderby (flag ? left : right)
                                         select(Status.Ready),
                                         Demo.Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpLowercaseAliasCastedLocalSelectCallInOrderByPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_orderby_lowercase_alias_casted_select_local_function");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
    public void RunReferences_ExactJson_CSharpParenthesizedKeywordNamedLocalBeforeParenthesizedTerminalSelectPreservesLaterEnumReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_keyword_named_local_before_parenthesized_select");
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
                        const int Select = 1;
                        return Sink.Pick(from Status in items
                                         orderby (Select)
                                         select(Status.Ready),
                                         Demo.Status.Ready);
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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
    public void RunReferences_ExactJson_CSharpLambdaParameterNamedLikeEnumDoesNotLeakAfterLambda()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_lambda_scope_end");
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
                    public Demo.Status Read()
                    {
                        Func<Holder, int> f = Status => Status.Ready;
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
            Assert.Equal(20, json.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpLambdaParameterNamedLikeEnumDoesNotLeakAfterSameLineLambda()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_lambda_same_line_scope_end");
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
                    public Demo.Status Read()
                    {
                        Func<Holder, int> f = Status => Status.Ready; return Demo.Status.Ready;
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpParenthesizedLambdaParameterNamedLikeEnumDoesNotSuppressEarlierSameLineReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parenthesized_lambda_prefix_scope");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableNamedLikeEnumDoesNotLeakAfterQuery()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_scope_end");
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
                    public Demo.Status Read(IEnumerable<Holder> items)
                    {
                        _ = from Status in items
                            select Status.Ready;

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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
            Assert.Equal(23, json.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableNamedLikeEnumDoesNotLeakPastQueryArgument()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_argument_scope_end");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpForeachValueNamedLikeEnumDoesNotLeakAfterEmbeddedStatement()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_foreach_scope_end");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
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
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
            Assert.Equal(22, json.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpForeachValueNamedLikeEnumDoesNotLeakAfterSameLineEmbeddedStatement()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_foreach_same_line_scope_end");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
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
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpForeachValueNamedLikeEnumDoesNotLeakInsideElseBranch()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_foreach_else_scope_end");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
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
    public void RunReferences_ExactJson_CSharpDeclarationPatternVariableDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_declaration_pattern_collision");
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
                        if (value is Holder Status)
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal(22, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpMultiLineIfDeclarationPatternVariableDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_multiline_if_declaration_pattern_collision");
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
                        if (
                            value is Holder Status)
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal(23, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpMultiLineWhileDeclarationPatternVariableDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_multiline_while_declaration_pattern_collision");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal(24, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
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
            Assert.Equal("call", first.RootElement.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", first.RootElement.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal(28, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableGenericTypePatternDesignationDoesNotLeakEnumReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_generic_type_pattern_designation");
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
                    public int Read(IEnumerable<Holder> items)
                    {
                        return (from Status in items
                                select Status is Dictionary<int, int> dict ? Status.Ready : 0).First();
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
    public void RunReferences_ExactJson_CSharpQueryRangeVariableGenericTypePatternWithoutDesignationDoesNotLeakEnumReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_generic_type_pattern_without_designation");
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
                    public int Read(IEnumerable<Holder> items)
                    {
                        return (from Status in items
                                select Status is Dictionary<int, int> ? Status.Ready : 0).First();
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

    [Theory]
    [InlineData("!=")]
    [InlineData("==")]
    public void RunReferences_ExactJson_CSharpQueryRangeVariableGenericAsNullComparisonDoesNotLeakEnumReference(string comparisonOperator)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_query_runner_enum_member_query_generic_as_null_{comparisonOperator switch { "!=" => "not_equal", "==" => "equal_equal", _ => "unknown" }}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                $$"""
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
                                select Status as Dictionary<int, int> {{comparisonOperator}} null ? Status.Ready : 0).First();
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal(24, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionDeclarationPatternWhenInMultiLineCommentDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_switch_expression_declaration_pattern_when_multiline_comment_collision");
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
                        return value switch
                        {
                            Holder /* trivia
                                      when comment */ Status when Status.Ready > 0 => Demo.Status.Ready,
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
            var rows = ParseJsonLines(stdout)
                .Select(document => (
                    Line: document.RootElement.GetProperty("line").GetInt32(),
                    Column: document.RootElement.GetProperty("column").GetInt32(),
                    ContainerName: document.RootElement.GetProperty("container_name").GetString()))
                .OrderBy(row => row.Line)
                .ThenBy(row => row.Column)
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", firstJson.GetProperty("reference_kind").GetString());
            Assert.Equal([20, 21], rows.Select(row => row.Line).ToArray());
            Assert.Equal([83, 30], rows.Select(row => row.Column).ToArray());
            Assert.All(rows, row => Assert.Equal("Read", row.ContainerName));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionDeclarationPatternWhenGuardDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_switch_expression_declaration_pattern_when_guard_collision");
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
                        return value switch
                        {
                            Holder Status when Status.Ready > 0 => Demo.Status.Ready,
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
            var rows = ParseJsonLines(stdout)
                .Select(document => (
                    Line: document.RootElement.GetProperty("line").GetInt32(),
                    Column: document.RootElement.GetProperty("column").GetInt32(),
                    ContainerName: document.RootElement.GetProperty("container_name").GetString()))
                .OrderBy(row => row.Line)
                .ThenBy(row => row.Column)
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", firstJson.GetProperty("reference_kind").GetString());
            Assert.Equal([19, 20], rows.Select(row => row.Line).ToArray());
            Assert.Equal([64, 30], rows.Select(row => row.Column).ToArray());
            Assert.All(rows, row => Assert.Equal("Read", row.ContainerName));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_ExactJson_CSharpSwitchExpressionRecursivePatternVariableDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_switch_expression_recursive_pattern_collision");
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

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
            Assert.Equal(20, json.GetProperty("line").GetInt32());
            Assert.Equal("Read", json.GetProperty("container_name").GetString());
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
            Assert.Equal("call", first.RootElement.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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
            Assert.Equal("call", json.GetProperty("reference_kind").GetString());
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

    [Fact]
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
            var (nonExactExitCode, nonExactStdout, nonExactStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--path", "src/Use.cs", "--limit", "100"]);
            var nonExactRows = ParseJsonLines(nonExactStdout)
                .Select(document => document.RootElement)
                .ToList();

            var (exactExitCode, exactStdout, exactStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
            using var exactDocument = ParseJsonOutput(exactStdout);

            var (countExitCode, countStdout, countStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"]);
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

    [Fact]
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
            var (nonExactExitCode, nonExactStdout, nonExactStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--path", "src/Use.cs", "--limit", "100"]);
            var nonExactRows = ParseJsonLines(nonExactStdout)
                .Select(document => document.RootElement)
                .ToList();

            var (exactExitCode, exactStdout, exactStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
            using var exactDocument = ParseJsonOutput(exactStdout);

            var (countExitCode, countStdout, countStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"]);
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

    [Fact]
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
            var (nonExactExitCode, nonExactStdout, nonExactStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--path", "src/Use.cs", "--limit", "100"]);
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

            var (exactExitCode, exactStdout, exactStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
            using var exactDocument = ParseJsonOutput(exactStdout);

            var (countExitCode, countStdout, countStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"]);
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

    [Fact]
    public void RunReferences_ExactJson_CSharpQualifiedConstantPatternSameFileEnumMemberSitesStaySuppressed()
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
            var (referencesExitCode, referencesStdout, referencesStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
            using var referencesDocument = ParseJsonOutput(referencesStdout);

            var (countExitCode, countStdout, countStderr) = RunBuiltCli(
                ["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--count"]);
            using var countDocument = ParseJsonOutput(countStdout);

            var (callersExitCode, callersStdout, callersStderr) = RunBuiltCli(
                ["callers", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
            using var callersDocument = ParseJsonOutput(callersStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, referencesExitCode);
            Assert.Equal(string.Empty, referencesStderr);
            Assert.Equal(0, referencesDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            Assert.Equal(0, countDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, callersExitCode);
            Assert.Equal(string.Empty, callersStderr);
            Assert.Equal(0, callersDocument.RootElement.GetProperty("count").GetInt32());
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
            Assert.Equal("call", redJson.GetProperty("reference_kind").GetString());
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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Shape", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
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

    [Theory]
    [InlineData("Point { X: < 0 } => 1,")]
    [InlineData("Point { X: > 0 } => 1,")]
    public void RunReferences_ExactJson_CSharpSwitchExpressionLaterArmAfterRelationalPatternStaysVisible(string previousArm)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_switch_expression_later_arm_after_relational");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                $$"""
                namespace Probe;

                class Point { public int X { get; init; } }
                class Shape {}

                class Demo
                {
                    int Match(object value) => value switch
                    {
                        {{previousArm}}
                        Shape => 2,
                        _ => 0,
                    };
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Shape", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
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

    [Theory]
    [InlineData("Point p when p.GetHashCode() > 0 => 1,")]
    [InlineData("Point { X: < 0 } => 1,")]
    public void RunReferences_ExactJson_CSharpSwitchExpressionLaterGenericArmStaysVisible(string previousArm)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_switch_expression_later_generic_arm");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "cases.cs"),
                $$"""
                namespace Probe;

                class Point { public int X { get; init; } }
                class Shape {}
                class Wrapper<TLeft, TRight> {}

                class Demo
                {
                    int Match(object value) => value switch
                    {
                        {{previousArm}}
                        Wrapper<Point, Shape> => 2,
                        _ => 0,
                    };
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = RunBuiltCli([projectRoot, "--json", "--quiet"]);
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Wrapper", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Wrapper", row.GetProperty("symbol_name").GetString());
            Assert.Equal("type_reference", row.GetProperty("reference_kind").GetString());
            Assert.Contains("Wrapper<Point, Shape> => 2", row.GetProperty("context").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);
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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--max-line-width", "8"]);

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

    [Fact]
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["references", "Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

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
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
            Assert.Equal(0, json.GetProperty("confirmed_count").GetInt32());
            Assert.Equal(0, json.GetProperty("confirmed_file_count").GetInt32());
            Assert.Equal(1, json.GetProperty("hint_count").GetInt32());
            Assert.Equal(1, json.GetProperty("hint_file_count").GetInt32());
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
            Assert.Contains("idx_symbol_refs_name_nocase", stderr);
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
            Assert.Contains("idx_symbol_refs_container_nocase", json.GetProperty("degraded_reason").GetString());
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
            using (var db = new DbContext(dbPath))
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
    public void RunReferences_Json_CSharpNestedRawStringInsideInterpolationDoesNotCreatePhantomReference()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_references_csharp_nested_raw_fixture");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "app.cs"),
                """"
                public class App
                {
                    private int Run() => 1;
                    private string Id(string value) => value;

                    public int Render()
                    {
                        return $"""
                            value = {Id("""
                                Execute();
                                public class Phantom
                                {
                                    public void Go() { }
                                }
                                """) + Run()}
                            """.Length;
                    }
                }
                """");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["Execute", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("references").GetArrayLength());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
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
