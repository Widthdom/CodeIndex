using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class JsonEnvelopeWrapperIssue4730Tests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void Search_CompactAndArrayEnvelopeExposeAuthoritativeResumableMetadata_Issue4730()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("discovery_search_paging_4730");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var index = 1; index <= 3; index++)
                TestProjectHelper.InsertIndexedFile(dbPath, $"src/Needle{index}.txt", "text", $"Needle marker {index}\n");

            var compactArgs = new[]
            {
                "search", "Needle", "--db", dbPath, "--format", "compact", "--limit", "1",
            };
            var (compactExitCode, compactStdout, compactStderr) = CaptureConsole(() =>
                ProgramRunner.Run(compactArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, compactExitCode);
            Assert.Equal(string.Empty, compactStderr);
            using var compactDocument = JsonDocument.Parse(compactStdout);
            var compact = compactDocument.RootElement;
            Assert.Equal(3, compact.GetProperty("total_count").GetInt32());
            Assert.Equal(2, compact.GetProperty("omitted_count").GetInt32());
            Assert.True(compact.GetProperty("truncated").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(compact.GetProperty("result_stable_at").GetString()));
            var compactCursor = Assert.IsType<string>(compact.GetProperty("next_cursor").GetString());
            Assert.StartsWith("response:v2:", compactCursor, StringComparison.Ordinal);
            var firstFile = compact.GetProperty("results")[0].GetProperty("file").GetString();

            var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() =>
                ProgramRunner.Run(compactArgs.Concat(["--cursor", compactCursor]).ToArray(), _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, secondExitCode);
            Assert.Equal(string.Empty, secondStderr);
            using var secondDocument = JsonDocument.Parse(secondStdout);
            Assert.Equal(1, secondDocument.RootElement.GetProperty("cursor_offset").GetInt32());
            Assert.NotEqual(firstFile, secondDocument.RootElement.GetProperty("results")[0].GetProperty("file").GetString());

            var (arrayExitCode, arrayStdout, arrayStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Needle", "--db", dbPath, "--json=array", "--json-envelope", "--limit", "1"],
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, arrayExitCode);
            Assert.Equal(string.Empty, arrayStderr);
            using var arrayDocument = JsonDocument.Parse(arrayStdout);
            var arrayMetadata = arrayDocument.RootElement.GetProperty("metadata");
            Assert.Equal(3, arrayMetadata.GetProperty("total_count").GetInt32());
            Assert.True(arrayMetadata.GetProperty("total_count_authoritative").GetBoolean());
            Assert.Equal(2, arrayMetadata.GetProperty("omitted_count").GetInt32());
            Assert.StartsWith("response:v2:", arrayMetadata.GetProperty("next_cursor").GetString(), StringComparison.Ordinal);
            Assert.Single(arrayDocument.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(arrayDocument.RootElement.GetProperty("results")[0].TryGetProperty("snippet", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Search_DataDirCursorUsesEffectiveDatabaseGeneration_Issue4730()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("discovery_data_dir_cursor_4730");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/One.txt", "text", "DataDirNeedle one\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Two.txt", "text", "DataDirNeedle two\n");
            var dataDir = Path.GetDirectoryName(dbPath)!;
            var args = new[]
            {
                "search", "DataDirNeedle", "--data-dir", dataDir,
                "--format", "compact", "--limit", "1",
            };

            var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() =>
                ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            using var firstDocument = JsonDocument.Parse(firstStdout);
            Assert.Equal(
                Path.GetFullPath(dbPath),
                firstDocument.RootElement.GetProperty("metadata").GetProperty("db_path").GetString());
            var cursor = Assert.IsType<string>(firstDocument.RootElement.GetProperty("next_cursor").GetString());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, "2026-07-24T13:45:00.0000000+00:00");
            }

            var (staleExitCode, staleStdout, staleStderr) = CaptureConsole(() =>
                ProgramRunner.Run(args.Concat(["--cursor", cursor]).ToArray(), _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, staleExitCode);
            Assert.Equal(string.Empty, staleStdout);
            Assert.Contains("index generation changed", staleStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Search_PostSelectorsPageTheGloballySelectedSequence_Issue4730()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("discovery_search_selection_cursor_4730");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/a.cs", "csharp", "SelectorNeedle one\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/b.cs", "csharp", "SelectorNeedle three\n");
            ReplaceChunks(
                dbPath,
                "src/a.cs",
                new ChunkRecord { ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "SelectorNeedle one\n" },
                new ChunkRecord { ChunkIndex = 1, StartLine = 2, EndLine = 2, Content = "SelectorNeedle two\n" });

            AssertPagedSearchSelection(
                [
                    "search", "SelectorNeedle", "--db", dbPath, "--exact-substring",
                    "--first-per-file", "--format", "compact",
                ],
                expectedTotal: 2);
            AssertPagedSearchSelection(
                [
                    "search", "SelectorNeedle", "--db", dbPath, "--exact-substring",
                    "--sample", "2", "--format", "compact",
                ],
                expectedTotal: 2);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void SymbolsFilesAndLanguages_PageWithGenerationBoundCursors_Issue4730()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("discovery_catalog_paging_4730");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var index = 1; index <= 3; index++)
                TestProjectHelper.InsertIndexedFile(dbPath, $"src/Type{index}.cs", "csharp", $"public sealed class Type{index} {{ }}\n");

            var symbolsArgs = new[]
            {
                "symbols", "--db", dbPath, "--format", "compact", "--limit", "1",
            };
            var symbolsCursor = AssertPagedCompactCommand(symbolsArgs, "symbols");

            var filesArgs = new[]
            {
                "files", "--db", dbPath, "--format", "compact", "--limit", "1",
            };
            AssertPagedCompactCommand(filesArgs, "files");

            var languageArgs = new[]
            {
                "languages", "--db", dbPath, "--json", "--limit", "1", "--max-json-bytes", "8192",
            };
            var (languageExitCode, languageStdout, languageStderr) = CaptureConsole(() =>
                ProgramRunner.Run(languageArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, languageExitCode);
            Assert.Equal(string.Empty, languageStderr);
            Assert.True(Encoding.UTF8.GetByteCount(languageStdout) <= 8192);
            using var languageDocument = JsonDocument.Parse(languageStdout);
            var languageMetadata = languageDocument.RootElement.GetProperty("metadata");
            Assert.True(languageMetadata.GetProperty("total_count").GetInt32() > 1);
            Assert.True(languageMetadata.GetProperty("has_more").GetBoolean());
            Assert.StartsWith("response:v2:", languageMetadata.GetProperty("next_cursor").GetString(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(languageMetadata.GetProperty("result_stable_at").GetString()));
            Assert.Single(languageDocument.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(languageDocument.RootElement.GetProperty("results")[0].TryGetProperty("exact_filenames", out _));
            var languageCursor = languageMetadata.GetProperty("next_cursor").GetString()!;
            var firstLanguage = languageDocument.RootElement.GetProperty("results")[0].GetProperty("lang").GetString();

            var (nextLanguageExitCode, nextLanguageStdout, nextLanguageStderr) = CaptureConsole(() =>
                ProgramRunner.Run(languageArgs.Concat(["--cursor", languageCursor]).ToArray(), _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, nextLanguageExitCode);
            Assert.Equal(string.Empty, nextLanguageStderr);
            using (var nextLanguageDocument = JsonDocument.Parse(nextLanguageStdout))
            {
                Assert.Equal(1, nextLanguageDocument.RootElement.GetProperty("metadata").GetProperty("cursor_offset").GetInt32());
                Assert.NotEqual(firstLanguage, nextLanguageDocument.RootElement.GetProperty("results")[0].GetProperty("lang").GetString());
            }

            var (mismatchExitCode, mismatchStdout, mismatchStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    symbolsArgs.Concat(["--kind", "function", "--cursor", symbolsCursor]).ToArray(),
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, mismatchExitCode);
            Assert.Equal(string.Empty, mismatchStdout);
            Assert.Contains("does not match this command, query, or filter set", mismatchStderr, StringComparison.Ordinal);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, "2026-07-24T12:34:56.0000000+00:00");
            }

            var (staleExitCode, staleStdout, staleStderr) = CaptureConsole(() =>
                ProgramRunner.Run(symbolsArgs.Concat(["--cursor", symbolsCursor]).ToArray(), _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, staleExitCode);
            Assert.Equal(string.Empty, staleStdout);
            Assert.Contains("index generation changed", staleStderr, StringComparison.Ordinal);
            Assert.Contains("Restart pagination", staleStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_PartialScanCursorResumesAfterLastScannedLine_Issue4730()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_scan_resume_4730");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var content = string.Join('\n', Enumerable.Range(1, 12).Select(index => $"Needle {index}"));
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Many.txt", "text", content);
            var firstArgs = new[]
            {
                "find", "Needle", "--db", dbPath, "--all", "--exact",
                "--json=ndjson", "--limit", "10", "--line-scan-limit", "3",
            };

            var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() =>
                ProgramRunner.Run(firstArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.PartialResult, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            var firstLines = ParseNdjson(firstStdout);
            Assert.Equal(new[] { 1, 2, 3 }, firstLines[..^1].Select(row => row.GetProperty("line").GetInt32()).ToArray());
            var terminal = firstLines[^1];
            Assert.Equal("resume_with_next_cursor", terminal.GetProperty("continuation_action").GetString());
            var cursor = Assert.IsType<string>(terminal.GetProperty("next_cursor").GetString());
            Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);

            var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() =>
                ProgramRunner.Run(firstArgs.Concat(["--cursor", cursor]).ToArray(), _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.PartialResult, secondExitCode);
            Assert.Equal(string.Empty, secondStderr);
            using var secondDocument = JsonDocument.Parse(secondStdout);
            Assert.Equal(
                new[] { 4, 5, 6 },
                secondDocument.RootElement.GetProperty("results").EnumerateArray()
                    .Select(row => row.GetProperty("line").GetInt32())
                    .ToArray());
            var secondCursor = secondDocument.RootElement.GetProperty("metadata").GetProperty("next_cursor").GetString();
            Assert.NotEqual(cursor, secondCursor);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_ByteBudgetPagesCapturedRowsBeforeAdvancingScanCursor_Issue4730()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_scan_byte_resume_4730");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longPath = $"src/{new string('a', 180)}.txt";
            var content = string.Join('\n', Enumerable.Range(1, 12).Select(index => $"Needle {index}"));
            TestProjectHelper.InsertIndexedFile(dbPath, longPath, "text", content);
            var baseArgs = new[]
            {
                "find", "Needle", "--db", dbPath, "--all", "--exact",
                "--json=ndjson", "--fields", "path,line", "--limit", "10", "--line-scan-limit", "8",
            };

            JsonDocument? firstDocument = null;
            string? pageCursor = null;
            var firstReturned = 0;
            foreach (var byteBudget in new[] { 2_400, 2_800, 3_200, 3_600, 4_000, 4_800 })
            {
                var (exitCode, stdout, _) = CaptureConsole(() =>
                    ProgramRunner.Run(
                        baseArgs.Concat(["--max-json-bytes", byteBudget.ToString()]).ToArray(),
                        _jsonOptions,
                        "1.0.0-test"));
                if (exitCode != CommandExitCodes.PartialResult || string.IsNullOrWhiteSpace(stdout))
                    continue;

                var candidate = JsonDocument.Parse(stdout);
                var metadata = candidate.RootElement.GetProperty("metadata");
                if (!metadata.TryGetProperty("byte_limit_reached", out var reached) || !reached.GetBoolean())
                {
                    candidate.Dispose();
                    continue;
                }

                firstDocument = candidate;
                firstReturned = metadata.GetProperty("returned_count").GetInt32();
                pageCursor = metadata.GetProperty("next_cursor").GetString();
                break;
            }

            using (firstDocument)
            {
                Assert.NotNull(firstDocument);
                Assert.InRange(firstReturned, 1, 7);
                Assert.StartsWith("response:v2:", pageCursor, StringComparison.Ordinal);
                Assert.Equal(
                    Enumerable.Range(1, firstReturned),
                    firstDocument.RootElement.GetProperty("results").EnumerateArray()
                        .Select(row => row.GetProperty("line").GetInt32()));
            }

            var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    baseArgs.Concat(["--max-json-bytes", "65536", "--cursor", pageCursor!]).ToArray(),
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.PartialResult, secondExitCode);
            Assert.Equal(string.Empty, secondStderr);
            using var secondDocument = JsonDocument.Parse(secondStdout);
            Assert.Equal(
                Enumerable.Range(firstReturned + 1, 8 - firstReturned),
                secondDocument.RootElement.GetProperty("results").EnumerateArray()
                    .Select(row => row.GetProperty("line").GetInt32()));
            Assert.StartsWith(
                "response:v2:",
                secondDocument.RootElement.GetProperty("metadata").GetProperty("next_cursor").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private string AssertPagedCompactCommand(string[] args, string collectionName)
    {
        var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() =>
            ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.Success, firstExitCode);
        Assert.Equal(string.Empty, firstStderr);
        using var firstDocument = JsonDocument.Parse(firstStdout);
        var first = firstDocument.RootElement;
        var metadata = first.GetProperty("metadata");
        Assert.True(metadata.GetProperty("total_count").GetInt32() > 1);
        Assert.True(metadata.GetProperty("has_more").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(metadata.GetProperty("result_stable_at").GetString()));
        var cursor = Assert.IsType<string>(metadata.GetProperty("next_cursor").GetString());
        Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);
        var firstPath = first.GetProperty(collectionName)[0].GetProperty("path").GetString();

        var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() =>
            ProgramRunner.Run(args.Concat(["--cursor", cursor]).ToArray(), _jsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.Success, secondExitCode);
        Assert.Equal(string.Empty, secondStderr);
        using var secondDocument = JsonDocument.Parse(secondStdout);
        var second = secondDocument.RootElement;
        Assert.Equal(1, second.GetProperty("cursor_offset").GetInt32());
        Assert.NotEqual(firstPath, second.GetProperty(collectionName)[0].GetProperty("path").GetString());
        return cursor;
    }

    private void AssertPagedSearchSelection(string[] baseArgs, int expectedTotal)
    {
        var (allExitCode, allStdout, allStderr) = CaptureConsole(() =>
            ProgramRunner.Run(baseArgs.Concat(["--limit", "10"]).ToArray(), _jsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.Success, allExitCode);
        Assert.Equal(string.Empty, allStderr);
        using var allDocument = JsonDocument.Parse(allStdout);
        var expectedFiles = allDocument.RootElement.GetProperty("results")
            .EnumerateArray()
            .Select(result => result.GetProperty("file").GetString())
            .ToArray();
        Assert.Equal(expectedTotal, expectedFiles.Length);

        var pageArgs = baseArgs.Concat(["--limit", "1"]).ToArray();
        var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() =>
            ProgramRunner.Run(pageArgs, _jsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.Success, firstExitCode);
        Assert.Equal(string.Empty, firstStderr);
        using var firstDocument = JsonDocument.Parse(firstStdout);
        Assert.Equal(expectedTotal, firstDocument.RootElement.GetProperty("total_count").GetInt32());
        Assert.Equal(expectedFiles[0], firstDocument.RootElement.GetProperty("results")[0].GetProperty("file").GetString());
        var cursor = Assert.IsType<string>(firstDocument.RootElement.GetProperty("next_cursor").GetString());

        var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() =>
            ProgramRunner.Run(pageArgs.Concat(["--cursor", cursor]).ToArray(), _jsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.Success, secondExitCode);
        Assert.Equal(string.Empty, secondStderr);
        using var secondDocument = JsonDocument.Parse(secondStdout);
        Assert.Equal(expectedFiles[1], secondDocument.RootElement.GetProperty("results")[0].GetProperty("file").GetString());
        Assert.False(secondDocument.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, secondDocument.RootElement.GetProperty("next_cursor").ValueKind);
    }

    private static void ReplaceChunks(string dbPath, string path, params ChunkRecord[] chunks)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT id FROM files WHERE path = @path";
        command.Parameters.AddWithValue("@path", path);
        var fileId = (long)(command.ExecuteScalar()
            ?? throw new InvalidOperationException($"Missing indexed file {path}."));
        var writer = new DbWriter(db.Connection);
        writer.DeleteFileData(fileId);
        foreach (var chunk in chunks)
            chunk.FileId = fileId;
        writer.InsertChunks(chunks);
    }

    private static JsonElement[] ParseNdjson(string stdout)
        => stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
        => ConsoleCapture.Capture(action);
}
