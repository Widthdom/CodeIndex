using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class JsonEnvelopeWrapperIssue4585Tests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void BoundedProjection_AllRequestedCommandFamiliesExposeSharedMetadata_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_response_families_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                """
                namespace Demo;
                public sealed class Target
                {
                    public void Call() => Helper();
                    public void Helper() { }
                }
                public sealed class Consumer
                {
                    public void Use() => new Target().Call();
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var cases = new (string Command, string[] Args)[]
            {
                ("definition", ["definition", "Target", "--db", dbPath, "--format", "compact", "--limit", "1", "--max-json-bytes", "4096"]),
                ("find", ["find", "Target", "--db", dbPath, "--path", "src/*.cs", "--format", "compact", "--limit", "1", "--max-json-bytes", "4096"]),
                ("references", ["references", "Target", "--db", dbPath, "--format", "compact", "--limit", "1", "--max-json-bytes", "4096"]),
                ("callers", ["callers", "Helper", "--db", dbPath, "--format", "compact", "--limit", "1", "--max-json-bytes", "4096"]),
                ("callees", ["callees", "Call", "--db", dbPath, "--format", "compact", "--limit", "1", "--max-json-bytes", "4096"]),
                ("status", ["status", "--db", dbPath, "--fields", "files,symbols", "--max-json-bytes", "4096"]),
                ("hotspots", ["hotspots", "--db", dbPath, "--compact", "--limit", "1", "--max-json-bytes", "4096"]),
                ("impact", ["impact", "Helper", "--db", dbPath, "--compact", "--limit", "1", "--max-json-bytes", "4096"]),
                ("map", ["map", "--db", dbPath, "--sections", "summary", "--fields", "file_count,total_symbols", "--max-json-bytes", "4096"]),
            };

            foreach (var testCase in cases)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(testCase.Args, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.True(Encoding.UTF8.GetByteCount(stdout) <= 4096, testCase.Command);
                using var document = JsonDocument.Parse(stdout);
                var metadata = document.RootElement.GetProperty("metadata");
                Assert.Equal(testCase.Command, metadata.GetProperty("command").GetString());
                Assert.True(metadata.TryGetProperty("returned_count", out var returned));
                Assert.True(metadata.TryGetProperty("total_count", out var total));
                Assert.True(metadata.TryGetProperty("omitted_count", out var omitted));
                Assert.True(metadata.TryGetProperty("total_count_authoritative", out _));
                Assert.True(metadata.TryGetProperty("next_cursor", out _));
                Assert.True(total.GetInt32() >= returned.GetInt32());
                Assert.Equal(Math.Max(0, total.GetInt32() - returned.GetInt32()), omitted.GetInt32());
                Assert.Equal(returned.GetInt32(), document.RootElement.GetProperty("results").GetArrayLength());
                if (testCase.Command is "definition" or "find" or "references" or "callers" or "callees")
                {
                    Assert.True(document.RootElement.TryGetProperty("format", out var format), $"{testCase.Command}: {stdout}");
                    Assert.Equal("compact", format.GetString());
                    Assert.Equal(returned.GetInt32(), document.RootElement.GetProperty("count").GetInt32());
                    if (returned.GetInt32() > 0)
                        Assert.True(document.RootElement.GetProperty("results")[0].TryGetProperty("file", out _));
                }
            }

            var zeroCalleeCases = new[]
            {
                new[]
                {
                    "callees", "DefinitelyNoSuchSymbol", "--exact-name", "--json", "--compact",
                    "--limit", "1", "--max-json-bytes", "8192", "--db", dbPath,
                },
                new[]
                {
                    "callees", "DefinitelyNoSuchSymbol", "--exact-name", "--json", "--compact",
                    "--fields", "path,line", "--limit", "1", "--max-json-bytes", "8192", "--db", dbPath,
                },
            };
            foreach (var args in zeroCalleeCases)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                var root = document.RootElement;
                Assert.Equal("compact", root.GetProperty("format").GetString());
                Assert.Equal(0, root.GetProperty("count").GetInt32());
                Assert.Empty(root.GetProperty("results").EnumerateArray());
                Assert.Equal(0, root.GetProperty("metadata").GetProperty("result_count").GetInt32());
                Assert.Equal(0, root.GetProperty("metadata").GetProperty("total_count").GetInt32());
            }

            var projectedGraphCases = new (string Command, string Query, string Fields)[]
            {
                ("references", "Helper", "path,symbol_name,reference_kind,line"),
                ("callers", "Helper", "path,caller_name,callee_name,first_line"),
                ("callees", "Call", "path,caller_name,callee_name,first_line"),
            };
            foreach (var testCase in projectedGraphCases)
            {
                var nonEmptyArgs = new[]
                {
                    testCase.Command, testCase.Query, "--exact-name", "--json",
                    "--lang", "csharp", "--fields", testCase.Fields, "--db", dbPath,
                };
                var (nonEmptyExitCode, nonEmptyStdout, nonEmptyStderr) = CaptureConsole(() =>
                    ProgramRunner.Run(nonEmptyArgs, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, nonEmptyExitCode);
                Assert.Equal(string.Empty, nonEmptyStderr);
                using var nonEmptyDocument = JsonDocument.Parse(nonEmptyStdout);
                var nonEmptyRoot = nonEmptyDocument.RootElement;
                Assert.True(
                    nonEmptyRoot.GetProperty("results").GetArrayLength() > 0,
                    $"{testCase.Command}: {nonEmptyStdout}");
                Assert.Equal(
                    nonEmptyRoot.GetProperty("results").GetArrayLength(),
                    nonEmptyRoot.GetProperty("metadata").GetProperty("returned_count").GetInt32());
                Assert.DoesNotContain(
                    nonEmptyRoot.GetProperty("results").EnumerateArray(),
                    result => result.EnumerateObject().Count() == 0);

                var emptyArgs = new[]
                {
                    testCase.Command, "DefinitelyNoSuchSymbol", "--exact-name", "--json",
                    "--fields", testCase.Fields, "--db", dbPath,
                };
                var (emptyExitCode, emptyStdout, emptyStderr) = CaptureConsole(() =>
                    ProgramRunner.Run(emptyArgs, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, emptyExitCode);
                Assert.Equal(string.Empty, emptyStderr);
                using var emptyDocument = JsonDocument.Parse(emptyStdout);
                var emptyRoot = emptyDocument.RootElement;
                Assert.Empty(emptyRoot.GetProperty("results").EnumerateArray());
                Assert.Equal(0, emptyRoot.GetProperty("metadata").GetProperty("result_count").GetInt32());
                Assert.Equal(0, emptyRoot.GetProperty("metadata").GetProperty("returned_count").GetInt32());
                Assert.Equal(0, emptyRoot.GetProperty("metadata").GetProperty("total_count").GetInt32());
            }

            var strictArgs = new[]
            {
                "callees", "DefinitelyNoSuchSymbol", "--exact-name", "--strict-not-found", "--json",
                "--fields", "path,caller_name,callee_name,first_line", "--db", dbPath,
            };
            var (strictExitCode, strictStdout, strictStderr) = CaptureConsole(() =>
                ProgramRunner.Run(strictArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.NotFound, strictExitCode);
            Assert.Equal(string.Empty, strictStderr);
            using var strictDocument = JsonDocument.Parse(strictStdout);
            Assert.Empty(strictDocument.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal(0, strictDocument.RootElement.GetProperty("metadata").GetProperty("total_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void BoundedProjection_CursorIsStableAndByteBudgetTrimsProjectedRows_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_response_cursor_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var content = string.Join('\n', Enumerable.Range(1, 40).Select(index => $"Alpha marker {index}"));
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Many.txt", "text", content);

            var firstArgs = new[]
            {
                "find", "Alpha", "--db", dbPath, "--path", "src/*.txt", "--fields", "path,line,column",
                "--limit", "1", "--max-json-bytes", "4096",
            };
            var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() => ProgramRunner.Run(firstArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            using var firstDocument = JsonDocument.Parse(firstStdout);
            var firstMetadata = firstDocument.RootElement.GetProperty("metadata");
            Assert.True(firstMetadata.GetProperty("total_count_authoritative").GetBoolean());
            Assert.Equal(40, firstMetadata.GetProperty("total_count").GetInt32());
            var cursor = Assert.IsType<string>(firstMetadata.GetProperty("next_cursor").GetString());
            Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);
            var firstLine = firstDocument.RootElement.GetProperty("results")[0].GetProperty("line").GetInt32();

            var secondArgs = firstArgs.Concat(["--cursor", cursor!]).ToArray();
            var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() => ProgramRunner.Run(secondArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, secondExitCode);
            Assert.Equal(string.Empty, secondStderr);
            using var secondDocument = JsonDocument.Parse(secondStdout);
            var secondMetadata = secondDocument.RootElement.GetProperty("metadata");
            Assert.Equal(1, secondMetadata.GetProperty("cursor_offset").GetInt32());
            Assert.Equal(40, secondMetadata.GetProperty("total_count").GetInt32());
            Assert.NotEqual(firstLine, secondDocument.RootElement.GetProperty("results")[0].GetProperty("line").GetInt32());

            var boundedArgs = new[]
            {
                "find", "Alpha", "--db", dbPath, "--path", "src/*.txt", "--fields", "path,line,column",
                "--limit", "40", "--max-json-bytes", "1200",
            };
            var (boundedExitCode, boundedStdout, boundedStderr) = CaptureConsole(() => ProgramRunner.Run(boundedArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, boundedExitCode);
            Assert.Equal(string.Empty, boundedStderr);
            Assert.True(Encoding.UTF8.GetByteCount(boundedStdout) <= 1200);
            using var boundedDocument = JsonDocument.Parse(boundedStdout);
            var boundedMetadata = boundedDocument.RootElement.GetProperty("metadata");
            Assert.True(boundedMetadata.GetProperty("byte_limit_reached").GetBoolean());
            Assert.InRange(boundedMetadata.GetProperty("returned_count").GetInt32(), 1, 39);
            Assert.Equal(40, boundedMetadata.GetProperty("total_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Definition_ProjectedMetadataDoesNotMaterializeExplicitBody_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_definition_body_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var methods = string.Join('\n', Enumerable.Range(1, 100).Select(index => $"public void Method{index}() {{ }}"));
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Large.cs", "csharp", $"public sealed class Large {{\n{methods}\n}}");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["definition", "Large", "--db", dbPath, "--body", "--fields", "path,line", "--limit", "1", "--max-json-bytes", "1200"],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(Encoding.UTF8.GetByteCount(stdout) <= 1200);
            using var document = JsonDocument.Parse(stdout);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal("src/Large.cs", result.GetProperty("path").GetString());
            Assert.True(result.TryGetProperty("line", out _));
            Assert.False(result.TryGetProperty("body", out _));
            Assert.False(result.TryGetProperty("body_excerpt", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Definition_MissingBoundedJsonPreservesStructuredError_Issue4744()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_definition_missing_4744");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "public sealed class App { }");

            var cases = new[]
            {
                new[] { "definition", "MissingDefinitionIssue4744", "--db", dbPath, "--json-envelope", "--max-json-bytes", "800" },
                new[] { "definition", "MissingDefinitionIssue4744", "--db", dbPath, "--fields", "file,line" },
            };

            foreach (var args in cases)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() =>
                    ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.NotFound, exitCode);
                Assert.Equal(string.Empty, stderr);
                if (args.Contains("--max-json-bytes", StringComparer.Ordinal))
                    Assert.True(Encoding.UTF8.GetByteCount(stdout) <= 800);

                using var document = JsonDocument.Parse(stdout);
                var metadata = document.RootElement.GetProperty("metadata");
                var error = metadata.GetProperty("error");
                Assert.Equal(CommandExitCodes.NotFound, metadata.GetProperty("exit_code").GetInt32());
                Assert.Equal(0, metadata.GetProperty("total_count").GetInt32());
                Assert.Equal(0, metadata.GetProperty("returned_count").GetInt32());
                Assert.Equal(CommandErrorCodes.QueryNotFound, error.GetProperty("error_code").GetString());
                Assert.Equal("not_found", error.GetProperty("category").GetString());
                Assert.Empty(document.RootElement.GetProperty("results").EnumerateArray());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Impact_CollapsedPartialDefinitionsDoNotEmitUnusableCursor_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_impact_partial_cursor_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/One.cs", "csharp", "public partial class SharedTarget { public void One() { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Two.cs", "csharp", "public partial class SharedTarget { public void Two() { } }");
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["impact", "SharedTarget", "--db", dbPath, "--compact", "--limit", "1", "--max-json-bytes", "4096"],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var metadata = document.RootElement.GetProperty("metadata");
            Assert.Equal("definitions", metadata.GetProperty("primary_collection").GetString());
            Assert.Equal(1, metadata.GetProperty("total_count").GetInt32());
            Assert.True(metadata.GetProperty("total_count_authoritative").GetBoolean());
            Assert.False(metadata.GetProperty("has_more").GetBoolean());
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("next_cursor").ValueKind);
            Assert.Equal(2, metadata.GetProperty("response_context").GetProperty("definition_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void AliasesAndBatch_UseTheSharedBoundedResponseContract_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_alias_batch_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "public sealed class Target { public void Run() { } } public sealed class Consumer { public void Use() => new Target().Run(); }");
            MarkGraphAndFoldReady(dbPath);

            var (refsExitCode, refsStdout, refsStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["refs", "Target", "--db", dbPath, "--fields", "file,line", "--limit", "1", "--max-json-bytes", "4096"],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, refsExitCode);
            Assert.Equal(string.Empty, refsStderr);
            Assert.True(Encoding.UTF8.GetByteCount(refsStdout) <= 4096);
            using (var refsDocument = JsonDocument.Parse(refsStdout))
                Assert.Equal("references", refsDocument.RootElement.GetProperty("metadata").GetProperty("command").GetString());

            var (statsExitCode, statsStdout, statsStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["stats", "--db", dbPath, "--compact", "--max-json-bytes", "4096"],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, statsExitCode);
            Assert.Equal(string.Empty, statsStderr);
            Assert.True(Encoding.UTF8.GetByteCount(statsStdout) <= 4096);
            using (var statsDocument = JsonDocument.Parse(statsStdout))
                Assert.Equal("status", statsDocument.RootElement.GetProperty("metadata").GetProperty("command").GetString());

            using var input = new StringReader("[\"definition\",\"Target\",\"--fields\",\"file,line\",\"--limit\",\"1\",\"--max-json-bytes\",\"4096\"]\n");
            using var capture = ConsoleCapture.StartWithInput(input, captureOut: true, captureError: true);
            var batchExitCode = ProgramRunner.Run(["batch", "--db", dbPath], _jsonOptions, "1.0.0-test");
            var batchStdout = capture.Out!.ToString()!;
            var batchStderr = capture.Error!.ToString()!;

            Assert.Equal(CommandExitCodes.Success, batchExitCode);
            Assert.Equal(string.Empty, batchStderr);
            using var batchDocument = JsonDocument.Parse(batchStdout);
            Assert.Equal("definition", batchDocument.RootElement.GetProperty("metadata").GetProperty("command").GetString());
            Assert.Equal(1, batchDocument.RootElement.GetProperty("results").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Definition_BodyContentAndAllProjectionsPreserveExplicitBody_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_definition_body_positive_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Body.cs",
                "csharp",
                "public sealed class BodyTarget\n{\n"
                + string.Join('\n', Enumerable.Range(1, 40).Select(index => $"    public void Run{index}() {{ var marker{index} = {index}; }}"))
                + "\n}");

            foreach (var fields in new[] { "body", "body_content", "all" })
            {
                foreach (var compact in new[] { false, true })
                {
                    var args = new List<string>
                    {
                        "definition", "BodyTarget", "--db", dbPath, "--body", "--fields", fields,
                        "--limit", "1", "--max-json-bytes", "8192",
                    };
                    if (compact)
                        args.Add("--compact");
                    var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                        [.. args],
                        _jsonOptions,
                        "1.0.0-test"));

                    Assert.Equal(CommandExitCodes.Success, exitCode);
                    Assert.Equal(string.Empty, stderr);
                    using var document = JsonDocument.Parse(stdout);
                    var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
                    Assert.Contains("marker", result.GetProperty("body_content").GetString(), StringComparison.Ordinal);
                    Assert.True(result.GetProperty("body_content_truncated").GetBoolean());
                    Assert.True(result.TryGetProperty("body_content_start_line", out _));
                    Assert.True(result.TryGetProperty("body_content_end_line", out _));
                    Assert.True(result.TryGetProperty("body_content_next_start_line", out _));
                    Assert.True(result.TryGetProperty("body_requested_start_line", out _));
                    Assert.True(result.TryGetProperty("body_requested_end_line", out _));
                    Assert.True(result.TryGetProperty("body_effective_start_line", out _));
                    Assert.True(result.TryGetProperty("body_effective_end_line", out _));
                    Assert.Contains(
                        result.GetProperty("body_content_truncation_reasons").EnumerateArray(),
                        reason => reason.GetString() == "body_line_cap");
                    Assert.True(result.TryGetProperty("body_content_recovery", out _));
                    Assert.True(result.GetProperty("content_omitted").GetBoolean());
                    Assert.Equal("body_content_field", result.GetProperty("content_omitted_reason").GetString());
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Impact_InactiveProjectedCollectionHasAuthoritativeZeroTotal_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_impact_collection_total_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Graph.cs",
                "csharp",
                "public sealed class Target { public void Run() { } } public sealed class Consumer { public void Use() => new Target().Run(); }");
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["impact", "Run", "--db", dbPath, "--fields", "file_impacts.source_path", "--limit", "1", "--max-json-bytes", "4096"],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var metadata = document.RootElement.GetProperty("metadata");
            Assert.Equal("file_impacts", metadata.GetProperty("primary_collection").GetString());
            Assert.Equal(0, metadata.GetProperty("total_count").GetInt32());
            Assert.True(metadata.GetProperty("total_count_authoritative").GetBoolean());
            Assert.False(metadata.GetProperty("has_more").GetBoolean());
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("next_cursor").ValueKind);
            Assert.Empty(document.RootElement.GetProperty("results").EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Map_SelectedSectionPagesRowsAndScalarProjectionOmitsSections_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_map_section_page_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Small.cs", "csharp", "public class Small { }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Medium.cs", "csharp", "public class Medium {\npublic void Run() { }\n}");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Large.cs", "csharp", "public class Large {\npublic void One() { }\npublic void Two() { }\n}");

            var firstArgs = new[]
            {
                "map", "--db", dbPath, "--fields", "top_files.path", "--limit", "1", "--max-json-bytes", "4096",
            };
            var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() => ProgramRunner.Run(firstArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            using var firstDocument = JsonDocument.Parse(firstStdout);
            var firstMetadata = firstDocument.RootElement.GetProperty("metadata");
            Assert.Equal(3, firstMetadata.GetProperty("total_count").GetInt32());
            Assert.Equal("top_files", firstMetadata.GetProperty("primary_collection").GetString());
            var firstPath = firstDocument.RootElement.GetProperty("results")[0].GetProperty("path").GetString();
            var cursor = Assert.IsType<string>(firstMetadata.GetProperty("next_cursor").GetString());

            var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() => ProgramRunner.Run(
                firstArgs.Concat(["--cursor", cursor]).ToArray(),
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, secondExitCode);
            Assert.Equal(string.Empty, secondStderr);
            using var secondDocument = JsonDocument.Parse(secondStdout);
            Assert.Equal(3, secondDocument.RootElement.GetProperty("metadata").GetProperty("total_count").GetInt32());
            Assert.NotEqual(firstPath, secondDocument.RootElement.GetProperty("results")[0].GetProperty("path").GetString());

            var lastCursor = Assert.IsType<string>(
                secondDocument.RootElement.GetProperty("metadata").GetProperty("next_cursor").GetString());
            var (lastExitCode, lastStdout, lastStderr) = CaptureConsole(() => ProgramRunner.Run(
                firstArgs.Concat(["--cursor", lastCursor]).ToArray(),
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, lastExitCode);
            Assert.Equal(string.Empty, lastStderr);
            using var lastDocument = JsonDocument.Parse(lastStdout);
            Assert.Equal(2, lastDocument.RootElement.GetProperty("metadata").GetProperty("cursor_offset").GetInt32());
            Assert.False(lastDocument.RootElement.GetProperty("metadata").GetProperty("has_more").GetBoolean());
            Assert.Equal(1, lastDocument.RootElement.GetProperty("results").GetArrayLength());

            var (scalarExitCode, scalarStdout, scalarStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["map", "--db", dbPath, "--fields", "file_count,total_lines", "--max-json-bytes", "4096"],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, scalarExitCode);
            Assert.Equal(string.Empty, scalarStderr);
            using var scalarDocument = JsonDocument.Parse(scalarStdout);
            var scalarResult = Assert.Single(scalarDocument.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal(2, scalarResult.EnumerateObject().Count());
            Assert.Equal(3, scalarResult.GetProperty("file_count").GetInt32());

            var (compactExitCode, compactStdout, compactStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["map", "--db", dbPath, "--compact", "--limit", "1", "--max-json-bytes", "16384"],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, compactExitCode);
            Assert.Equal(string.Empty, compactStderr);
            Assert.True(Encoding.UTF8.GetByteCount(compactStdout) <= 16384);
            using var compactDocument = JsonDocument.Parse(compactStdout);
            Assert.Equal("map", compactDocument.RootElement.GetProperty("metadata").GetProperty("command").GetString());
            Assert.Equal("compact", compactDocument.RootElement.GetProperty("metadata").GetProperty("format").GetString());
            Assert.NotEmpty(compactDocument.RootElement.GetProperty("languages").EnumerateArray());
            Assert.NotEmpty(compactDocument.RootElement.GetProperty("modules").EnumerateArray());
            Assert.NotEmpty(compactDocument.RootElement.GetProperty("top_files").EnumerateArray());
            Assert.True(compactDocument.RootElement.TryGetProperty("truncation", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void BoundedParserFailure_EmitsResponseBudgetError_Issues4585_4909()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_parser_cap_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var content = string.Join('\n', Enumerable.Range(1, 2_000).Select(index => $"marker {index}"));
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Many.txt", "text", content);

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["find", "marker", "--db", dbPath, "--path", "src/*.txt", "--fields", "path", "--limit", "10000", "--max-json-bytes", "128"],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var error = document.RootElement;
            Assert.Equal("E028_RESPONSE_BUDGET_TOO_SMALL", error.GetProperty("error_code").GetString());
            Assert.Equal("response_budget", error.GetProperty("category").GetString());
            Assert.Equal("find", error.GetProperty("command").GetString());
            Assert.Equal(128, error.GetProperty("requested_bytes").GetInt64());
            Assert.Equal(128, error.GetProperty("effective_bytes").GetInt64());
            Assert.True(error.GetProperty("minimum_required_bytes_known").GetBoolean());
            Assert.True(error.GetProperty("minimum_required_bytes_uncertain").GetBoolean());
            Assert.True(
                error.GetProperty("retry").GetProperty("recommended_bytes").GetInt64()
                > error.GetProperty("minimum_required_bytes").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Impact_DefinitionCursorPagesLogicalDefinitionsWithoutChangingCollection_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_impact_definition_page_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/A.cs", "csharp", "public class A { public void SharedTarget() { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/B.cs", "csharp", "public class B { public void SharedTarget() { } }");
            MarkGraphAndFoldReady(dbPath);
            var firstArgs = new[]
            {
                "impact", "SharedTarget", "--db", dbPath, "--fields", "definitions.path",
                "--limit", "1", "--max-json-bytes", "4096",
            };

            var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() => ProgramRunner.Run(firstArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            using var firstDocument = JsonDocument.Parse(firstStdout);
            var firstMetadata = firstDocument.RootElement.GetProperty("metadata");
            Assert.Equal("definitions", firstMetadata.GetProperty("primary_collection").GetString());
            Assert.Equal(2, firstMetadata.GetProperty("total_count").GetInt32());
            var firstPath = firstDocument.RootElement.GetProperty("results")[0].GetProperty("path").GetString();
            var cursor = Assert.IsType<string>(firstMetadata.GetProperty("next_cursor").GetString());

            var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() => ProgramRunner.Run(
                firstArgs.Concat(["--cursor", cursor]).ToArray(),
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, secondExitCode);
            Assert.Equal(string.Empty, secondStderr);
            using var secondDocument = JsonDocument.Parse(secondStdout);
            var secondMetadata = secondDocument.RootElement.GetProperty("metadata");
            Assert.Equal("definitions", secondMetadata.GetProperty("primary_collection").GetString());
            Assert.Equal(2, secondMetadata.GetProperty("total_count").GetInt32());
            Assert.False(secondMetadata.GetProperty("has_more").GetBoolean());
            Assert.NotEqual(firstPath, secondDocument.RootElement.GetProperty("results")[0].GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Map_RejectsCollectionProjectionExcludedByLegacyShapeControls_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_map_projection_conflict_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "public class App { }");

            var (summaryExitCode, summaryStdout, summaryStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["map", "--db", dbPath, "--fields", "top_files.path", "--summary-only", "--limit", "1"],
                _jsonOptions,
                "1.0.0-test"));
            Assert.Equal(CommandExitCodes.UsageError, summaryExitCode);
            Assert.Equal(string.Empty, summaryStdout);
            Assert.Contains("cannot be combined with --summary-only", summaryStderr, StringComparison.Ordinal);

            var (sectionsExitCode, sectionsStdout, sectionsStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["map", "--db", dbPath, "--fields", "top_files.path", "--sections", "languages", "--limit", "1"],
                _jsonOptions,
                "1.0.0-test"));
            Assert.Equal(CommandExitCodes.UsageError, sectionsExitCode);
            Assert.Equal(string.Empty, sectionsStdout);
            Assert.Contains("requires --sections hotspots", sectionsStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Diagnostics_AreMetadataControlRecordsInsteadOfProjectedRows_Issue4585()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_diagnostics_4585");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "public class App { }");

            foreach (var diagnosticFlag in new[] { "--profile", "--verbose" })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["status", "--db", dbPath, "--fields", "files", diagnosticFlag, "--max-json-bytes", "8192"],
                    _jsonOptions,
                    "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                var metadata = document.RootElement.GetProperty("metadata");
                Assert.Equal(1, metadata.GetProperty("result_count").GetInt32());
                Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
                var control = Assert.Single(metadata.GetProperty("stream_control_records").EnumerateArray());
                Assert.True(control.TryGetProperty(diagnosticFlag == "--profile" ? "profile" : "_debug", out _));
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void BoundedResponse_BudgetPreflightIsParseableForEmptyAndEscapedNonEmptyRows_Issue4909()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_response_budget_4909");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/日本語/Quote\"Target.cs",
                "csharp",
                """
                namespace Demo;
                public sealed class Target
                {
                    public void Run() { }
                }
                """);

            var zeroArgs = new[]
            {
                "definition", "Target", "--db", dbPath, "--format", "compact",
                "--max-json-bytes", "0",
            };
            var (zeroExitCode, zeroStdout, zeroStderr) = CaptureConsole(() => ProgramRunner.Run(
                zeroArgs,
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, zeroExitCode);
            Assert.Equal(string.Empty, zeroStderr);
            using var zeroDocument = JsonDocument.Parse(zeroStdout);
            var zeroError = zeroDocument.RootElement;
            Assert.Equal("E028_RESPONSE_BUDGET_TOO_SMALL", zeroError.GetProperty("error_code").GetString());
            Assert.Equal("response_budget", zeroError.GetProperty("category").GetString());
            Assert.Equal("definition", zeroError.GetProperty("command").GetString());
            Assert.Equal(0, zeroError.GetProperty("requested_bytes").GetInt64());
            Assert.Equal(JsonValueKind.Null, zeroError.GetProperty("effective_bytes").ValueKind);
            Assert.False(zeroError.GetProperty("minimum_required_bytes_known").GetBoolean());
            Assert.Equal(
                "normal_payload_not_materialized",
                zeroError.GetProperty("minimum_required_bytes_unavailable_reason").GetString());

            var (duplicateExitCode, duplicateStdout, duplicateStderr) = CaptureConsole(() => ProgramRunner.Run(
                [
                    "definition", "Target", "--db", dbPath, "--format", "compact",
                    "--max-json-bytes", "10", "--max-json-bytes", "0",
                ],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, duplicateExitCode);
            Assert.Equal(string.Empty, duplicateStderr);
            using var duplicateDocument = JsonDocument.Parse(duplicateStdout);
            var duplicateError = duplicateDocument.RootElement;
            Assert.Equal(
                CommandErrorCodes.ResponseBudgetTooSmall,
                duplicateError.GetProperty("error_code").GetString());
            Assert.Equal(0, duplicateError.GetProperty("requested_bytes").GetInt64());

            foreach (var orderedArgs in new[]
                     {
                         new[]
                         {
                             "definition", "Target", "--db", dbPath, "--format", "compact",
                             "--limit", "0", "--max-json-bytes", "0",
                         },
                         new[]
                         {
                             "definition", "Target", "--db", dbPath, "--format", "compact",
                             "--max-json-bytes", "0", "--limit", "0",
                         },
                     })
            {
                var (orderedExitCode, orderedStdout, orderedStderr) = CaptureConsole(() =>
                    ProgramRunner.Run(
                        orderedArgs,
                        _jsonOptions,
                        "1.0.0-test"));

                Assert.Equal(CommandExitCodes.UsageError, orderedExitCode);
                Assert.Equal(string.Empty, orderedStderr);
                using var orderedDocument = JsonDocument.Parse(orderedStdout);
                Assert.Equal(
                    CommandErrorCodes.ResponseBudgetTooSmall,
                    orderedDocument.RootElement.GetProperty("error_code").GetString());
                Assert.Equal(0, orderedDocument.RootElement.GetProperty("requested_bytes").GetInt64());
            }

            var tinyArgs = new[]
            {
                "definition", "Target", "--db", dbPath, "--format", "compact",
                "--max-json-bytes", "1",
            };
            var (tinyExitCode, tinyStdout, tinyStderr) = CaptureConsole(() => ProgramRunner.Run(
                tinyArgs,
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, tinyExitCode);
            Assert.Equal(string.Empty, tinyStderr);
            using var tinyDocument = JsonDocument.Parse(tinyStdout);
            var tinyError = tinyDocument.RootElement;
            Assert.Equal("E028_RESPONSE_BUDGET_TOO_SMALL", tinyError.GetProperty("error_code").GetString());
            Assert.Equal("definition", tinyError.GetProperty("command").GetString());
            Assert.StartsWith(
                "cdidx definition ",
                tinyError.GetProperty("usage").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(1, tinyError.GetProperty("requested_bytes").GetInt64());
            Assert.Equal(1, tinyError.GetProperty("effective_bytes").GetInt64());
            Assert.True(tinyError.GetProperty("minimum_required_bytes_known").GetBoolean());
            Assert.True(tinyError.GetProperty("minimum_required_bytes_uncertain").GetBoolean());
            Assert.Equal(
                "runtime_metadata_or_embedded_budget_varies_between_invocations",
                tinyError.GetProperty("minimum_required_bytes_uncertainty_reason").GetString());
            var minimumRequiredBytes = tinyError.GetProperty("minimum_required_bytes").GetInt64();
            var recommendedBytes = tinyError.GetProperty("retry").GetProperty("recommended_bytes").GetInt64();
            Assert.True(recommendedBytes > minimumRequiredBytes);

            var retryArgs = tinyArgs.ToArray();
            retryArgs[^1] = recommendedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var (retryExitCode, retryStdout, retryStderr) = CaptureConsole(() => ProgramRunner.Run(
                retryArgs,
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, retryExitCode);
            Assert.Equal(string.Empty, retryStderr);
            Assert.True(Encoding.UTF8.GetByteCount(retryStdout) <= recommendedBytes);
            using var retryDocument = JsonDocument.Parse(retryStdout);
            var retryResult = Assert.Single(retryDocument.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal("src/日本語/Quote\"Target.cs", retryResult.GetProperty("file").GetString());

            var (emptyExitCode, emptyStdout, emptyStderr) = CaptureConsole(() => ProgramRunner.Run(
                [
                    "definition", "MissingSymbol", "--db", dbPath, "--format", "compact",
                    "--max-json-bytes", "1",
                ],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, emptyExitCode);
            Assert.Equal(string.Empty, emptyStderr);
            using var emptyDocument = JsonDocument.Parse(emptyStdout);
            var emptyError = emptyDocument.RootElement;
            Assert.Equal("E028_RESPONSE_BUDGET_TOO_SMALL", emptyError.GetProperty("error_code").GetString());
            Assert.Contains(
                "complete empty bounded response envelope",
                emptyError.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.True(emptyError.GetProperty("minimum_required_bytes_known").GetBoolean());

            var findArgs = new[]
            {
                "find", "Target", "--db", dbPath, "--json", "--max-json-bytes", "1",
            };
            var (findExitCode, findStdout, findStderr) = CaptureConsole(() => ProgramRunner.Run(
                findArgs,
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, findExitCode);
            Assert.Equal(string.Empty, findStderr);
            using var findDocument = JsonDocument.Parse(findStdout);
            var findError = findDocument.RootElement;
            Assert.Equal(
                CommandErrorCodes.ResponseBudgetTooSmall,
                findError.GetProperty("error_code").GetString());
            Assert.Contains(
                "complete bounded error envelope",
                findError.GetProperty("message").GetString(),
                StringComparison.Ordinal);

            var findRetryArgs = findArgs.ToArray();
            findRetryArgs[^1] = findError
                .GetProperty("retry")
                .GetProperty("recommended_bytes")
                .GetInt64()
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            var (findRetryExitCode, findRetryStdout, findRetryStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    findRetryArgs,
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, findRetryExitCode);
            Assert.Equal(string.Empty, findRetryStderr);
            using var findRetryDocument = JsonDocument.Parse(findRetryStdout);
            Assert.Equal(
                CommandErrorCodes.UsageError,
                findRetryDocument.RootElement
                    .GetProperty("metadata")
                    .GetProperty("error")
                    .GetProperty("error_code")
                    .GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
        => ConsoleCapture.Capture(action);

    private static void MarkGraphAndFoldReady(string dbPath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkFoldReady();
        writer.MarkCSharpSymbolNameContractReady();
        writer.MarkHotspotFamilyReady("csharp", "test-fixture-csharp-family");
    }
}
