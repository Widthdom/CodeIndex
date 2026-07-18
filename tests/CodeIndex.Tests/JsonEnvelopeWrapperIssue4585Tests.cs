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
            Assert.StartsWith("response:v1:1:", cursor, StringComparison.Ordinal);
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
                "public sealed class BodyTarget { public void Run() { var marker = 42; } }");

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
                ["impact", "Run", "--db", dbPath, "--fields", "file_impacts.path", "--limit", "1", "--max-json-bytes", "4096"],
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

            var cursorFingerprint = cursor[cursor.LastIndexOf(':')..];
            var (lastExitCode, lastStdout, lastStderr) = CaptureConsole(() => ProgramRunner.Run(
                firstArgs.Concat(["--cursor", "response:v1:2" + cursorFingerprint]).ToArray(),
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
    public void BoundedParserFailure_RespectsHardByteCapByLeavingStdoutEmpty_Issue4585()
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
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("raw JSON node count exceeded", stderr, StringComparison.Ordinal);
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

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
        => ConsoleCapture.Capture(action);

    private static void MarkGraphAndFoldReady(string dbPath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkFoldReady();
        writer.MarkCSharpSymbolNameContractReady();
    }
}
