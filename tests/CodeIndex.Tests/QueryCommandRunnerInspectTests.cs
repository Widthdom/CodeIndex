using System.Globalization;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunInspect_StrictNotFoundReturnsNotFoundForEmptyAnalysis_Issue1425()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_strict_not_found");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/auth.cs",
                "csharp",
                "public class AuthFixture { }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Missing", "--db", dbPath, "--json", "--strict-not-found"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.NotFound, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            Assert.Equal("Missing", document.RootElement.GetProperty("query").GetString());
            Assert.Empty(document.RootElement.GetProperty("definitions").EnumerateArray());
            Assert.Empty(document.RootElement.GetProperty("references").EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_AllowsPathValueThatLooksLikePreviewOption()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_preview_like_path_value");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["target", "--db", dbPath, "--path=--max-line-width", "--json"],
                _jsonOptions));

            Assert.NotEqual(CommandExitCodes.UsageError, exitCode);
            Assert.DoesNotContain("is not supported", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_CompactJson_CapsNearbySymbolsAndReportsTruncation_Issue3009()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_compact_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/SharedTarget.cs",
                "csharp",
                """
                public class SharedTarget
                {
                    public void Run0() { }
                    public void Run1() { }
                    public void Run2() { }
                    public void Run3() { }
                    public void Run4() { }
                    public void Run5() { }
                    public void Run6() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["SharedTarget", "--db", dbPath, "--compact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var nearbySymbols = json.GetProperty("nearby_symbols").EnumerateArray().ToList();
            var nearbySymbolsTruncation = json
                .GetProperty("truncation")
                .GetProperty("sections")
                .GetProperty("nearby_symbols");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("compact").GetBoolean());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, nearbySymbols.Count);
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, nearbySymbolsTruncation.GetProperty("returned").GetInt32());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit + 1, nearbySymbolsTruncation.GetProperty("source_count").GetInt32());
            Assert.True(nearbySymbolsTruncation.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_Json_TruncatesRunawayDefinitionSignatures_Issue2989()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_signature_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var parameters = string.Join(", ", Enumerable.Range(0, 90).Select(index => $"string parameter{index:D2}"));
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/HugeSignatures.cs",
                "csharp",
                $$"""
                public class HugeSignatures
                {
                    public void Run({{parameters}}) { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Run", "--db", dbPath, "--json", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var definition = document.RootElement
                .GetProperty("definitions")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "Run");
            var signature = definition.GetProperty("signature").GetString();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.NotNull(signature);
            Assert.True(signature!.Length <= 512);
            Assert.EndsWith("...", signature, StringComparison.Ordinal);
            Assert.True(definition.GetProperty("signature_truncated").GetBoolean());
            Assert.True(definition.GetProperty("signature_original_length").GetInt32() > signature.Length);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_JsonBodyRecoveryCommandIncludesActiveDb_Issue3562()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_body_recovery_db_3562");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longLiteral = new string('a', DbReader.DefinitionBodyMaxBytes + 1024);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/huge_body.py",
                "python",
                $"def huge_body():\n    value = \"{longLiteral}\"\n    return value\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["huge_body", "--db", dbPath, "--read-only", "--json", "--body", "--lang", "python", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var definition = document.RootElement
                .GetProperty("definitions")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "huge_body");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(definition.GetProperty("body_content_truncated").GetBoolean());
            var recovery = definition.GetProperty("body_content_recovery");
            var recoveryCommand = recovery.GetProperty("command").GetString();
            Assert.Contains("cdidx excerpt src/huge_body.py", recoveryCommand);
            Assert.Contains("--db", recoveryCommand);
            Assert.Contains("file:", recoveryCommand);
            var expectedReadOnlyUri = DbContext.ToReadOnlyUri(dbPath);
            Assert.Contains(expectedReadOnlyUri, recoveryCommand);
            Assert.Contains("immutable=1", recoveryCommand);
            Assert.Contains("mode=ro", recoveryCommand);
            Assert.Contains("--start 2 --end 3 --max-line-width 0 --json", recoveryCommand);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ParseFields_ImplyJsonAndCanonicalizeAliases_Issue3056()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--fields", "body,refs,nearby-symbols"],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.True(options.Json);
        Assert.True(options.IncludeBody);
        Assert.Equal(["definitions", "references", "nearby_symbols"], options.InspectFields);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void RunInspect_ParseBodyRange_ImplyBodyAndValidateValues_Issue3394()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--body-start", "6", "--body-lines=2"],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.True(options.IncludeBody);
        Assert.Equal(6, options.BodyStartLine);
        Assert.Equal(2, options.BodyLines);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void RunInspect_ParseBodyLineCountAlias_MapsToBodyLines_Issue3665()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--body-line-count=3"],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.True(options.IncludeBody);
        Assert.Equal(3, options.BodyLines);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void RunInspect_KindFilterJson_FiltersDefinitions_Issue3666()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_kind_filter_3666");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/target.py",
                "python",
                """
                class Target:
                    pass

                def Target():
                    return 1
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Target", "--db", dbPath, "--json", "--lang", "python", "--kind", "class", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var definitions = document.RootElement.GetProperty("definitions").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(definitions);
            Assert.Equal("class", definitions[0].GetProperty("kind").GetString());
            Assert.Equal("Target", definitions[0].GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_JsonPrioritizesSourceDefinitionsOverTests_Issue3666()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_source_priority_3666");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "tests/RunImportTests.cs",
                "csharp",
                """
                public class RunImportTests
                {
                    public void RunImport() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/RunImport.cs",
                "csharp",
                """
                public class ImportService
                {
                    public void RunImport() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["RunImport", "--db", dbPath, "--json", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var definitions = document.RootElement.GetProperty("definitions").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(definitions.Count >= 2);
            Assert.Equal("src/RunImport.cs", definitions[0].GetProperty("path").GetString());
            Assert.Contains(definitions, definition => definition.GetProperty("path").GetString() == "tests/RunImportTests.cs");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_LineRangeJson_IncludesSourceExcerpt_Issue3786()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_source_excerpt_3786");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Target.cs",
                "csharp",
                """
                public class Target
                {
                    public int Compute()
                    {
                        return 42;
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Compute", "--db", dbPath, "--json", "--line", "5", "--context", "1", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var sourceExcerpt = document.RootElement.GetProperty("source_excerpt");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/Target.cs", sourceExcerpt.GetProperty("path").GetString());
            Assert.Equal(4, sourceExcerpt.GetProperty("start_line").GetInt32());
            Assert.Equal(6, sourceExcerpt.GetProperty("end_line").GetInt32());
            Assert.Equal(4, sourceExcerpt.GetProperty("requested_start_line").GetInt32());
            Assert.Equal(6, sourceExcerpt.GetProperty("requested_end_line").GetInt32());
            Assert.Contains("return 42;", sourceExcerpt.GetProperty("content").GetString(), StringComparison.Ordinal);
            Assert.False(sourceExcerpt.GetProperty("content_truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_PathLineJson_WorksWithoutSymbolQuery_Issue3786()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_path_line_3786");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Target.cs",
                "csharp",
                """
                public class Target
                {
                    public int Compute()
                    {
                        return 42;
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["--db", dbPath, "--json", "--path", "src/Target.cs", "--line", "5"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var sourceExcerpt = root.GetProperty("source_excerpt");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/Target.cs:5", root.GetProperty("query").GetString());
            Assert.Empty(root.GetProperty("definitions").EnumerateArray());
            Assert.Equal("src/Target.cs", sourceExcerpt.GetProperty("path").GetString());
            Assert.Equal(5, sourceExcerpt.GetProperty("start_line").GetInt32());
            Assert.Equal(5, sourceExcerpt.GetProperty("end_line").GetInt32());
            Assert.Contains("return 42;", sourceExcerpt.GetProperty("content").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_FormatCompact_ActsLikeCompactJson_Issue3446()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_format_compact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Target.cs",
                "csharp",
                """
                public class Target
                {
                    public void Compute() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Target", "--db", dbPath, "--format", "compact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("compact").GetBoolean());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, json.GetProperty("compact_limit").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_FieldsJson_EmitsOnlySelectedTopLevelGroups_Issue3056()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_fields_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Target.cs",
                "csharp",
                """
                public class Target
                {
                    public void Compute() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Target", "--db", dbPath, "--fields", "definitions,file"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var selectedFields = json.GetProperty("selected_fields").EnumerateArray().Select(item => item.GetString()).ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(["definitions", "file"], selectedFields);
            Assert.True(json.TryGetProperty("api_version", out _));
            Assert.True(json.TryGetProperty("query", out _));
            Assert.True(json.TryGetProperty("definitions", out _));
            Assert.True(json.TryGetProperty("file", out _));
            Assert.False(json.TryGetProperty("nearby_symbols", out _));
            Assert.False(json.TryGetProperty("references", out _));
            Assert.False(json.TryGetProperty("callers", out _));
            Assert.False(json.TryGetProperty("callees", out _));
            Assert.False(json.TryGetProperty("graph_supported", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_BodyOnlyJson_EmitsDefinitionBodiesOnly_Issue3056()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_body_only_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Target.cs",
                "csharp",
                """
                public class Target
                {
                    public int Compute()
                    {
                        return 42;
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Compute", "--db", dbPath, "--body-only"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var selectedFields = json.GetProperty("selected_fields").EnumerateArray().Select(item => item.GetString()).ToList();
            var definition = json.GetProperty("definitions").EnumerateArray().Single();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(["definitions"], selectedFields);
            Assert.Contains("return 42;", definition.GetProperty("body_content").GetString(), StringComparison.Ordinal);
            Assert.False(json.TryGetProperty("file", out _));
            Assert.False(json.TryGetProperty("references", out _));
            Assert.False(json.TryGetProperty("callers", out _));
            Assert.False(json.TryGetProperty("callees", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_BodyRangeJson_PagesDefinitionBody_Issue3394()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_body_range_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Target.cs",
                "csharp",
                """
                public class Target
                {
                    public int Compute()
                    {
                        var value1 = 1;
                        var value2 = 2;
                        var value3 = 3;
                        return value1 + value2 + value3;
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Compute", "--db", dbPath, "--json", "--body", "--body-start", "6", "--body-lines", "2"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var definition = document.RootElement.GetProperty("definitions").EnumerateArray().Single();
            var bodyContent = definition.GetProperty("body_content").GetString();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("var value2 = 2;", bodyContent, StringComparison.Ordinal);
            Assert.Contains("var value3 = 3;", bodyContent, StringComparison.Ordinal);
            Assert.DoesNotContain("var value1 = 1;", bodyContent, StringComparison.Ordinal);
            Assert.DoesNotContain("return value1", bodyContent, StringComparison.Ordinal);
            Assert.Equal(6, definition.GetProperty("body_content_start_line").GetInt32());
            Assert.Equal(7, definition.GetProperty("body_content_end_line").GetInt32());
            Assert.Equal(8, definition.GetProperty("body_content_next_start_line").GetInt32());
            Assert.True(definition.GetProperty("body_content_truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_BodyRangeJson_ReportsNextLineFromByteClampedContent_Issue3394()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_body_byte_clamp_range_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longValue = new string('x', DbReader.DefinitionBodyMaxBytes + 1024);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Target.cs",
                "csharp",
                $$"""
                public class Target
                {
                    public string Compute()
                    {
                        var marker1 = "{{longValue}}";
                        var marker2 = "after";
                        return marker1 + marker2;
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Compute", "--db", dbPath, "--json", "--body"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var definition = document.RootElement.GetProperty("definitions").EnumerateArray().Single();
            var bodyContent = definition.GetProperty("body_content").GetString();
            var bodyContentEndLine = definition.GetProperty("body_content_end_line").GetInt32();
            var bodyContentNextStartLine = definition.GetProperty("body_content_next_start_line").GetInt32();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("var marker1", bodyContent, StringComparison.Ordinal);
            Assert.DoesNotContain("var marker2", bodyContent, StringComparison.Ordinal);
            Assert.True(definition.GetProperty("body_content_truncated").GetBoolean());
            Assert.Equal(bodyContentEndLine + 1, bodyContentNextStartLine);
            Assert.True(bodyContentNextStartLine < definition.GetProperty("body_end_line").GetInt32());

            var (nextExitCode, nextStdout, nextStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Compute", "--db", dbPath, "--json", "--body", "--body-start", bodyContentNextStartLine.ToString(CultureInfo.InvariantCulture)],
                _jsonOptions));

            using var nextDocument = ParseJsonOutput(nextStdout);
            var nextDefinition = nextDocument.RootElement.GetProperty("definitions").EnumerateArray().Single();
            var nextBodyContent = nextDefinition.GetProperty("body_content").GetString();

            Assert.Equal(CommandExitCodes.Success, nextExitCode);
            Assert.Equal(string.Empty, nextStderr);
            Assert.Equal(bodyContentNextStartLine, nextDefinition.GetProperty("body_content_start_line").GetInt32());
            Assert.Contains("var marker2", nextBodyContent, StringComparison.Ordinal);
            Assert.DoesNotContain("var marker1", nextBodyContent, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_JsonWithoutBody_IncludesBodyModeHint_Issue3441()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_body_mode_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Target.cs",
                "csharp",
                """
                public class Target
                {
                    public int Compute()
                    {
                        return 42;
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Compute", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var definition = json.GetProperty("definitions").EnumerateArray().Single();
            var bodyMode = json.GetProperty("body_mode");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(JsonValueKind.Null, definition.GetProperty("body_content").ValueKind);
            Assert.False(bodyMode.GetProperty("include_body").GetBoolean());
            Assert.False(bodyMode.GetProperty("definitions_only").GetBoolean());
            Assert.False(bodyMode.GetProperty("body_content_present").GetBoolean());
            Assert.Equal(DbReader.DefinitionBodyMaxLines, bodyMode.GetProperty("default_body_lines").GetInt32());
            Assert.Equal(DbReader.DefinitionBodyMaxRequestedLines, bodyMode.GetProperty("max_body_lines").GetInt32());
            Assert.Contains("--body-only", bodyMode.GetProperty("hint").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_HumanWithoutBody_PrintsBodyModeHint_Issue3441()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_body_mode_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Target.cs",
                "csharp",
                """
                public class Target
                {
                    public int Compute()
                    {
                        return 42;
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Compute", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("Body Hint", stdout, StringComparison.Ordinal);
            Assert.Contains("--body", stdout, StringComparison.Ordinal);
            Assert.Contains("--body-only", stdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_IndentsByContainerDepth()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_depth");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/deep.cs",
                "csharp",
                """
                namespace OuterNs
                {
                    namespace InnerNs
                    {
                        public class OuterClass
                        {
                            public class NestedClass
                            {
                                public class DeeplyNested
                                {
                                    public void Method() { }
                                }
                            }
                        }
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/deep.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var outer = lines.Single(line => line.Contains("namespace OuterNs", StringComparison.Ordinal));
            var inner = lines.Single(line => line.Contains("namespace InnerNs", StringComparison.Ordinal));
            var outerClass = lines.Single(line => line.Contains("public class OuterClass", StringComparison.Ordinal));
            var nestedClass = lines.Single(line => line.Contains("public class NestedClass", StringComparison.Ordinal));
            var deeplyNested = lines.Single(line => line.Contains("public class DeeplyNested", StringComparison.Ordinal));
            var method = lines.Single(line => line.Contains("public void Method()", StringComparison.Ordinal));

            var outerIndex = outer.IndexOf("namespace OuterNs", StringComparison.Ordinal);
            Assert.Equal(4, inner.IndexOf("namespace InnerNs", StringComparison.Ordinal) - outerIndex);
            Assert.Equal(4, outerClass.IndexOf("public class OuterClass", StringComparison.Ordinal) - inner.IndexOf("namespace InnerNs", StringComparison.Ordinal));
            Assert.Equal(4, nestedClass.IndexOf("public class NestedClass", StringComparison.Ordinal) - outerClass.IndexOf("public class OuterClass", StringComparison.Ordinal));
            Assert.Equal(4, deeplyNested.IndexOf("public class DeeplyNested", StringComparison.Ordinal) - nestedClass.IndexOf("public class NestedClass", StringComparison.Ordinal));
            Assert.Equal(4, method.IndexOf("public void Method()", StringComparison.Ordinal) - deeplyNested.IndexOf("public class DeeplyNested", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_CompactJson_CapsSymbolsAndReportsTruncation_Issue3009()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_compact_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/many.cs",
                "csharp",
                """
                public class Many
                {
                    public void M0() { }
                    public void M1() { }
                    public void M2() { }
                    public void M3() { }
                    public void M4() { }
                    public void M5() { }
                    public void M6() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/many.cs", "--db", dbPath, "--compact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols").EnumerateArray().ToList();
            var symbolTruncation = json
                .GetProperty("truncation")
                .GetProperty("sections")
                .GetProperty("symbols");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("compact").GetBoolean());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, symbols.Count);
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, symbolTruncation.GetProperty("returned").GetInt32());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit + 3, symbolTruncation.GetProperty("source_count").GetInt32());
            Assert.True(symbolTruncation.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Json_TruncatesRunawaySignatures_Issue2989()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_signature_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var parameters = string.Join(", ", Enumerable.Range(0, 90).Select(index => $"string parameter{index:D2}"));
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/HugeSignatures.cs",
                "csharp",
                $$"""
                public class HugeSignatures
                {
                    public void Run({{parameters}}) { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/HugeSignatures.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var symbol = document.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "Run");
            var signature = symbol.GetProperty("signature").GetString();
            var displayName = symbol.GetProperty("display_name").GetString();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.NotNull(signature);
            Assert.True(signature!.Length <= 512);
            Assert.EndsWith("...", signature, StringComparison.Ordinal);
            Assert.True(symbol.GetProperty("signature_truncated").GetBoolean());
            Assert.True(symbol.GetProperty("signature_original_length").GetInt32() > signature.Length);
            Assert.Equal($"Run@{symbol.GetProperty("line").GetInt32()}", displayName);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_CompactJson_UsesExplicitLimit_Issue3009()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_compact_limit_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/many.cs",
                "csharp",
                """
                public class Many
                {
                    public void M0() { }
                    public void M1() { }
                    public void M2() { }
                    public void M3() { }
                    public void M4() { }
                    public void M5() { }
                    public void M6() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/many.cs", "--db", dbPath, "--compact", "--limit", "2"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols").EnumerateArray().ToList();
            var symbolTruncation = json
                .GetProperty("truncation")
                .GetProperty("sections")
                .GetProperty("symbols");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("compact").GetBoolean());
            Assert.Equal(2, json.GetProperty("compact_limit").GetInt32());
            Assert.Equal(2, symbols.Count);
            Assert.Equal(2, symbolTruncation.GetProperty("returned").GetInt32());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit + 3, symbolTruncation.GetProperty("source_count").GetInt32());
            Assert.True(symbolTruncation.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Json_ProjectsFiltersAndPagesSymbols_Issue3986()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_projection_paging");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/many.cs",
                "csharp",
                """
                public class Many
                {
                    public void M0() { }
                    public int Value { get; set; }
                    public void M1() { }
                    public void M2() { }
                }
                """);

            var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/many.cs", "--db", dbPath, "--json", "--kind", "function", "--limit", "2", "--outline-fields", "name,line,kind"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            using var firstDocument = ParseJsonOutput(firstStdout);
            var firstJson = firstDocument.RootElement;
            var firstSymbols = firstJson.GetProperty("symbols").EnumerateArray().ToList();

            Assert.Equal(3, firstJson.GetProperty("symbol_count").GetInt32());
            Assert.Equal(3, firstJson.GetProperty("total_symbol_count").GetInt32());
            Assert.Equal(2, firstJson.GetProperty("returned_symbol_count").GetInt32());
            Assert.Equal(0, firstJson.GetProperty("cursor_offset").GetInt32());
            Assert.True(firstJson.GetProperty("has_more").GetBoolean());
            Assert.Equal("outline:2", firstJson.GetProperty("next_cursor").GetString());
            Assert.Equal(new[] { "function" }, firstJson.GetProperty("kind_filter").EnumerateArray().Select(item => item.GetString()).ToArray());
            Assert.Equal(new[] { "name", "line", "kind" }, firstJson.GetProperty("selected_fields").EnumerateArray().Select(item => item.GetString()).ToArray());
            Assert.Equal(new[] { "M0", "M1" }, firstSymbols.Select(symbol => symbol.GetProperty("name").GetString()).ToArray());
            foreach (var symbol in firstSymbols)
                Assert.Equal(new[] { "name", "line", "kind" }, symbol.EnumerateObject().Select(property => property.Name).ToArray());

            var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/many.cs", "--db", dbPath, "--json", "--kind", "function", "--limit", "2", "--cursor", "outline:2", "--outline-fields", "name,line,kind"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, secondExitCode);
            Assert.Equal(string.Empty, secondStderr);
            using var secondDocument = ParseJsonOutput(secondStdout);
            var secondJson = secondDocument.RootElement;
            var secondSymbols = secondJson.GetProperty("symbols").EnumerateArray().ToList();

            Assert.Equal(3, secondJson.GetProperty("total_symbol_count").GetInt32());
            Assert.Equal(1, secondJson.GetProperty("returned_symbol_count").GetInt32());
            Assert.Equal(2, secondJson.GetProperty("cursor_offset").GetInt32());
            Assert.False(secondJson.GetProperty("has_more").GetBoolean());
            Assert.Equal(JsonValueKind.Null, secondJson.GetProperty("next_cursor").ValueKind);
            Assert.Single(secondSymbols);
            Assert.Equal("M2", secondSymbols[0].GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_RejectsInvalidKindFilter_Issue3986()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_invalid_kind");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/many.cs", "--db", dbPath, "--json", "--kind", "function,missing"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("invalid --kind value `missing`", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_UsesNestedSymbolDepthInHumanOutput()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_depth");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/deep.cs",
                "csharp",
                """
                namespace OuterNs
                {
                    namespace InnerNs
                    {
                        public class OuterClass
                        {
                            public class NestedClass
                            {
                                public class DeeplyNested
                                {
                                    public void Method() { }
                                }
                            }
                        }
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/deep.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var outerIndex = stdout.IndexOf("public class OuterClass", StringComparison.Ordinal);
            var nestedIndex = stdout.IndexOf("public class NestedClass", StringComparison.Ordinal);
            var deepIndex = stdout.IndexOf("public class DeeplyNested", StringComparison.Ordinal);

            Assert.True(outerIndex >= 0);
            Assert.True(nestedIndex > outerIndex);
            Assert.True(deepIndex > nestedIndex);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_RejectsMissingMaxLineWidthValue()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_missing_max_line_width");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
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
    public void RunOutline_HumanDisplaysCompactOverloadSignatures()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_overload_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/worker.cs",
                "csharp",
                """
                using System.Threading;

                public class Worker
                {
                    public void Process(string input) { }
                    public void Process(int count, CancellationToken cancellationToken = default) { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/worker.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("Process(string)", stdout);
            Assert.Contains("Process(int, CancellationToken)", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_CSharpTopLevelStatementsFile_WritesHelpfulNote()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_toplevel_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/program.cs",
                "csharp",
                """""
                using System;
                using System.Linq;

                var values = new[] { 1, 2, 3 };
                foreach (var value in values)
                {
                    Console.WriteLine(value);
                }

                Console.WriteLine(Sum(values));

                static int Sum(int[] values)
                {
                    var total = 0;
                    foreach (var value in values)
                    {
                        total += value;
                    }

                    return total;
                }

                Console.WriteLine("done");
                Console.WriteLine(values.Length);
                """"");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/program.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/program.cs", stdout);
            Assert.Contains("Note: no type/namespace declarations found; this file likely uses C# top-level statements.", stderr);
            Assert.Contains("Outline lists imports and local functions only; the executable body is not indexed as symbols.", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Json_CSharpTopLevelStatementsFile_LeavesJsonContractUnchanged()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_toplevel_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/program.cs",
                "csharp",
                """
                using System;

                Console.WriteLine("boot");
                Console.WriteLine("run");
                Console.WriteLine("more");
                Console.WriteLine("lines");
                Console.WriteLine("to");
                Console.WriteLine("cross");
                Console.WriteLine("the");
                Console.WriteLine("top-level");
                Console.WriteLine("statement");
                Console.WriteLine("threshold");
                Console.WriteLine("without");
                Console.WriteLine("declaring");
                Console.WriteLine("types");
                Console.WriteLine("or");
                Console.WriteLine("namespaces");
                Console.WriteLine("in");
                Console.WriteLine("this");
                Console.WriteLine("file");
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/program.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/program.cs", json.GetProperty("path").GetString());
            Assert.Equal("csharp", json.GetProperty("lang").GetString());
            Assert.True(json.TryGetProperty("symbols", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Json_AcceptsAbsolutePathWithExplicitDbOutsideProjectRoot()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_absolute_path");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Sample.cs", "csharp", "namespace Demo;\npublic class Svc { }\n");
            var absolutePath = Path.Combine(projectRoot, "src", "Sample.cs");

            var (relativeExitCode, relativeStdout, relativeStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/Sample.cs", "--db", dbPath, "--json"],
                _jsonOptions));
            var (absoluteExitCode, absoluteStdout, absoluteStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                [absolutePath, "--db", dbPath, "--json"],
                _jsonOptions));

            using var relativeDocument = ParseJsonOutput(relativeStdout);
            using var absoluteDocument = ParseJsonOutput(absoluteStdout);

            Assert.Equal(CommandExitCodes.Success, relativeExitCode);
            Assert.Equal(CommandExitCodes.Success, absoluteExitCode);
            Assert.Equal(string.Empty, relativeStderr);
            Assert.Equal(string.Empty, absoluteStderr);
            Assert.Equal("src/Sample.cs", relativeDocument.RootElement.GetProperty("path").GetString());
            Assert.Equal(relativeDocument.RootElement.GetProperty("path").GetString(), absoluteDocument.RootElement.GetProperty("path").GetString());
            Assert.Equal(relativeDocument.RootElement.GetProperty("symbol_count").GetInt32(), absoluteDocument.RootElement.GetProperty("symbol_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_CSharpStatementOnlyTopLevelFile_WritesHelpfulNote()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_statement_only_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/program.cs",
                "csharp",
                """
                using System;

                Console.WriteLine("boot");
                Console.WriteLine("run");
                Console.WriteLine("more");
                Console.WriteLine("lines");
                Console.WriteLine("to");
                Console.WriteLine("cross");
                Console.WriteLine("the");
                Console.WriteLine("top-level");
                Console.WriteLine("statement");
                Console.WriteLine("threshold");
                Console.WriteLine("without");
                Console.WriteLine("declaring");
                Console.WriteLine("types");
                Console.WriteLine("or");
                Console.WriteLine("namespaces");
                Console.WriteLine("in");
                Console.WriteLine("this");
                Console.WriteLine("file");
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/program.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/program.cs", stdout);
            Assert.Contains("Note: no type/namespace declarations found; this file likely uses C# top-level statements.", stderr);
            Assert.Contains("Outline lists imports and local functions only; the executable body is not indexed as symbols.", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_CSharpUsingVarTopLevelFile_WritesHelpfulNote()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_using_var_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/program.cs",
                "csharp",
                """
                using System;
                using System.IO;

                using var stream01 = new MemoryStream();
                using var stream02 = new MemoryStream();
                using var stream03 = new MemoryStream();
                using var stream04 = new MemoryStream();
                using var stream05 = new MemoryStream();
                using var stream06 = new MemoryStream();
                using var stream07 = new MemoryStream();
                using var stream08 = new MemoryStream();
                using var stream09 = new MemoryStream();
                using var stream10 = new MemoryStream();
                using var stream11 = new MemoryStream();
                using var stream12 = new MemoryStream();
                using var stream13 = new MemoryStream();
                using var stream14 = new MemoryStream();
                using var stream15 = new MemoryStream();
                using var stream16 = new MemoryStream();
                using var stream17 = new MemoryStream();
                using var stream18 = new MemoryStream();
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/program.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/program.cs", stdout);
            Assert.Contains("Note: no type/namespace declarations found; this file likely uses C# top-level statements.", stderr);
            Assert.Contains("Outline lists imports and local functions only; the executable body is not indexed as symbols.", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_CSharpUsingStatementTopLevelFile_WritesHelpfulNote()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_using_statement_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/program.cs",
                "csharp",
                """
                using System;
                using System.IO;

                using (var stream01 = new MemoryStream())
                {
                    Console.WriteLine(stream01.Length);
                }
                using (var stream02 = new MemoryStream())
                {
                    Console.WriteLine(stream02.Length);
                }
                using (var stream03 = new MemoryStream())
                {
                    Console.WriteLine(stream03.Length);
                }
                using (var stream04 = new MemoryStream())
                {
                    Console.WriteLine(stream04.Length);
                }
                using (var stream05 = new MemoryStream())
                {
                    Console.WriteLine(stream05.Length);
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/program.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/program.cs", stdout);
            Assert.Contains("Note: no type/namespace declarations found; this file likely uses C# top-level statements.", stderr);
            Assert.Contains("Outline lists imports and local functions only; the executable body is not indexed as symbols.", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_CSharpLocalFunctionsOnlyFile_DoesNotWriteTopLevelStatementsHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_local_functions_only_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/program.cs",
                "csharp",
                """
                using System;

                static int Sum(int left, int right)
                {
                    return left + right;
                }

                static int Diff(int left, int right)
                {
                    return left - right;
                }

                static int Mul(int left, int right)
                {
                    return left * right;
                }

                static int Div(int left, int right)
                {
                    return left / right;
                }

                static void Print(int value)
                {
                    Console.WriteLine(value);
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/program.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/program.cs", stdout);
            Assert.DoesNotContain("likely uses C# top-level statements", stderr);
            Assert.DoesNotContain("executable body is not indexed as symbols", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_GlobalUsingsFile_DoesNotWriteTopLevelStatementsHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_global_usings_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/GlobalUsings.cs",
                "csharp",
                """
                global using System;
                global using System.IO;
                global using System.Linq;
                global using System.Text;
                global using System.Text.Json;
                global using System.Text.Json.Serialization;
                global using System.Collections.Generic;
                global using System.Collections.Immutable;
                global using System.Diagnostics;
                global using System.Globalization;
                global using System.Net.Http;
                global using System.Net.Http.Json;
                global using System.Threading;
                global using System.Threading.Tasks;
                global using Microsoft.Extensions.Logging;
                global using Microsoft.Extensions.Options;
                global using Microsoft.Data.Sqlite;
                global using CodeIndex.Models;
                global using CodeIndex.Database;
                global using CodeIndex.Indexer;
                global using CodeIndex.Cli;
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/GlobalUsings.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/GlobalUsings.cs", stdout);
            Assert.DoesNotContain("likely uses C# top-level statements", stderr);
            Assert.DoesNotContain("executable body is not indexed as symbols", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_AssemblyInfoFile_DoesNotWriteTopLevelStatementsHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_assembly_info_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Properties/AssemblyInfo.cs",
                "csharp",
                """
                using System.Reflection;

                [assembly: AssemblyTitle("CodeIndex")]
                [assembly: AssemblyDescription("Tests top-level hint suppression")]
                [assembly: AssemblyConfiguration("Debug")]
                [assembly: AssemblyCompany("Widthdom")]
                [assembly: AssemblyProduct("CodeIndex")]
                [assembly: AssemblyCopyright("Copyright Widthdom")]
                [assembly: AssemblyTrademark("")]
                [assembly: AssemblyCulture("")]
                [assembly: AssemblyVersion("1.0.0.0")]
                [assembly: AssemblyFileVersion("1.0.0.0")]
                [assembly: AssemblyMetadata("Build", "Local")]
                [assembly: AssemblyMetadata("Environment", "Test")]
                [assembly: AssemblyMetadata("Flavor", "Debug")]
                [assembly: AssemblyMetadata("Feature", "OutlineHint")]
                [assembly: AssemblyMetadata("Branch", "pr-149")]
                [assembly: AssemblyMetadata("Commit", "local")]
                [assembly: AssemblyMetadata("Purpose", "Negative coverage")]
                [assembly: AssemblyMetadata("Owner", "CodeIndex Tests")]
                [assembly: AssemblyMetadata("Language", "csharp")]
                [assembly: AssemblyMetadata("FileKind", "AssemblyInfo")]
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/Properties/AssemblyInfo.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/Properties/AssemblyInfo.cs", stdout);
            Assert.DoesNotContain("likely uses C# top-level statements", stderr);
            Assert.DoesNotContain("executable body is not indexed as symbols", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_GeneratedGlobalUsingsFile_DoesNotWriteTopLevelStatementsHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_generated_global_usings_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/GlobalUsings.g.cs",
                "csharp",
                """
                // <auto-generated/>
                #pragma warning disable 1591
                global using System;
                global using System.IO;
                global using System.Linq;
                global using System.Text;
                global using System.Text.Json;
                global using System.Text.Json.Serialization;
                global using System.Collections.Generic;
                global using System.Collections.Immutable;
                global using System.Diagnostics;
                global using System.Globalization;
                global using System.Net.Http;
                global using System.Net.Http.Json;
                global using System.Threading;
                global using System.Threading.Tasks;
                global using Microsoft.Extensions.Logging;
                global using Microsoft.Extensions.Options;
                global using Microsoft.Data.Sqlite;
                global using CodeIndex.Models;
                global using CodeIndex.Database;
                global using CodeIndex.Indexer;
                global using CodeIndex.Cli;
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/GlobalUsings.g.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/GlobalUsings.g.cs", stdout);
            Assert.DoesNotContain("likely uses C# top-level statements", stderr);
            Assert.DoesNotContain("executable body is not indexed as symbols", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_GeneratedAssemblyInfoFile_DoesNotWriteTopLevelStatementsHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_generated_assembly_info_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Properties/AssemblyInfo.cs",
                "csharp",
                """
                // <auto-generated/>
                #pragma warning disable 1591
                using System.Reflection;

                [assembly: AssemblyTitle("CodeIndex")]
                [assembly: AssemblyDescription("Tests top-level hint suppression")]
                [assembly: AssemblyConfiguration("Debug")]
                [assembly: AssemblyCompany("Widthdom")]
                [assembly: AssemblyProduct("CodeIndex")]
                [assembly: AssemblyCopyright("Copyright Widthdom")]
                [assembly: AssemblyTrademark("")]
                [assembly: AssemblyCulture("")]
                [assembly: AssemblyVersion("1.0.0.0")]
                [assembly: AssemblyFileVersion("1.0.0.0")]
                [assembly: AssemblyMetadata("Build", "Local")]
                [assembly: AssemblyMetadata("Environment", "Test")]
                [assembly: AssemblyMetadata("Flavor", "Debug")]
                [assembly: AssemblyMetadata("Feature", "OutlineHint")]
                [assembly: AssemblyMetadata("Branch", "pr-149")]
                [assembly: AssemblyMetadata("Commit", "local")]
                [assembly: AssemblyMetadata("Purpose", "Negative coverage")]
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/Properties/AssemblyInfo.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/Properties/AssemblyInfo.cs", stdout);
            Assert.DoesNotContain("likely uses C# top-level statements", stderr);
            Assert.DoesNotContain("executable body is not indexed as symbols", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_BlockCommentGlobalUsingsFile_DoesNotWriteTopLevelStatementsHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_block_comment_global_usings_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/GlobalUsings.cs",
                "csharp",
                """
                /*
                 * <auto-generated/>
                 * This file is generated during build.
                 */
                #pragma warning disable 1591
                global using System;
                global using System.IO;
                global using System.Linq;
                global using System.Text;
                global using System.Text.Json;
                global using System.Text.Json.Serialization;
                global using System.Collections.Generic;
                global using System.Collections.Immutable;
                global using System.Diagnostics;
                global using System.Globalization;
                global using System.Net.Http;
                global using System.Net.Http.Json;
                global using System.Threading;
                global using System.Threading.Tasks;
                global using Microsoft.Extensions.Logging;
                global using Microsoft.Extensions.Options;
                global using Microsoft.Data.Sqlite;
                global using CodeIndex.Models;
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/GlobalUsings.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/GlobalUsings.cs", stdout);
            Assert.DoesNotContain("likely uses C# top-level statements", stderr);
            Assert.DoesNotContain("executable body is not indexed as symbols", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_BlockCommentAssemblyInfoFile_DoesNotWriteTopLevelStatementsHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_block_comment_assembly_info_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Properties/AssemblyInfo.cs",
                "csharp",
                """
                /*
                 * <auto-generated/>
                 * This file is generated during build.
                 */
                #pragma warning disable 1591
                using System.Reflection;

                [assembly: AssemblyTitle("CodeIndex")]
                [assembly: AssemblyDescription("Tests top-level hint suppression")]
                [assembly: AssemblyConfiguration("Debug")]
                [assembly: AssemblyCompany("Widthdom")]
                [assembly: AssemblyProduct("CodeIndex")]
                [assembly: AssemblyCopyright("Copyright Widthdom")]
                [assembly: AssemblyTrademark("")]
                [assembly: AssemblyCulture("")]
                [assembly: AssemblyVersion("1.0.0.0")]
                [assembly: AssemblyFileVersion("1.0.0.0")]
                [assembly: AssemblyMetadata("Build", "Local")]
                [assembly: AssemblyMetadata("Environment", "Test")]
                [assembly: AssemblyMetadata("Flavor", "Debug")]
                [assembly: AssemblyMetadata("Feature", "OutlineHint")]
                [assembly: AssemblyMetadata("Branch", "pr-149")]
                [assembly: AssemblyMetadata("Commit", "local")]
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/Properties/AssemblyInfo.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/Properties/AssemblyInfo.cs", stdout);
            Assert.DoesNotContain("likely uses C# top-level statements", stderr);
            Assert.DoesNotContain("executable body is not indexed as symbols", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Human_ExternAliasOnlyFile_DoesNotWriteTopLevelStatementsHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_extern_alias_only_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Aliases.cs",
                "csharp",
                """
                extern alias Foo01;
                extern alias Foo02;
                extern alias Foo03;
                extern alias Foo04;
                extern alias Foo05;
                extern alias Foo06;
                extern alias Foo07;
                extern alias Foo08;
                extern alias Foo09;
                extern alias Foo10;
                extern alias Foo11;
                extern alias Foo12;
                extern alias Foo13;
                extern alias Foo14;
                extern alias Foo15;
                extern alias Foo16;
                extern alias Foo17;
                extern alias Foo18;
                extern alias Foo19;
                extern alias Foo20;
                extern alias Foo21;
                extern alias Foo22;
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/Aliases.cs", "--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("# src/Aliases.cs", stdout);
            Assert.DoesNotContain("likely uses C# top-level statements", stderr);
            Assert.DoesNotContain("executable body is not indexed as symbols", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_CssFamilyLessFontFaceSameLineStillFindsFollowingRule()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_css_familyless_fontface_inline");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "styles.css"),
                """
                @font-face { src: url("no-family.woff2"); } .after { color: red; }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/styles.css", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(outlineStdout);
            var names = document.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .Where(name => name != null)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Contains(".after", names);
            Assert.DoesNotContain("@font-face", names);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_CSharpSameLineSiblingEnumsIncludesBothEnumsAndMembers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_enum_same_line_siblings");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/mode.cs",
                "csharp",
                """
                namespace Demo;

                public enum InlineA { A1 } public enum InlineB { B1 }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/mode.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var names = document.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .Where(name => name != null)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("InlineA", names);
            Assert.Contains("InlineB", names);
            Assert.Contains("A1", names);
            Assert.Contains("B1", names);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_CSharpSameLineNamespaceBodyIncludesNestedEnumAndMembers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_enum_same_line_namespace");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/mode.cs",
                "csharp",
                """
                namespace Demo { public enum E { A } }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/mode.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var names = document.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .Where(name => name != null)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("Demo", names);
            Assert.Contains("E", names);
            Assert.Contains("A", names);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_CSharpBraceCharLiteralKeepsMethodInsideClass()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_csharp_brace_char");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """""
                namespace Demo;

                public class FixtureHost
                {
                    public bool IsClosingBrace(char c)
                    {
                        return c is not '}';
                    }

                    public void AfterBraceLiteral()
                    {
                    }
                }
                """"");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (inspectExitCode, inspectStdout, inspectStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["AfterBraceLiteral", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(inspectStdout);
            var json = document.RootElement;
            var definition = Assert.Single(json.GetProperty("definitions").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr);
            Assert.Equal("AfterBraceLiteral", definition.GetProperty("name").GetString());
            Assert.Equal("class", definition.GetProperty("container_kind").GetString());
            Assert.Equal("FixtureHost", definition.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_CSharpMultilineRawStringBraceKeepsMethodInsideClass()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_csharp_raw_string_brace");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """"
                namespace Demo;

                public class FixtureHost
                {
                    public string UsesRawFixture()
                    {
                        return """
                            }
                            """;
                    }

                    public void AfterRawString()
                    {
                    }
                }
                """");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (inspectExitCode, inspectStdout, inspectStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["AfterRawString", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(inspectStdout);
            var json = document.RootElement;
            var definition = Assert.Single(json.GetProperty("definitions").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr);
            Assert.Equal("AfterRawString", definition.GetProperty("name").GetString());
            Assert.Equal("class", definition.GetProperty("container_kind").GetString());
            Assert.Equal("FixtureHost", definition.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_CSharpMultilineVerbatimStringBraceKeepsMethodInsideClass()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_csharp_verbatim_string_brace");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """"
                namespace Demo;

                public class FixtureHost
                {
                    public string UsesVerbatimFixture()
                    {
                        return @"
                {
                ";
                    }

                    public void AfterVerbatimString()
                    {
                    }
                }
                """");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (inspectExitCode, inspectStdout, inspectStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["AfterVerbatimString", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(inspectStdout);
            var json = document.RootElement;
            var definition = Assert.Single(json.GetProperty("definitions").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr);
            Assert.Equal("AfterVerbatimString", definition.GetProperty("name").GetString());
            Assert.Equal("class", definition.GetProperty("container_kind").GetString());
            Assert.Equal("FixtureHost", definition.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_RustLifetimeAnnotationsKeepBodyRangesIntact()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_rust_lifetime");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.rs"),
                """
                pub struct Holder<'a> {
                    value: &'a str,
                }

                impl<'a> Holder<'a> {
                    pub fn get(&self) -> &'a str {
                        self.value
                    }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/fixture.rs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(outlineStdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols").EnumerateArray().ToArray();
            var holder = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "struct" && symbol.GetProperty("name").GetString() == "Holder"));
            var get = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "function" && symbol.GetProperty("name").GetString() == "get"));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal(3, holder.GetProperty("end_line").GetInt32());
            Assert.Equal(8, get.GetProperty("end_line").GetInt32());
            Assert.Equal("class", get.GetProperty("container_kind").GetString());
            Assert.Equal("Holder", get.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_CSharpSplitPropertyHeader_DoesNotEmitPhantomFunctionAndKeepsSignature()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_split_property_header");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """
                namespace Demo;

                public class Model
                {
                    public string
                        SplitName
                        => "x";
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/fixture.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var outlineDocument = ParseJsonOutput(outlineStdout);
            var outlineJson = outlineDocument.RootElement;
            var splitNameSymbols = outlineJson.GetProperty("symbols").EnumerateArray()
                .Where(symbol => symbol.GetProperty("name").GetString() == "SplitName")
                .ToArray();
            var property = Assert.Single(splitNameSymbols.Where(symbol => symbol.GetProperty("kind").GetString() == "property"));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Single(splitNameSymbols);
            Assert.Equal("public string SplitName => \"x\";", property.GetProperty("signature").GetString());
            Assert.Equal(5, property.GetProperty("start_line").GetInt32());
            Assert.Equal(7, property.GetProperty("end_line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_CSharpLongGenericMultilinePropertyHeader_KeepsReturnTypeAndSignature()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_long_generic_property_header");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """
                namespace Demo;

                public class Model
                {
                    public Dictionary<
                        string,
                        List<
                            int
                        >>
                        Count
                        => new();
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/fixture.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var outlineDocument = ParseJsonOutput(outlineStdout);
            var outlineJson = outlineDocument.RootElement;
            var property = Assert.Single(outlineJson.GetProperty("symbols").EnumerateArray().Where(symbol =>
                symbol.GetProperty("kind").GetString() == "property"
                && symbol.GetProperty("name").GetString() == "Count"));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal("Dictionary<string,List<int>>", property.GetProperty("return_type").GetString());
            Assert.Equal("public Dictionary< string, List< int >> Count => new();", property.GetProperty("signature").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_CSharpBraceOnNextLinePropertyHeader_KeepsHeaderSignature()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_brace_property_header");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """
                namespace Demo;

                public class Model
                {
                    public string SplitName
                    {
                        get;
                        set;
                    }

                    public int Count
                    { get => 1; }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/fixture.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var outlineDocument = ParseJsonOutput(outlineStdout);
            var outlineJson = outlineDocument.RootElement;
            var splitName = Assert.Single(outlineJson.GetProperty("symbols").EnumerateArray().Where(symbol =>
                symbol.GetProperty("kind").GetString() == "property"
                && symbol.GetProperty("name").GetString() == "SplitName"));
            var count = Assert.Single(outlineJson.GetProperty("symbols").EnumerateArray().Where(symbol =>
                symbol.GetProperty("kind").GetString() == "property"
                && symbol.GetProperty("name").GetString() == "Count"));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal("public string SplitName {", splitName.GetProperty("signature").GetString());
            Assert.Equal("public int Count {", count.GetProperty("signature").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_CSharpPartialPropertyImplementation_WithAccessorAttribute_IsDetected()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_accessor_attribute_property");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """
                namespace Demo;

                public partial class Model
                {
                    public partial string Name { get; set; }
                }

                public partial class Model
                {
                    public partial string Name { [System.Obsolete] get => "x"; set { } }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/fixture.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var outlineDocument = ParseJsonOutput(outlineStdout);
            var outlineJson = outlineDocument.RootElement;
            var names = outlineJson.GetProperty("symbols").EnumerateArray()
                .Where(symbol => symbol.GetProperty("kind").GetString() == "property"
                    && symbol.GetProperty("name").GetString() == "Name")
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal(2, names.Length);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_CSharpPartialPropertyImplementation_WithMultilineAccessorAttribute_IsDetected()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_multiline_accessor_attribute_property");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """
                namespace Demo;

                public partial class Model
                {
                    public partial string Name
                    {
                        [System.Obsolete(
                            "x"
                        )]
                        get => "x";
                        set { }
                    }

                    public int Other => 1;
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/fixture.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var outlineDocument = ParseJsonOutput(outlineStdout);
            var outlineJson = outlineDocument.RootElement;
            var symbols = outlineJson.GetProperty("symbols").EnumerateArray().ToArray();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Contains(symbols, symbol => symbol.GetProperty("kind").GetString() == "property" && symbol.GetProperty("name").GetString() == "Name");
            Assert.Contains(symbols, symbol => symbol.GetProperty("kind").GetString() == "property" && symbol.GetProperty("name").GetString() == "Other");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_JavaScriptStringBraceKeepsFollowingMethodInsideClass()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_js_string_brace");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "example.js"),
                """
                export class Example {
                  foo() {
                    const value = "}";
                    return value;
                  }

                  bar() {
                    return 1;
                  }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/example.js", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(outlineStdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols").EnumerateArray().ToArray();
            var example = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "class" && symbol.GetProperty("name").GetString() == "Example"));
            var foo = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "function" && symbol.GetProperty("name").GetString() == "foo"));
            var bar = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "function" && symbol.GetProperty("name").GetString() == "bar"));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal(10, example.GetProperty("end_line").GetInt32());
            Assert.Equal(5, foo.GetProperty("end_line").GetInt32());
            Assert.Equal("class", bar.GetProperty("container_kind").GetString());
            Assert.Equal("Example", bar.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_JavaScriptTemplateLiteralBraceKeepsFollowingMethodInsideClass()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_js_template_brace");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "example.js"),
                """
                export class Example {
                  foo() {
                    const value = `}`;
                    return value;
                  }

                  bar() {
                    return 1;
                  }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/example.js", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(outlineStdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols").EnumerateArray().ToArray();
            var example = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "class" && symbol.GetProperty("name").GetString() == "Example"));
            var foo = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "function" && symbol.GetProperty("name").GetString() == "foo"));
            var bar = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "function" && symbol.GetProperty("name").GetString() == "bar"));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal(10, example.GetProperty("end_line").GetInt32());
            Assert.Equal(5, foo.GetProperty("end_line").GetInt32());
            Assert.Equal("class", bar.GetProperty("container_kind").GetString());
            Assert.Equal("Example", bar.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_TypeScriptTemplateInterpolationBracesKeepFollowingMethodInsideClass()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_ts_template_interp");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "example.ts"),
                """
                export class Example {
                  foo() {
                    const value = `${format({ answer: 42 })}`;
                    return value;
                  }

                  bar() {
                    return 1;
                  }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/example.ts", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(outlineStdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols").EnumerateArray().ToArray();
            var example = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "class" && symbol.GetProperty("name").GetString() == "Example"));
            var foo = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "function" && symbol.GetProperty("name").GetString() == "foo"));
            var bar = Assert.Single(symbols.Where(symbol => symbol.GetProperty("kind").GetString() == "function" && symbol.GetProperty("name").GetString() == "bar"));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal(10, example.GetProperty("end_line").GetInt32());
            Assert.Equal(5, foo.GetProperty("end_line").GetInt32());
            Assert.Equal("class", bar.GetProperty("container_kind").GetString());
            Assert.Equal("Example", bar.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_Json_StaleSqlGraphContractIncludesDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["fn_Target", "--db", dbPath, "--json", "--lang", "sql", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_MixedRepoStaleSqlGraphContractDoesNotDegradePureCSharpBundle()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_mixed_sql_graph_contract");
        try
        {
            var dbPath = CreateMixedSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["N", "--db", dbPath, "--json", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("N", json.GetProperty("query").GetString());
            Assert.False(json.TryGetProperty("sql_graph_contract_ready", out _));
            Assert.False(json.TryGetProperty("sql_graph_contract_degraded_reason", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_JsonKeepsSubscribeReferencesVisibleInBundle()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_subscribe_bundle");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Changed", "--db", dbPath, "--json", "--lang", "csharp", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var reference = Assert.Single(json.GetProperty("references").EnumerateArray());
            var caller = Assert.Single(json.GetProperty("callers").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("subscribe", reference.GetProperty("reference_kind").GetString());
            Assert.Equal("Hook", reference.GetProperty("container_name").GetString());
            Assert.Equal("Hook", caller.GetProperty("caller_name").GetString());
            Assert.Equal("Changed", caller.GetProperty("callee_name").GetString());
            Assert.Empty(json.GetProperty("callees").EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_JsonIncludesResolvedEnumMemberReferences()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_enum_member_bundle");
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
                    public void Use()
                    {
                        _ = Nested.A;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["A", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var definition = Assert.Single(json.GetProperty("definitions").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("A", definition.GetProperty("name").GetString());
            Assert.Equal("enum", definition.GetProperty("container_kind").GetString());
            Assert.Equal("Nested", definition.GetProperty("container_name").GetString());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            var reference = Assert.Single(json.GetProperty("references").EnumerateArray());
            Assert.Equal("A", reference.GetProperty("symbol_name").GetString());
            Assert.Equal("Use", reference.GetProperty("container_name").GetString());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpMultiLineConstantPatternKeepsFileReferenceCountWithoutPhantomProperties()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_multiline_constant_pattern");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/A/Color.cs", "csharp",
                """
                namespace A;

                public enum Color
                {
                    Red
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/A/UseDeep.cs", "csharp",
                """
                using static A.Color;

                namespace A;

                public class DemoDeep
                {
                    public bool Match(object value) => value is
                        Red
                        or
                        Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Match", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--path", "src/A/UseDeep.cs", "--body"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var file = json.GetProperty("file");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(4, file.GetProperty("symbol_count").GetInt32());
            Assert.Equal(2, file.GetProperty("reference_count").GetInt32());
            Assert.DoesNotContain(
                json.GetProperty("nearby_symbols").EnumerateArray(),
                symbol => symbol.GetProperty("kind").GetString() == "property"
                    && symbol.GetProperty("name").GetString() == "Red");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_Json_CSharpMultiLineExpressionBodiedMethod_KeepsFullSignatureAndSiblingField()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_outline_csharp_multiline_expr_body_signature");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                namespace Demo;

                public enum Color
                {
                    Red
                }

                public sealed class Uses
                {
                    public bool Match(object value) => value is
                        Red; public int X;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/Use.cs", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray().ToList();
            var match = Assert.Single(symbols.Where(symbol =>
                symbol.GetProperty("kind").GetString() == "function"
                && symbol.GetProperty("name").GetString() == "Match"));
            var x = Assert.Single(symbols.Where(symbol =>
                symbol.GetProperty("kind").GetString() == "property"
                && symbol.GetProperty("name").GetString() == "X"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("public bool Match(object value) => value is Red;", match.GetProperty("signature").GetString());
            Assert.Equal("public int X;", x.GetProperty("signature").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_NonExactJson_TreatsEnumMembersAsGraphSupported()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_enum_member_bundle_non_exact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Color
                {
                    Red,
                    Green
                }

                public class UsesColor
                {
                    public Color Shade => Color.Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var definition = Assert.Single(json.GetProperty("definitions").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Red", definition.GetProperty("name").GetString());
            Assert.Equal("enum", definition.GetProperty("container_kind").GetString());
            Assert.Equal("Color", definition.GetProperty("container_name").GetString());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            var reference = Assert.Single(json.GetProperty("references").EnumerateArray());
            Assert.Equal("Shade", reference.GetProperty("container_name").GetString());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpCompactSameLineTypeBody_PrefersInnermostMethodContainer()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_compact_same_line_type_body");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var reference = Assert.Single(json.GetProperty("references").EnumerateArray());
            var caller = Assert.Single(json.GetProperty("callers").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("function", reference.GetProperty("container_kind").GetString());
            Assert.Equal("M", reference.GetProperty("container_name").GetString());
            Assert.Equal("function", caller.GetProperty("caller_kind").GetString());
            Assert.Equal("M", caller.GetProperty("caller_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_NonExactJson_MixedCallableAndEnumMemberKeepsGraphSupported()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_mixed_callable_enum");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["A", "--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.NotEmpty(json.GetProperty("references").EnumerateArray());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_LargeMixedCandidateSetKeepsGraphClean()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_large_mixed_exact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < 40; i++)
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_MixedCSharpEnumMemberHitsKeepGraphSupported()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_mixed_enum_member_bundle");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum E
                {
                    A
                }

                public class Holder
                {
                    public int A { get; }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["A", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("definitions").GetArrayLength());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CrossLanguageMixedHitCombinesPrimaryGraphAndEnumGapReason()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_cross_language_mixed_exact");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("javascript", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
            Assert.Equal("Call-graph extraction is indexed for 'javascript'.", json.GetProperty("graph_support_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CrossLanguageMixedHitPrefersGraphCapablePrimaryDefinition()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_cross_language_primary_alignment");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "web/app.js", "javascript",
                """
                function Ready() {}

                function Helper() {}

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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var nearbyPaths = json.GetProperty("nearby_symbols")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("path").GetString())
                .Where(path => path != null)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var references = json.GetProperty("references").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("javascript", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
            Assert.Equal("web/app.js", json.GetProperty("file").GetProperty("path").GetString());
            Assert.Contains("web/app.js", nearbyPaths);
            Assert.DoesNotContain("src/status.cs", nearbyPaths);
            Assert.Contains(json.GetProperty("nearby_symbols").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "Helper");
            Assert.NotEmpty(references);
            Assert.All(references, reference =>
                Assert.Equal("javascript", reference.GetProperty("lang").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpEnumMemberShortNameCollisionDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_short_name_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace A;

                public enum Status
                {
                    Ready
                }

                namespace B;

                public static class Status
                {
                    public static int Ready = 1;
                }

                public class Uses
                {
                    public int Read()
                    {
                        return Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.Equal("Call-graph extraction is indexed for 'csharp'.", json.GetProperty("graph_support_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpEnumMemberUsingAliasCollisionDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_alias_collision");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace A;

                public enum Status
                {
                    Ready
                }

                public static class Values
                {
                    public static int Ready = 1;
                }

                namespace B;

                using Status = A.Values;

                public class UsesAlias
                {
                    public int Read()
                    {
                        return Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpEnumMemberAttributeArgumentUsesMetadataKindWithoutCallerEdge()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_attribute_argument");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                using System;

                namespace Demo;

                public enum ConverterStrategy
                {
                    AllowNumbers,
                    Strict
                }

                [AttributeUsage(AttributeTargets.Class)]
                public sealed class JsonConverterAttribute : Attribute
                {
                    public JsonConverterAttribute(ConverterStrategy strategy) { }
                }

                [JsonConverter(ConverterStrategy.AllowNumbers)]
                public class UsesAttribute
                {
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["AllowNumbers", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var reference = Assert.Single(json.GetProperty("references").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("attribute", reference.GetProperty("reference_kind").GetString());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpValueReceiverNamedLikeEnumDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_value_receiver_collision");
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

                    public int Read()
                    {
                        return Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpStaticPropertyShadowStillSuppressesReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_static_property_collision");
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
                    public static Holder Status { get; } = new();

                    public static int Read()
                    {
                        return Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpGlobalQualifiedEnumMemberSurvivesPropertyShadowing()
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var reference = Assert.Single(json.GetProperty("references").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("M", reference.GetProperty("container_name").GetString());
            Assert.Equal(12, reference.GetProperty("line").GetInt32());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpGlobalQualifiedEnumMemberSurvivesConflictingUsingAlias()
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var reference = Assert.Single(json.GetProperty("references").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("M", reference.GetProperty("container_name").GetString());
            Assert.Equal(23, reference.GetProperty("line").GetInt32());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpGlobalQualifiedUsingAliasNameDoesNotCreateReference()
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Red", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpIndentedLocalNamedLikeEnumDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_indented_local_collision");
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
                    public Demo.Status Read(bool flag)
                    {
                        if (flag)
                        {
                            Holder Status = new();
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
    public void RunInspect_ExactJson_CSharpLambdaParameterNamedLikeEnumDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_lambda_parameter_collision");
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
                    public Func<Holder, int> Build()
                    {
                        return Status => Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpMultiLineLambdaParameterNamedLikeEnumDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_multiline_lambda_collision");
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
                    public Func<Holder, int> Build()
                    {
                        return
                            (Status) =>
                                Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpSwitchExpressionRecursivePatternVariableDoesNotLeakReferenceContext()
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var referenceLines = json.GetProperty("references")
                .EnumerateArray()
                .Select(reference => reference.GetProperty("line").GetInt32())
                .ToArray();
            var callerReferenceCounts = json.GetProperty("callers")
                .EnumerateArray()
                .Select(caller => caller.GetProperty("reference_count").GetInt32())
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal([20], referenceLines);
            Assert.Equal([1], callerReferenceCounts);
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpQueryRangeVariableNamedLikeEnumDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_range_collision");
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
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        return from Status in items
                               select Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpQueryRangeVariableNamedLikeEnum_OrderByDirectionalCommaDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_orderby_directional_collision");
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
                               orderby Status descending, items.Count() ascending
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr));
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpQueryRangeVariableMemberNamedSelectSeparatedBySpacesDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_orderby_member_named_select_spaced_dot_collision");
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
                               orderby Status . select, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr));
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpQueryRangeVariableOrderByObjectInitializerCommaDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_query_orderby_object_initializer_collision");
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

                public sealed class Key
                {
                    public int A { get; set; }
                    public int B { get; set; }
                }

                public sealed class Uses
                {
                    public IEnumerable<int> Read(IEnumerable<Holder> items)
                    {
                        return from Status in items
                               orderby new Key { A = Status.Ready, B = items.Count() }, items.Count()
                               select Status.Ready;
                    }
                }
                """);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr));
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpNestedQueryBeforeParenthesizedOrderByCommaPreservesOnlyTrailingEnumReferenceBundle()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_nested_query_parenthesized_orderby_collision");
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
            var (exitCode, stdout, stderr) = RunBuiltCli(["inspect", "Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"]);

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var reference = Assert.Single(json.GetProperty("references").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Read", reference.GetProperty("container_name").GetString());
            Assert.Contains("Status.Ready", reference.GetProperty("context").GetString(), StringComparison.Ordinal);
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpParameterNamedLikeEnumDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_parameter_collision");
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
                    public int Read(Holder Status)
                    {
                        return Status.Ready;
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.Empty(json.GetProperty("callers").EnumerateArray());
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpQueryRangeVariableGenericSelectorDoesNotLeakEnumReferenceBundle()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_query_generic_selector");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("definitions").GetArrayLength());
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpQueryRangeVariableShiftSelectorPreservesOnlyTrailingEnumReferenceBundle()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_query_shift_selector");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var reference = Assert.Single(json.GetProperty("references").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("definitions").GetArrayLength());
            Assert.Equal(28, reference.GetProperty("line").GetInt32());
            Assert.Equal("Read", reference.GetProperty("container_name").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpQueryRangeVariableGenericTypePatternDesignationDoesNotLeakEnumReferenceBundle()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_query_generic_type_pattern_designation");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("definitions").GetArrayLength());
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpQueryRangeVariableGenericTypePatternWithoutDesignationDoesNotLeakEnumReferenceBundle()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_query_generic_type_pattern_without_designation");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("definitions").GetArrayLength());
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
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
    public void RunInspect_ExactJson_CSharpQueryRangeVariableGenericAsNullComparisonDoesNotLeakEnumReferenceBundle(string comparisonOperator)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_query_runner_inspect_query_generic_as_null_{comparisonOperator switch { "!=" => "not_equal", "==" => "equal_equal", _ => "unknown" }}");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("definitions").GetArrayLength());
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpQueryRangeVariableTupleGenericSelectorDoesNotLeakEnumReferenceBundle()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_query_tuple_generic_selector");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("definitions").GetArrayLength());
            Assert.Empty(json.GetProperty("references").EnumerateArray());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_CSharpSwitchExpressionDeclarationPatternWhenInCommentDoesNotLeakReferenceContext()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_enum_member_switch_expression_declaration_pattern_when_comment_collision");
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
                            Holder Status /* when comment */ => (Demo.Status)Status.Ready,
                            _ => Demo.Status.Ready
                        };
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var referenceLines = json.GetProperty("references")
                .EnumerateArray()
                .Select(reference => reference.GetProperty("line").GetInt32())
                .ToArray();
            var callerReferenceCounts = json.GetProperty("callers")
                .EnumerateArray()
                .Select(caller => caller.GetProperty("reference_count").GetInt32())
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal([20], referenceLines);
            Assert.Equal([1], callerReferenceCounts);
            Assert.Equal("csharp", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_Json_CrossLanguageMixedHitPrefersGraphCapablePrimaryDefinition()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_cross_language_primary_alignment_non_exact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "web/app.js", "javascript",
                """
                function Ready() {}

                function Helper() {}

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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var nearbyPaths = json.GetProperty("nearby_symbols")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("path").GetString())
                .Where(path => path != null)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var references = json.GetProperty("references").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("javascript", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
            Assert.Equal("web/app.js", json.GetProperty("file").GetProperty("path").GetString());
            Assert.Contains("web/app.js", nearbyPaths);
            Assert.DoesNotContain("src/status.cs", nearbyPaths);
            Assert.Contains(json.GetProperty("nearby_symbols").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "Helper");
            Assert.NotEmpty(references);
            Assert.All(references, reference =>
                Assert.Equal("javascript", reference.GetProperty("lang").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactJson_PathScopeDoesNotInheritOutOfScopeEnumMemberMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_scoped_js");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Ready", "--db", dbPath, "--json", "--exact-name", "--path", "web/"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("javascript", json.GetProperty("file").GetProperty("lang").GetString());
            Assert.Equal("javascript", json.GetProperty("graph_language").GetString());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_JsonKeepsSubscribeCalleesVisibleForCallerSymbols()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_subscribe_callee_bundle");
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Hook", "--db", dbPath, "--json", "--lang", "csharp", "--exact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var callee = Assert.Single(json.GetProperty("callees").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Hook", callee.GetProperty("caller_name").GetString());
            Assert.Equal("Changed", callee.GetProperty("callee_name").GetString());
            Assert.Equal("event", callee.GetProperty("reference_kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_NonExactJsonOnReadOnlyLegacyDb_OmitsExactDegradedFields()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_nonexact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/session.py", "python", "def login(user, password):\n    return Run(user)\n");
            DropGraphExactFallbackIndexes(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Run", "--db", readOnlyUri, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.TryGetProperty("exact_index_available", out _));
            Assert.False(json.TryGetProperty("degraded_reason", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_WithJsonIncludesWorkspaceMetadataForCustomDbUnderCdidx()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_custom_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_custom_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "shared.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["App", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(projectRoot, json.GetProperty("project_root").GetString());
            Assert.Equal(expectedHead, json.GetProperty("git_head").GetString());
            Assert.False(json.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void RunInspect_WithJsonUsesProjectLocalCdidxPathForExplicitProjectDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_project_local_explicit");
        var staleRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_project_local_stale");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, staleRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["App", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(projectRoot, json.GetProperty("project_root").GetString());
            Assert.Equal(expectedHead, json.GetProperty("git_head").GetString());
            Assert.False(json.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(staleRoot);
        }
    }

    [Fact]
    public void RunInspect_WithJsonIncludesWorkspaceMetadataForExplicitExternalCodeIndexDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_codeindex_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_codeindex_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["App", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(projectRoot, json.GetProperty("project_root").GetString());
            Assert.Equal(expectedHead, json.GetProperty("git_head").GetString());
            Assert.False(json.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactOnReadOnlyLegacyDb_WithMissingSymbolFallbackIndex_WarnsAtBundleLevel()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_symbol_exact_warn");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/session.py",
                "python",
                "def Run(user):\n    return user\n\ndef login(user, password):\n    return Run(user)\n");
            ForceLegacyExactFallbackMode(dbPath);
            DropSymbolExactFallbackIndex(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Run", "--db", readOnlyUri, "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Exact Index          : DEGRADED", stdout);
            Assert.Contains("idx_symbols_name_nocase", stdout);
            Assert.Contains("WARN: --exact inspect bundle ran without all supporting indexes", stderr);
            Assert.Contains("idx_symbols_name_nocase", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactOnReadOnlyLegacyDb_WithMissingSymbolIndexAndGraphTable_StillWarnsAboutIndex()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_symbol_and_table_missing");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/session.py",
                "python",
                "def Run(user):\n    return user\n\ndef login(user, password):\n    return Run(user)\n");
            ForceLegacyExactFallbackMode(dbPath);
            DropSymbolExactFallbackIndex(dbPath);
            DropGraphTable(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Run", "--db", readOnlyUri, "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Graph Table          : MISSING", stdout);
            Assert.Contains("Exact Index          : DEGRADED", stdout);
            Assert.Contains("idx_symbols_name_nocase", stdout);
            Assert.Contains("WARN: --exact inspect bundle ran without all supporting indexes", stderr);
            Assert.Contains("idx_symbols_name_nocase", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactOnReadOnlyLegacyDb_UnsupportedGraphLanguage_DoesNotReportFalseDegradedSignal()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_toml_exact_ok");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "docs/guide.toml",
                "toml",
                "title = \"Heading\"\nrun = \"Run\"\n");
            ForceLegacyExactFallbackMode(dbPath);
            DropGraphExactFallbackIndexes(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Heading", "--db", readOnlyUri, "--exact", "--lang", "toml", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("graph_supported").GetBoolean());
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("degraded_reason", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactOnReadOnlyLegacyDb_PathOnlyUnsupportedSlice_DoesNotReportFalseDegradedSignal()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_path_only_exact_ok");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "docs/guide.toml",
                "toml",
                "title = \"Heading\"\nrun = \"Run\"\n");
            ForceLegacyExactFallbackMode(dbPath);
            DropGraphExactFallbackIndexes(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Run", "--db", readOnlyUri, "--exact", "--path", "docs/", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("exact_index_available").GetBoolean());
            Assert.False(json.TryGetProperty("degraded_reason", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_PrefersExactDefinitionFileWhenSubstringDefinitionsOverlap()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_exact_anchor");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Services/ILoggerService.cs",
                "csharp",
                """
                public interface ILoggerService
                {
                    void Log(string message);
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Services/LoggerService.cs",
                "csharp",
                """
                public class LoggerService : ILoggerService
                {
                    public void Log(string message) { }
                    public void Execute() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["loggerservice", "--db", dbPath, "--lang", "csharp", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("File : src/Services/LoggerService.cs", stdout);
            Assert.Contains("class      LoggerService", stdout);
            Assert.DoesNotContain("File : src/Services/ILoggerService.cs", stdout);
            Assert.DoesNotContain("interface  ILoggerService", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_ExactZeroHumanOutput_PrintsExactZeroHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_exact_zero");
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

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["HandleRe", "--db", dbPath, "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("--exact found 0 matches, but substring matching would return 2", stderr);
            Assert.Contains("`HandleRequest`", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunInspect_BlankQueryReturnsDistinctUsageError()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
            ["   "],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: inspect query cannot be empty or whitespace-only", stderr);
        Assert.DoesNotContain("inspect requires a symbol query argument", stderr);
        Assert.Contains("empty or whitespace-only arguments", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine("inspect")}", stderr);
    }

    [Fact]
    public void RunInspect_BareVerbatimQueryWithExactReturnsUsageError()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_inspect_bare_verbatim_exact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["@", "--db", dbPath, "--exact", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("bare verbatim prefixes", stderr);
            Assert.Equal(string.Empty, stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_WithJson_JavaModuleInfoWithMultilineDirectivesIncludesImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_outline_java_module_multiline");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "main", "java"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "main", "java", "module-info.java"),
                """
                module com.example.app {
                    requires /*comment*/ java.base;
                    exports com.example.internal
                        to com.example.plugin,
                           com.example.tools;
                    opens com.example.model
                        to com.example.viewer,
                           com.example.editor;
                    uses com.example.spi.MyService;
                    provides com.example.spi.MyService
                        with com.example.impl.DefaultService,
                             com.example.impl.BackupService;
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/main/java/module-info.java", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(6, symbols.Count);
            Assert.Contains(symbols, symbol => symbol.GetProperty("kind").GetString() == "import"
                && symbol.GetProperty("name").GetString() == "java.base");
            Assert.Contains(symbols, symbol => symbol.GetProperty("kind").GetString() == "import"
                && symbol.GetProperty("name").GetString() == "com.example.internal");
            Assert.Contains(symbols, symbol => symbol.GetProperty("kind").GetString() == "import"
                && symbol.GetProperty("name").GetString() == "com.example.model");
            Assert.Equal(2, symbols.Count(symbol => symbol.GetProperty("kind").GetString() == "import"
                && symbol.GetProperty("name").GetString() == "com.example.spi.MyService"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOutline_WithJson_JavaModuleInfoWithAllmanBraceLineDirectiveIncludesImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_outline_java_module_allman_brace_line");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "module-info.java"),
                """
                module com.example.app
                { requires java.base;
                  exports com.example.api;
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["module-info.java", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(3, symbols.Count);
            Assert.Contains(symbols, symbol => symbol.GetProperty("kind").GetString() == "import"
                && symbol.GetProperty("name").GetString() == "java.base");
            Assert.Contains(symbols, symbol => symbol.GetProperty("kind").GetString() == "import"
                && symbol.GetProperty("name").GetString() == "com.example.api");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
