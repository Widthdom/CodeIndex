using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class ExportImportCommandRunnerTests
{
    [Fact]
    public void RunImport_JsonParseErrorIncludesPhaseAndCode_Issue3548()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ExportImportCommandRunner.RunImport(["--json", "--unknown"], jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        AssertExportImportError(stdout, "import", "parse_args", "import_unknown_option");
    }

    [Fact]
    public void RunImport_JsonManifestErrorIncludesPhaseAndCode_Issue3548()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_manifest_json_error");
        try
        {
            var archivePath = CreateArchiveWithManifest(workDir, "{");
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", dbPath, "--json"], jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertExportImportError(stdout, "import", "manifest", "import_manifest_invalid");
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void RunImport_JsonSqliteValidationErrorIncludesPhaseAndCode_Issue3548()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_import_sqlite_json_error");
        try
        {
            var databaseBytes = new byte[] { 1, 2, 3, 4 };
            var sha256 = Convert.ToHexString(SHA256.HashData(databaseBytes)).ToLowerInvariant();
            var manifest = $$"""
                {"format_version":"1","cdidx_version":"test","user_version":0,"database_sha256":"{{sha256}}"}
                """;
            var archivePath = CreateArchiveWithManifestAndDatabase(workDir, manifest, databaseBytes);
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", dbPath, "--json"], jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertExportImportError(stdout, "import", "sqlite_validate", "import_manifest_mismatch");
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void RunImport_RejectsOversizedManifestBeforeDatabaseEntry()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_manifest_size");
        try
        {
            var manifest = new string(' ', ExportImportCommandRunner.MaxImportManifestBytes + 1);
            var archivePath = CreateArchiveWithManifest(workDir, manifest);
            var dbPath = Path.Combine(workDir, "codeindex.db");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", dbPath], new JsonSerializerOptions()));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("archive manifest is invalid: archive manifest.json is too large", stderr);
            Assert.DoesNotContain("archive is missing codeindex.db", stderr);
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void RunImport_RejectsDeepManifestBeforeDatabaseEntry()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_manifest_depth");
        try
        {
            var depth = ExportImportCommandRunner.MaxImportManifestJsonDepth + 4;
            var manifest =
                "{\"format_version\":\"1\",\"cdidx_version\":\"test\",\"user_version\":0,\"database_sha256\":\"" +
                new string('0', 64) +
                "\",\"nested\":" +
                string.Concat(Enumerable.Repeat("{\"x\":", depth)) +
                "0" +
                new string('}', depth) +
                "}";
            var archivePath = CreateArchiveWithManifest(workDir, manifest);
            var dbPath = Path.Combine(workDir, "codeindex.db");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", dbPath], new JsonSerializerOptions()));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains($"manifest.json exceeds the JSON depth limit of {ExportImportCommandRunner.MaxImportManifestJsonDepth}", stderr);
            Assert.DoesNotContain("archive is missing codeindex.db", stderr);
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Theory]
    [InlineData("../manifest.json", "import_archive_unsafe_entry_name", "parent-directory")]
    [InlineData("./manifest.json", "import_archive_noncanonical_entry_name", "non-canonical entry")]
    [InlineData("payload.txt", "import_archive_unexpected_entry", "unexpected entry")]
    public void RunImport_RejectsUnsafeOrUnexpectedArchiveEntriesBeforeReadingManifest(
        string entryName,
        string expectedErrorCode,
        string expectedMessage)
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_import_entries");
        try
        {
            var archivePath = CreateArchiveWithTextEntries(workDir, "unsafe-entry.cdidx.zip", (entryName, "{}"));
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", dbPath, "--json"], jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertExportImportError(stdout, "import", "open_archive", expectedErrorCode);
            using var document = JsonDocument.Parse(stdout);
            Assert.Contains(expectedMessage, document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void RunImport_RejectsDuplicateArchiveEntriesBeforeReadingManifest()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_import_duplicate_entries");
        try
        {
            var archivePath = CreateArchiveWithTextEntries(
                workDir,
                "duplicate-entry.cdidx.zip",
                ("manifest.json", "{"),
                ("manifest.json", "{}"));
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", dbPath, "--json"], jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            AssertExportImportError(stdout, "import", "manifest", "import_archive_duplicate_entry");
            using var document = JsonDocument.Parse(stdout);
            Assert.Contains("duplicate entry", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void RunExportCtags_RejectsDatabaseAndSidecarOutputPaths(string outputSuffix)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ctags_output_guard");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var outputPath = dbPath + outputSuffix;
            var outputExisted = File.Exists(outputPath);
            var outputInfo = outputExisted ? new FileInfo(outputPath) : null;
            var outputLength = outputInfo?.Length;
            var outputLastWriteUtc = outputInfo?.LastWriteTimeUtc;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    ["ctags", "--db", dbPath, "--output", outputPath],
                    new JsonSerializerOptions(),
                    "test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("ctags output path must not be the source database or a SQLite sidecar", stderr);
            Assert.Equal(outputExisted, File.Exists(outputPath));
            if (outputInfo != null)
            {
                outputInfo.Refresh();
                Assert.Equal(outputLength, outputInfo.Length);
                Assert.Equal(outputLastWriteUtc, outputInfo.LastWriteTimeUtc);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportCtags_MissingDatabaseDoesNotCreateDatabase_Issue3368()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_ctags_missing_db");
        try
        {
            var dbPath = Path.Combine(workDir, "missing.db");
            var outputPath = Path.Combine(workDir, "tags");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    ["ctags", "--db", dbPath, "--output", outputPath],
                    new JsonSerializerOptions(),
                    "test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("database", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(dbPath));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void RunExportCtags_JsonReportsSkipReasonsAndGeneratedPolicy_Issue4720()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ctags_json_filters");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "public class App { public void Run() {} }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "tests/AppTests.cs", "csharp", "public class AppTests { public void Run() {} }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Generated.cs", "csharp", "public class Generated { }\n", isGenerated: true);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Excluded.cs", "csharp", "public class Excluded { }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "docs/Guide.cs", "csharp", "public class Guide { }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/tool.py", "python", "def run():\n    pass\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO symbols(file_id, kind, name, line)
                    SELECT id, 'class', '', 1 FROM files WHERE path = 'src/App.cs';
                    INSERT INTO symbols(file_id, kind, name, line)
                    SELECT id, NULL, 'UnsupportedKind', 1 FROM files WHERE path = 'src/App.cs';
                    """;
                cmd.ExecuteNonQuery();
            }
            var outputPath = Path.Combine(projectRoot, "tags");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [
                        "ctags",
                        "--db",
                        dbPath,
                        "--output",
                        outputPath,
                        "--json",
                        "--lang",
                        "csharp",
                        "--path",
                        "src/",
                        "--exclude-path",
                        "src/Excluded*",
                        "--exclude-tests"
                    ],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(File.Exists(outputPath));
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.Equal("1", root.GetProperty("api_version").GetString());
            Assert.Equal("success", root.GetProperty("status").GetString());
            Assert.Equal(Path.GetFullPath(outputPath), root.GetProperty("output_path").GetString());
            Assert.Equal(Path.GetFullPath(dbPath), root.GetProperty("db_path").GetString());
            Assert.True(root.GetProperty("tag_count").GetInt64() > 0);
            Assert.True(root.GetProperty("emitted_count").GetInt64() > 0);
            Assert.True(root.GetProperty("skipped_count").GetInt64() > 0);
            Assert.Equal(
                root.GetProperty("tag_count").GetInt64(),
                root.GetProperty("emitted_count").GetInt64() + root.GetProperty("skipped_count").GetInt64());

            var filters = root.GetProperty("filters");
            Assert.Equal("csharp", filters.GetProperty("lang").GetString());
            Assert.Equal("src/", filters.GetProperty("path")[0].GetString());
            Assert.Equal("src/Excluded*", filters.GetProperty("exclude_path")[0].GetString());
            Assert.True(filters.GetProperty("exclude_tests").GetBoolean());
            Assert.False(filters.GetProperty("include_generated").GetBoolean());
            Assert.Equal("exclude", filters.GetProperty("generated_code_policy").GetString());
            Assert.True(filters.GetProperty("generated_file_filter_available").GetBoolean());
            Assert.Contains(root.GetProperty("metadata_fields").EnumerateArray(), field => field.GetString() == "language");

            var skipReasonCounts = root.GetProperty("skip_reason_counts");
            Assert.True(skipReasonCounts.GetProperty("invalid_name").GetInt64() > 0);
            Assert.True(skipReasonCounts.GetProperty("unsupported_kind").GetInt64() > 0);
            Assert.True(skipReasonCounts.GetProperty("generated_code").GetInt64() > 0);
            Assert.True(skipReasonCounts.GetProperty("language_filter").GetInt64() > 0);
            Assert.True(skipReasonCounts.GetProperty("test_filter").GetInt64() > 0);
            Assert.True(skipReasonCounts.GetProperty("path_filter").GetInt64() > 0);
            Assert.True(skipReasonCounts.GetProperty("exclude_path_filter").GetInt64() > 0);
            Assert.Equal(0, skipReasonCounts.GetProperty("other").GetInt64());
            Assert.Equal(
                root.GetProperty("skipped_count").GetInt64(),
                skipReasonCounts.EnumerateObject().Sum(reason => reason.Value.GetInt64()));

            var tags = File.ReadAllText(outputPath);
            Assert.Contains("App\tsrc/App.cs", tags);
            Assert.Contains("language:csharp", tags);
            Assert.DoesNotContain("AppTests", tags);
            Assert.DoesNotContain("Generated", tags);
            Assert.DoesNotContain("Excluded", tags);
            Assert.DoesNotContain("Guide", tags);
            Assert.DoesNotContain("tool.py", tags);

            var (includeExitCode, includeStdout, includeStderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [
                        "ctags",
                        "--db",
                        dbPath,
                        "--output",
                        outputPath,
                        "--json",
                        "--lang",
                        "csharp",
                        "--path",
                        "src/",
                        "--exclude-path",
                        "src/Excluded*",
                        "--exclude-tests",
                        "--include-generated"
                    ],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, includeExitCode);
            Assert.Equal(string.Empty, includeStderr);
            using var includeDocument = JsonDocument.Parse(includeStdout);
            var includeRoot = includeDocument.RootElement;
            var includeFilters = includeRoot.GetProperty("filters");
            Assert.True(includeFilters.GetProperty("include_generated").GetBoolean());
            Assert.Equal("include", includeFilters.GetProperty("generated_code_policy").GetString());
            Assert.True(includeFilters.GetProperty("generated_file_filter_available").GetBoolean());
            Assert.Equal(0, includeRoot.GetProperty("skip_reason_counts").GetProperty("generated_code").GetInt64());
            Assert.Contains("Generated\tsrc/Generated.cs", File.ReadAllText(outputPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportCtags_LegacyDatabaseReportsGeneratedFilterUnavailable_Issue4720()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ctags_legacy_generated_filter");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Generated.cs", "csharp", "public class Generated { }\n", isGenerated: true);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = """
                    DROP INDEX idx_files_generated;
                    ALTER TABLE files DROP COLUMN generated;
                    """;
                cmd.ExecuteNonQuery();
            }

            var outputPath = Path.Combine(projectRoot, "tags");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    ["ctags", "--db", dbPath, "--output", outputPath, "--json"],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var filters = root.GetProperty("filters");
            Assert.False(filters.GetProperty("include_generated").GetBoolean());
            Assert.Equal("unavailable", filters.GetProperty("generated_code_policy").GetString());
            Assert.False(filters.GetProperty("generated_file_filter_available").GetBoolean());
            Assert.Equal(0, root.GetProperty("skip_reason_counts").GetProperty("generated_code").GetInt64());
            Assert.Contains("Generated\tsrc/Generated.cs", File.ReadAllText(outputPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportCtags_JsonUnknownOptionReturnsStructuredError_Issue3551()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ExportImportCommandRunner.RunExport(["ctags", "--json", "--bogus"], jsonOptions, "test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        AssertExportImportError(stdout, "export", "parse_args", "ctags_export_unknown_option");
    }

    [Fact]
    public void IsDatabaseOrSqliteSidecarPath_UsesStampedCaseSensitivity_Issue3368()
    {
        var dbPath = Path.Combine("Project", ".cdidx", "codeindex.db");
        var dbPathCaseVariant = Path.Combine("Project", ".cdidx", "CODEINDEX.DB");
        var walPathCaseVariant = Path.Combine("Project", ".cdidx", "CODEINDEX.DB-WAL");

        Assert.False(ExportImportCommandRunner.IsDatabaseOrSqliteSidecarPath(
            dbPathCaseVariant,
            dbPath,
            StringComparison.Ordinal));
        Assert.False(ExportImportCommandRunner.IsDatabaseOrSqliteSidecarPath(
            walPathCaseVariant,
            dbPath,
            StringComparison.Ordinal));

        Assert.True(ExportImportCommandRunner.IsDatabaseOrSqliteSidecarPath(
            dbPathCaseVariant,
            dbPath,
            StringComparison.OrdinalIgnoreCase));
        Assert.True(ExportImportCommandRunner.IsDatabaseOrSqliteSidecarPath(
            walPathCaseVariant,
            dbPath,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsDatabaseOrSqliteSidecarPath_UsesLiveIgnoreCaseForMovedSensitiveStamp_Issue3368()
    {
        lock (PathCasingTestLock.Gate)
        {
            PathCasing.ResetCacheForTests();
            var projectRoot = TestProjectHelper.CreateTempProject("export_path_case_moved_stamp");
            try
            {
                var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
                using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                {
                    var writer = new DbWriter(db.Connection);
                    writer.SetMeta(DbContext.WorkspacePathCaseSensitiveMetaKey, bool.TrueString);
                }

                PathCasing.SeedFromWorkspace(Path.GetDirectoryName(dbPath)!, ignoreCase: true);
                var outputPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "CODEINDEX.DB");

                Assert.True(ExportImportCommandRunner.IsDatabaseOrSqliteSidecarPath(outputPath, dbPath));
            }
            finally
            {
                PathCasing.ResetCacheForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Theory]
    [InlineData("true", StringComparison.Ordinal)]
    [InlineData("false", StringComparison.OrdinalIgnoreCase)]
    public void ResolveDatabasePathComparison_UsesWorkspaceStamp_Issue3368(
        string stamp,
        StringComparison expectedComparison)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_path_case_stamp");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.WorkspacePathCaseSensitiveMetaKey, stamp);
            }

            Assert.Equal(expectedComparison, ExportImportCommandRunner.ResolveDatabasePathComparison(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImport_FailureOmitsRawExceptionMessage()
    {
        var workDir = TestProjectHelper.CreateTempProject("import_error_sanitize");
        try
        {
            var archiveDirectory = Path.Combine(workDir, "archive-directory");
            Directory.CreateDirectory(archiveDirectory);
            var dbPath = Path.Combine(workDir, "codeindex.db");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archiveDirectory, "--db", dbPath], new JsonSerializerOptions()));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("import failed (", stderr);
            Assert.DoesNotContain(archiveDirectory, stderr);
            Assert.DoesNotContain(Path.GetFileName(archiveDirectory), stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void FormatImportManifestReadException_SanitizesRawMessage_Issue3796()
    {
        var secretPath = Path.Combine(Path.GetTempPath(), "secret-import-token-ghp_1234567890abcdef-private", "manifest.json");
        var ex = new InvalidDataException($"archive manifest stream failed near {secretPath} payload token=ghp_abcdef1234567890_private");

        var message = ExportImportCommandRunner.FormatImportManifestReadException(ex);

        Assert.Equal("InvalidDataException", message);
        Assert.DoesNotContain(secretPath, message);
        Assert.DoesNotContain("ghp_abcdef1234567890", message);
        Assert.DoesNotContain("manifest.json", message);
    }

    [Fact]
    public void RunImport_TemporaryDatabaseCleanupFailureWarnsAndPreservesImportError_Issue3032()
    {
        var workDir = TestProjectHelper.CreateTempProject("import_temp_cleanup_warning");
        string? cleanupPath = null;
        try
        {
            var manifest = $$"""
                {"format_version":"1","cdidx_version":"test","user_version":0,"database_sha256":"{{new string('0', 64)}}"}
                """;
            var archivePath = CreateArchiveWithManifestAndDatabase(workDir, manifest, [1, 2, 3, 4]);
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            ExportImportCommandRunner.DeleteFileForTesting = path =>
            {
                if (Path.GetFileName(path).StartsWith(".codeindex-import-", StringComparison.Ordinal)
                    && path.EndsWith(".db", StringComparison.Ordinal))
                {
                    cleanupPath = path;
                    throw new IOException("simulated import temp cleanup failure");
                }

                File.Delete(path);
            };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", dbPath], jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("archive manifest mismatch: database_sha256 does not match codeindex.db", stderr);
            Assert.Contains("Warning: failed to delete import temporary database", stderr);
            Assert.Contains("IOException", stderr);
            Assert.NotNull(cleanupPath);
            Assert.True(File.Exists(cleanupPath));
        }
        finally
        {
            ExportImportCommandRunner.DeleteFileForTesting = null;
            if (cleanupPath != null && File.Exists(cleanupPath))
                File.Delete(cleanupPath);
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void RunImport_DryRunUsesPrivateTemporaryDirectory_Issue3411()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("import_dry_run_private_temp");
        string? cleanupPath = null;
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var archivePath = Path.Combine(projectRoot, "codeindex.cdidx.zip");
            var exportResult = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport([archivePath, "--db", sourceDbPath], new JsonSerializerOptions(), "test"));
            Assert.Equal(CommandExitCodes.Success, exportResult.ExitCode);

            var importDbPath = Path.Combine(projectRoot, "imported.db");
            ExportImportCommandRunner.DeleteFileForTesting = path =>
            {
                if (Path.GetFileName(path) == "codeindex.db"
                    && Path.GetFileName(Path.GetDirectoryName(path)!).StartsWith("codeindex-import-", StringComparison.Ordinal))
                {
                    cleanupPath = path;
                    throw new IOException("simulated import dry-run temp cleanup failure");
                }

                File.Delete(path);
            };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", importDbPath, "--dry-run"], new JsonSerializerOptions()));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Validated CodeIndex archive", stdout);
            Assert.False(File.Exists(importDbPath));
            Assert.Contains("Warning: failed to delete import temporary database", stderr);
            Assert.NotNull(cleanupPath);
            Assert.True(File.Exists(cleanupPath));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    DataDirectorySecurity.PrivateDirectoryMode,
                    File.GetUnixFileMode(Path.GetDirectoryName(cleanupPath)!) & DataDirectorySecurity.PermissionBits);
            }
        }
        finally
        {
            ExportImportCommandRunner.DeleteFileForTesting = null;
            if (cleanupPath != null && File.Exists(cleanupPath))
                TestProjectHelper.DeleteDirectory(Path.GetDirectoryName(cleanupPath)!);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImport_PrunePathsJsonUsesDestinationProjectRoot_Issue3459()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var sourceRoot = TestProjectHelper.CreateTempProject("import_prune_source");
        var targetRoot = TestProjectHelper.CreateTempProject("import_prune_target");
        var unrelatedCwd = TestProjectHelper.CreateTempProject("import_prune_cwd");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(sourceRoot);
            TestProjectHelper.InsertIndexedFile(sourceDbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");
            var archivePath = Path.Combine(sourceRoot, "codeindex.cdidx.zip");
            var targetDbPath = Path.Combine(targetRoot, ".cdidx", "codeindex.db");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var exportResult = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport([archivePath, "--db", sourceDbPath], jsonOptions, "test"));
            Assert.Equal(CommandExitCodes.Success, exportResult.ExitCode);

            Directory.SetCurrentDirectory(unrelatedCwd);
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", targetDbPath, "--prune-paths", "--json"], jsonOptions));

            var expectedRoot = Path.GetFullPath(targetRoot);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(Path.GetFullPath(targetDbPath), document.RootElement.GetProperty("db_path").GetString());
            Assert.True(document.RootElement.GetProperty("pruned_paths").GetBoolean());
            Assert.Equal(expectedRoot, document.RootElement.GetProperty("pruned_project_root").GetString());
            Assert.Equal(expectedRoot, ReadMetaValue(targetDbPath, DbContext.IndexedProjectRootMetaKey));
            Assert.NotEqual(Path.GetFullPath(unrelatedCwd), ReadMetaValue(targetDbPath, DbContext.IndexedProjectRootMetaKey));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            TestProjectHelper.DeleteDirectory(sourceRoot);
            TestProjectHelper.DeleteDirectory(targetRoot);
            TestProjectHelper.DeleteDirectory(unrelatedCwd);
        }
    }

    [Fact]
    public void RunImport_PrunePathsHumanOutputReportsDestinationProjectRoot_Issue3459()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var sourceRoot = TestProjectHelper.CreateTempProject("import_prune_human_source");
        var targetRoot = TestProjectHelper.CreateTempProject("import_prune_human_target");
        var unrelatedCwd = TestProjectHelper.CreateTempProject("import_prune_human_cwd");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(sourceRoot);
            TestProjectHelper.InsertIndexedFile(sourceDbPath, "src/app.cs", "csharp", "class App { void Run() {} }\n");
            var archivePath = Path.Combine(sourceRoot, "codeindex.cdidx.zip");
            var targetDbPath = Path.Combine(targetRoot, ".cdidx", "codeindex.db");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var exportResult = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport([archivePath, "--db", sourceDbPath], jsonOptions, "test"));
            Assert.Equal(CommandExitCodes.Success, exportResult.ExitCode);

            Directory.SetCurrentDirectory(unrelatedCwd);
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", targetDbPath, "--prune-paths", "--dry-run"], jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains($"pruned paths to project root {Path.GetFullPath(targetRoot)}", stdout);
            Assert.False(File.Exists(targetDbPath));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            TestProjectHelper.DeleteDirectory(sourceRoot);
            TestProjectHelper.DeleteDirectory(targetRoot);
            TestProjectHelper.DeleteDirectory(unrelatedCwd);
        }
    }

    [Fact]
    public void RunExportArchive_FailureOmitsRawExceptionMessage()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_error_sanitize");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var outputDirectory = Path.Combine(projectRoot, "archive-output");
            Directory.CreateDirectory(outputDirectory);

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [outputDirectory, "--db", dbPath, "--overwrite"],
                    new JsonSerializerOptions(),
                    "test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("export failed (", stderr);
            Assert.DoesNotContain(outputDirectory, stderr);
            Assert.DoesNotContain(Path.GetFileName(outputDirectory), stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_TemporaryDatabaseCleanupFailureWarnsWithoutFailing_Issue3032()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_temp_cleanup_warning");
        string? cleanupPath = null;
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var outputPath = Path.Combine(projectRoot, "codeindex.cdidx.zip");
            ExportImportCommandRunner.DeleteFileForTesting = path =>
            {
                if (Path.GetFileName(path) == "codeindex.db"
                    && Path.GetFileName(Path.GetDirectoryName(path)!).StartsWith("codeindex-export-", StringComparison.Ordinal))
                {
                    cleanupPath = path;
                    throw new IOException("simulated export temp cleanup failure");
                }

                File.Delete(path);
            };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport([outputPath, "--db", dbPath], new JsonSerializerOptions(), "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Exported CodeIndex archive", stdout);
            Assert.True(File.Exists(outputPath));
            Assert.Contains("Warning: failed to delete export temporary database", stderr);
            Assert.Contains("IOException", stderr);
            Assert.NotNull(cleanupPath);
            Assert.True(File.Exists(cleanupPath));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    DataDirectorySecurity.PrivateDirectoryMode,
                    File.GetUnixFileMode(Path.GetDirectoryName(cleanupPath)!) & DataDirectorySecurity.PermissionBits);
            }
        }
        finally
        {
            ExportImportCommandRunner.DeleteFileForTesting = null;
            if (cleanupPath != null && File.Exists(cleanupPath))
                TestProjectHelper.DeleteDirectory(Path.GetDirectoryName(cleanupPath)!);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_RelativeOutputReportsAndWritesFullPath_Issue3138()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var projectRoot = TestProjectHelper.CreateTempProject("export_archive_full_output");
        try
        {
            Directory.SetCurrentDirectory(projectRoot);
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var expectedOutput = Path.GetFullPath("codeindex.cdidx.zip");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(["codeindex.cdidx.zip", "--db", dbPath, "--json"], jsonOptions, "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(File.Exists(expectedOutput));
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(expectedOutput, document.RootElement.GetProperty("archive_path").GetString());
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_DefaultRefusesExistingDestination_Issue4827()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_archive_existing_destination");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var archivePath = Path.Combine(projectRoot, "codeindex.cdidx.zip");
            File.WriteAllText(archivePath, "existing archive");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport([archivePath, "--db", dbPath, "--json"], jsonOptions, "test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("write_archive", document.RootElement.GetProperty("phase").GetString());
            Assert.Equal("export_archive_exists", document.RootElement.GetProperty("error_code").GetString());
            Assert.Contains("--overwrite", document.RootElement.GetProperty("hint").GetString());
            Assert.Equal("existing archive", File.ReadAllText(archivePath));
            Assert.Empty(Directory.GetFiles(projectRoot, ".cdidx-*.tmp"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_OverwriteReturnsVerifiablePrivateArtifactMetadata_Issue4827()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_archive_artifact_metadata");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "public class App { public void Run() { } }");
            var archivePath = Path.Combine(projectRoot, "codeindex.cdidx.zip");
            File.WriteAllText(archivePath, "replace me");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [archivePath, "--db", dbPath, "--overwrite", "--json"],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var resultDocument = JsonDocument.Parse(stdout);
            var result = resultDocument.RootElement;
            Assert.Equal(new FileInfo(archivePath).Length, result.GetProperty("archive_size_bytes").GetInt64());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant(),
                result.GetProperty("archive_sha256").GetString());

            using var archive = ZipFile.OpenRead(archivePath);
            using var manifestStream = archive.GetEntry("manifest.json")!.Open();
            using var manifestDocument = JsonDocument.Parse(manifestStream);
            Assert.Equal(
                manifestDocument.RootElement.GetRawText(),
                result.GetProperty("manifest").GetRawText());
            Assert.True(result.GetProperty("manifest").GetProperty("file_count").GetInt64() > 0);
            Assert.Equal(
                result.GetProperty("manifest").GetProperty("scope").GetRawText(),
                result.GetProperty("scope").GetRawText());

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    DataDirectorySecurity.PrivateFileMode,
                    File.GetUnixFileMode(archivePath) & DataDirectorySecurity.PermissionBits);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_DefaultRefusesDanglingSymlinkDestination_Issue4827()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("export_archive_symlink_destination");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var archivePath = Path.Combine(projectRoot, "codeindex.cdidx.zip");
            var missingTargetPath = Path.Combine(projectRoot, "missing-target.cdidx.zip");
            File.CreateSymbolicLink(archivePath, missingTargetPath);
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport([archivePath, "--db", dbPath, "--json"], jsonOptions, "test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("export_archive_exists", document.RootElement.GetProperty("error_code").GetString());
            Assert.Equal(missingTargetPath, new FileInfo(archivePath).LinkTarget);
            Assert.False(File.Exists(missingTargetPath));
            Assert.Empty(Directory.GetFiles(projectRoot, ".cdidx-*.tmp"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_AppliesProjectPathLanguageAndTestScope_Issue4714()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_archive_scope");
        try
        {
            TestProjectHelper.WriteTextFile(projectRoot, "src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App/App.cs", "csharp", "public class AppType { }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/shared/Shared.cs", "csharp", "public class SharedType { }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Other/Other.cs", "csharp", "public class OtherType { }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App/tool.py", "python", "def tool(): pass");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App/tests/AppTests.cs", "csharp", "public class AppTests { }");
            var archivePath = Path.Combine(projectRoot, "scoped.cdidx.zip");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [
                        archivePath,
                        "--db", dbPath,
                        "--project", "App",
                        "--path", "src/shared/*",
                        "--lang", "csharp",
                        "--exclude-tests",
                        "--json",
                    ],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var result = JsonDocument.Parse(stdout);
            var scope = result.RootElement.GetProperty("scope");
            Assert.True(scope.GetProperty("scoped").GetBoolean());
            Assert.Equal(5, scope.GetProperty("source_file_count").GetInt64());
            Assert.Equal(2, scope.GetProperty("exported_file_count").GetInt64());
            Assert.Equal("src/App/*", scope.GetProperty("resolved_project_path")[0].GetString());

            var extractedDb = Path.Combine(projectRoot, "scoped.db");
            using (var archive = ZipFile.OpenRead(archivePath))
                archive.GetEntry("codeindex.db")!.ExtractToFile(extractedDb);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = extractedDb }.ConnectionString);
            connection.Open();
            using (var filesCommand = connection.CreateCommand())
            {
                filesCommand.CommandText = "SELECT path FROM files ORDER BY path";
                using var reader = filesCommand.ExecuteReader();
                var paths = new List<string>();
                while (reader.Read())
                    paths.Add(reader.GetString(0));
                Assert.Equal(["src/App/App.cs", "src/shared/Shared.cs"], paths);
            }
            using (var integrityCommand = connection.CreateCommand())
            {
                integrityCommand.CommandText = "PRAGMA foreign_key_check";
                using var reader = integrityCommand.ExecuteReader();
                Assert.False(reader.Read());
            }

            using var manifest = ZipFile.OpenRead(archivePath);
            using var manifestStream = manifest.GetEntry("manifest.json")!.Open();
            using var manifestDocument = JsonDocument.Parse(manifestStream);
            Assert.Equal(2, manifestDocument.RootElement.GetProperty("file_count").GetInt64());
            Assert.True(manifestDocument.RootElement.GetProperty("scope").GetProperty("scoped").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_MigratesLegacySnapshotBeforeApplyingScope_Issue4714()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_archive_legacy_scope");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/keep/Keep.cs", "csharp", "public class Keep { }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/drop/Drop.cs", "csharp", "public class Drop { }");
            using (var legacyConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                legacyConnection.Open();
                using var legacyCommand = legacyConnection.CreateCommand();
                legacyCommand.CommandText = "DROP TABLE symbol_reference_candidates";
                legacyCommand.ExecuteNonQuery();
            }
            var archivePath = Path.Combine(projectRoot, "legacy-scope.cdidx.zip");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, _, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [archivePath, "--db", dbPath, "--path", "src/keep/*"],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var extractedDb = Path.Combine(projectRoot, "legacy-scope.db");
            using (var archive = ZipFile.OpenRead(archivePath))
                archive.GetEntry("codeindex.db")!.ExtractToFile(extractedDb);
            using var scopedConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = extractedDb }.ConnectionString);
            scopedConnection.Open();
            using var scopedCommand = scopedConnection.CreateCommand();
            scopedCommand.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM files),
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'symbol_reference_candidates')
                """;
            using var reader = scopedCommand.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_ReResolvesRetainedReferencesAfterScopePruning_Issue4714()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_archive_reference_resolution");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/keep/Source.cs", "csharp", "public class Source { }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/keep/Target.cs", "csharp", "public class Target { }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/drop/Target.cs", "csharp", "public class Target { }");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO symbol_references(
                        file_id,
                        symbol_name,
                        symbol_name_folded,
                        reference_kind,
                        line,
                        column_number,
                        context,
                        container_kind,
                        container_name,
                        container_name_folded,
                        source_symbol_id,
                        resolution_candidate_count,
                        resolution_state,
                        is_self_reference,
                        is_mutual_recursion)
                    SELECT
                        source_file.id,
                        'Target',
                        'target',
                        'instantiate',
                        1,
                        1,
                        'new Target()',
                        'class',
                        'Source',
                        'source',
                        source_symbol.id,
                        2,
                        'ambiguous',
                        1,
                        1
                    FROM files source_file
                    JOIN symbols source_symbol ON source_symbol.file_id = source_file.id
                    WHERE source_file.path = 'src/keep/Source.cs'
                      AND source_symbol.name = 'Source';

                    INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
                    SELECT last_insert_rowid(), target_symbol.id, 2
                    FROM symbols target_symbol
                    JOIN files target_file ON target_file.id = target_symbol.file_id
                    WHERE target_symbol.name = 'Target'
                      AND target_file.path = 'src/drop/Target.cs';
                    """;
                command.ExecuteNonQuery();
            }
            var archivePath = Path.Combine(projectRoot, "resolved-scope.cdidx.zip");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, _, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [archivePath, "--db", dbPath, "--path", "src/keep/*"],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var extractedDb = Path.Combine(projectRoot, "resolved-scope.db");
            using (var archive = ZipFile.OpenRead(archivePath))
                archive.GetEntry("codeindex.db")!.ExtractToFile(extractedDb);
            using var scopedConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = extractedDb }.ConnectionString);
            scopedConnection.Open();
            using var scopedCommand = scopedConnection.CreateCommand();
            scopedCommand.CommandText = """
                SELECT
                    resolution_candidate_count,
                    resolution_state,
                    target_symbol_id,
                    target_symbol_key,
                    is_self_reference,
                    is_mutual_recursion,
                    (SELECT COUNT(*) FROM symbol_reference_candidates),
                    (
                        SELECT target_file.path
                        FROM symbol_reference_candidates candidate
                        JOIN symbols target_symbol ON target_symbol.id = candidate.symbol_id
                        JOIN files target_file ON target_file.id = target_symbol.file_id
                        WHERE candidate.reference_id = symbol_references.id
                        LIMIT 1
                    )
                FROM symbol_references
                """;
            using var reader = scopedCommand.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("resolved", reader.GetString(1));
            Assert.False(reader.IsDBNull(2));
            Assert.False(reader.IsDBNull(3));
            Assert.Equal(0, reader.GetInt32(4));
            Assert.Equal(0, reader.GetInt32(5));
            Assert.Equal(1, reader.GetInt32(6));
            Assert.Equal("src/keep/Target.cs", reader.GetString(7));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImport_DryRunReportsBoundedDestinationDeltaWithoutReplacingDb_Issue4714()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("import_destination_delta");
        try
        {
            var sourceDb = TestProjectHelper.CreateProjectDb(Path.Combine(projectRoot, "source"));
            var destinationDb = TestProjectHelper.CreateProjectDb(Path.Combine(projectRoot, "destination"));
            TestProjectHelper.InsertIndexedFile(sourceDb, "src/Same.cs", "csharp", "public class Imported { void Run() { Run(); } }");
            TestProjectHelper.InsertIndexedFile(destinationDb, "src/Same.cs", "csharp", "public class Existing { void Run() { Run(); } }");
            var archivePath = ExportArchive(projectRoot, sourceDb);
            var destinationBefore = File.ReadAllBytes(destinationDb);
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport(
                    [archivePath, "--db", destinationDb, "--dry-run", "--limit", "1", "--offset", "0", "--json"],
                    jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(destinationBefore, File.ReadAllBytes(destinationDb));
            using var document = JsonDocument.Parse(stdout);
            var delta = document.RootElement.GetProperty("destination_delta");
            Assert.True(delta.GetProperty("destination_exists").GetBoolean());
            Assert.True(delta.GetProperty("comparable").GetBoolean());
            Assert.Equal("compared", delta.GetProperty("status").GetString());
            var comparison = delta.GetProperty("comparison");
            Assert.Equal(1, comparison.GetProperty("limit").GetInt32());
            Assert.Equal(0, comparison.GetProperty("offset").GetInt32());
            var record = Assert.Single(comparison.GetProperty("records").EnumerateArray());
            Assert.Equal("file", record.GetProperty("area").GetString());
            Assert.True(record.GetProperty("side").GetString() is "left" or "right");
            Assert.False(string.IsNullOrEmpty(record.GetProperty("identity_sha256").GetString()));
            Assert.Contains(
                record.GetProperty("fields").EnumerateArray(),
                field =>
                    field.GetProperty("name").GetString() == "path"
                    && field.GetProperty("redacted").GetBoolean());
            Assert.True(comparison.GetProperty("has_more").GetBoolean());
            Assert.Equal(1, comparison.GetProperty("returned_count").GetInt32());
            Assert.True(comparison.GetProperty("omitted_count").GetInt64() > 0);
            Assert.False(comparison.TryGetProperty("current_cursor", out _));
            Assert.False(comparison.TryGetProperty("next_cursor", out _));
            Assert.False(comparison.TryGetProperty("replay", out _));
            Assert.Equal(1, comparison.GetProperty("next_offset").GetInt32());
            Assert.DoesNotContain("public class Imported", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("public class Existing", stdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImport_DryRunIncludesCommittedWalRowsWithoutTouchingDestination_Issue4714()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("import_destination_wal_delta");
        try
        {
            var sourceDb = TestProjectHelper.CreateProjectDb(Path.Combine(projectRoot, "source"));
            TestProjectHelper.InsertIndexedFile(
                sourceDb,
                "src/Same.cs",
                "csharp",
                "public class Same { }",
                releasePoolForFileAccess: true);
            var archivePath = ExportArchive(projectRoot, sourceDb);
            var destinationDb = Path.Combine(projectRoot, "destination", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationDb)!);
            File.Copy(sourceDb, destinationDb);
            using var destinationConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationDb,
                Pooling = false,
            }.ConnectionString);
            destinationConnection.Open();
            using (var walMode = destinationConnection.CreateCommand())
            {
                walMode.CommandText = "PRAGMA journal_mode = WAL";
                Assert.Equal("wal", walMode.ExecuteScalar() as string);
            }
            using (var disableCheckpoint = destinationConnection.CreateCommand())
            {
                disableCheckpoint.CommandText = "PRAGMA wal_autocheckpoint = 0";
                disableCheckpoint.ExecuteNonQuery();
            }
            using (var insert = destinationConnection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO files(path, lang, size, lines, checksum)
                    VALUES ('src/WalOnly.cs', 'csharp', 1, 1, 'wal-only')
                    """;
                insert.ExecuteNonQuery();
            }
            var destinationBefore = ReadSqliteArtifactBytes(destinationDb);
            var walBefore = ReadSqliteArtifactBytes(destinationDb + "-wal");
            var shmBefore = ReadSqliteArtifactBytes(destinationDb + "-shm");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport(
                    [archivePath, "--db", destinationDb, "--dry-run", "--json"],
                    jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(destinationBefore, ReadSqliteArtifactBytes(destinationDb));
            Assert.Equal(walBefore, ReadSqliteArtifactBytes(destinationDb + "-wal"));
            Assert.Equal(shmBefore, ReadSqliteArtifactBytes(destinationDb + "-shm"));
            using var document = JsonDocument.Parse(stdout);
            var comparison = document.RootElement
                .GetProperty("destination_delta")
                .GetProperty("comparison");
            Assert.False(comparison.GetProperty("identical").GetBoolean());
            var expectedPathHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes("src/WalOnly.cs")))
                .ToLowerInvariant();
            Assert.Contains(
                comparison.GetProperty("records").EnumerateArray(),
                record =>
                    record.GetProperty("area").GetString() == "file"
                    && record.GetProperty("side").GetString() == "left"
                    && record.GetProperty("fields").EnumerateArray().Any(
                        field =>
                            field.GetProperty("name").GetString() == "path"
                            && field.GetProperty("sha256").GetString() == expectedPathHash
                            && field.GetProperty("redacted").GetBoolean()));
            Assert.DoesNotContain("src/WalOnly.cs", stdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImport_DryRunReportsUnreadableDestinationWithoutFailingArchiveValidation_Issue4714()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("import_destination_unreadable");
        try
        {
            var sourceDb = TestProjectHelper.CreateProjectDb(Path.Combine(projectRoot, "source"));
            var destinationDb = TestProjectHelper.CreateProjectDb(Path.Combine(projectRoot, "destination"));
            TestProjectHelper.InsertIndexedFile(sourceDb, "src/Source.cs", "csharp", "public class Source { }");
            var archivePath = ExportArchive(projectRoot, sourceDb);
            var originalMode = File.GetUnixFileMode(destinationDb);
            try
            {
                SqliteConnection.ClearAllPools();
                File.SetUnixFileMode(destinationDb, UnixFileMode.None);
                try
                {
                    using var probe = File.OpenRead(destinationDb);
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                }

                var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
                var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                    ExportImportCommandRunner.RunImport(
                        [archivePath, "--db", destinationDb, "--dry-run", "--json"],
                        jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                var delta = document.RootElement.GetProperty("destination_delta");
                Assert.False(delta.GetProperty("comparable").GetBoolean());
                Assert.Equal("destination_unreadable", delta.GetProperty("status").GetString());
            }
            finally
            {
                File.SetUnixFileMode(destinationDb, originalMode);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImport_DryRunTextReportsSchemaMismatchInsteadOfCountDeltas_Issue4714()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("import_destination_schema_mismatch");
        try
        {
            var sourceDb = TestProjectHelper.CreateProjectDb(Path.Combine(projectRoot, "source"));
            var destinationDb = TestProjectHelper.CreateProjectDb(Path.Combine(projectRoot, "destination"));
            TestProjectHelper.InsertIndexedFile(sourceDb, "src/Source.cs", "csharp", "public class Source { }");
            var archivePath = ExportArchive(projectRoot, sourceDb);
            using (var destinationConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationDb }.ConnectionString))
            {
                destinationConnection.Open();
                using var versionCommand = destinationConnection.CreateCommand();
                versionCommand.CommandText = "PRAGMA user_version = 0";
                versionCommand.ExecuteNonQuery();
            }
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport(
                    [archivePath, "--db", destinationDb, "--dry-run"],
                    jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("destination and archive schema versions differ", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("destination delta:", stdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_ManifestReportsEmptyUnknownExtensionSampleList_Issue3715()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_unknown_extensions_empty");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SetUnknownExtensionPathSamples(dbPath, []);

            using var document = ExportArchiveManifest(projectRoot, dbPath);
            var root = document.RootElement;

            Assert.Equal(0, root.GetProperty("unknown_extension_file_sample_count").GetInt32());
            Assert.Equal(DbContext.UnknownExtensionFilePathSampleLimit, root.GetProperty("unknown_extension_file_sample_limit").GetInt32());
            Assert.False(root.GetProperty("unknown_extension_file_sample_truncated").GetBoolean());
            Assert.True(
                !root.TryGetProperty("unknown_extension_files", out var files)
                || files.ValueKind == JsonValueKind.Null);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_ManifestReportsBoundedUnknownExtensionSampleList_Issue3715()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_unknown_extensions_bounded");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SetUnknownExtensionPathSamples(dbPath, ["tools/custom.foo", "docs/archive.bar"]);

            using var document = ExportArchiveManifest(projectRoot, dbPath);
            var root = document.RootElement;

            Assert.Equal(2, root.GetProperty("unknown_extension_file_sample_count").GetInt32());
            Assert.Equal(DbContext.UnknownExtensionFilePathSampleLimit, root.GetProperty("unknown_extension_file_sample_limit").GetInt32());
            Assert.False(root.GetProperty("unknown_extension_file_sample_truncated").GetBoolean());
            Assert.Equal("tools/custom.foo", root.GetProperty("unknown_extension_files")[0].GetString());
            Assert.Equal("docs/archive.bar", root.GetProperty("unknown_extension_files")[1].GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_ManifestReportsTruncatedUnknownExtensionSampleList_Issue3715()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_unknown_extensions_truncated");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sampleLimit = DbContext.UnknownExtensionFilePathSampleLimit;
            var paths = Enumerable
                .Range(0, sampleLimit + 3)
                .Select(index => $"samples/file-{index:D2}.unknown")
                .ToArray();
            SetUnknownExtensionPathSamples(dbPath, paths);

            using var document = ExportArchiveManifest(projectRoot, dbPath);
            var root = document.RootElement;
            var files = root.GetProperty("unknown_extension_files");

            Assert.Equal(paths.Length, root.GetProperty("unknown_extension_file_count").GetInt64());
            Assert.Equal(sampleLimit, root.GetProperty("unknown_extension_file_sample_count").GetInt32());
            Assert.Equal(sampleLimit, root.GetProperty("unknown_extension_file_sample_limit").GetInt32());
            Assert.True(root.GetProperty("unknown_extension_file_sample_truncated").GetBoolean());
            Assert.Equal(sampleLimit, files.GetArrayLength());
            Assert.Equal(paths[0], files[0].GetString());
            Assert.Equal(paths[sampleLimit - 1], files[sampleLimit - 1].GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_ManifestBoundsUnknownExtensionSampleStringsBeforeMaterializing_Issue3908()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_unknown_extensions_string_bounds");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longPath = new string('a', 5000) + ".unknown";
            SetUnknownExtensionPathSamples(dbPath, [longPath]);

            using var document = ExportArchiveManifest(projectRoot, dbPath);
            var root = document.RootElement;
            var exportedPath = root.GetProperty("unknown_extension_files")[0].GetString();

            Assert.Equal(ExportImportCommandRunner.ManifestUnknownExtensionPathCharLimit, exportedPath?.Length);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_ManifestBoundsUnknownExtensionSampleItemsBeforeMaterializing_Issue3908()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_unknown_extensions_item_bounds");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var itemLimit = ExportImportCommandRunner.ManifestUnknownExtensionDecodedItemLimit;
            var paths = Enumerable
                .Range(0, itemLimit + 3)
                .Select(index => $"samples/file-{index:D3}.unknown")
                .ToArray();
            SetUnknownExtensionPathSamples(dbPath, paths);

            using var document = ExportArchiveManifest(projectRoot, dbPath);
            var root = document.RootElement;
            var files = root.GetProperty("unknown_extension_files");

            Assert.Equal(DbContext.UnknownExtensionFilePathSampleLimit, files.GetArrayLength());
            Assert.True(root.GetProperty("unknown_extension_file_sample_truncated").GetBoolean());
            Assert.Equal(paths[DbContext.UnknownExtensionFilePathSampleLimit - 1], files[files.GetArrayLength() - 1].GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImport_DryRunJsonReportsUnknownExtensionSampleMetadata_Issue3715()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("import_unknown_extensions_metadata");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sampleLimit = DbContext.UnknownExtensionFilePathSampleLimit;
            var paths = Enumerable
                .Range(0, sampleLimit + 2)
                .Select(index => $"imports/file-{index:D2}.unknown")
                .ToArray();
            SetUnknownExtensionPathSamples(sourceDbPath, paths);
            var archivePath = ExportArchive(projectRoot, sourceDbPath);
            var importDbPath = Path.Combine(projectRoot, "imported", "codeindex.db");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", importDbPath, "--dry-run", "--json"], jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var files = root.GetProperty("unknown_extension_files");
            Assert.Equal(paths.Length, root.GetProperty("unknown_extension_file_count").GetInt64());
            Assert.Equal(sampleLimit, root.GetProperty("unknown_extension_file_sample_count").GetInt32());
            Assert.Equal(sampleLimit, root.GetProperty("unknown_extension_file_sample_limit").GetInt32());
            Assert.True(root.GetProperty("unknown_extension_file_sample_truncated").GetBoolean());
            Assert.Equal(sampleLimit, files.GetArrayLength());
            Assert.Equal(paths[sampleLimit - 1], files[sampleLimit - 1].GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void CreateDatabaseSnapshot_AppliesPrivateFileMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("export_snapshot_mode");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var snapshotPath = Path.Combine(projectRoot, "snapshot.db");

            ExportImportCommandRunner.CreateDatabaseSnapshot(sourceDbPath, snapshotPath, CancellationToken.None);

            var mode = File.GetUnixFileMode(snapshotPath) & DataDirectorySecurity.PermissionBits;
            Assert.Equal(DataDirectorySecurity.PrivateFileMode, mode);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportCtags_FailureOmitsRawExceptionMessage()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ctags_error_sanitize");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var outputDirectory = Path.Combine(projectRoot, "tags-output");
            Directory.CreateDirectory(outputDirectory);

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(["ctags", "--db", dbPath, "--output", outputDirectory], new JsonSerializerOptions(), "test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("ctags export failed (", stderr);
            Assert.DoesNotContain(outputDirectory, stderr);
            Assert.DoesNotContain(Path.GetFileName(outputDirectory), stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void WriteExportArchiveFile_FailurePreservesExistingArchive()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_export");
        try
        {
            var outputPath = Path.Combine(workDir, "codeindex.cdidx.zip");
            File.WriteAllText(outputPath, "existing archive");
            var missingSnapshotPath = Path.Combine(workDir, "missing.db");
            var manifest = new ExportImportCommandRunner.ExportManifest(
                "1",
                "test",
                0,
                null,
                null,
                new string('0', 64));

            Assert.Throws<FileNotFoundException>(() =>
                ExportImportCommandRunner.WriteExportArchiveFile(
                    outputPath,
                    missingSnapshotPath,
                    manifest,
                    new JsonSerializerOptions(),
                    CancellationToken.None));

            Assert.Equal("existing archive", File.ReadAllText(outputPath));
            Assert.Single(Directory.GetFiles(workDir));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void WriteExportArchiveFile_FailureLeavesNoArtifactOrTemporaryFile_Issue4827()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_export_failed_cleanup");
        try
        {
            var outputPath = Path.Combine(workDir, "codeindex.cdidx.zip");
            var missingSnapshotPath = Path.Combine(workDir, "missing.db");
            var manifest = new ExportImportCommandRunner.ExportManifest(
                "1",
                "test",
                0,
                null,
                null,
                new string('0', 64));

            Assert.Throws<FileNotFoundException>(() =>
                ExportImportCommandRunner.WriteExportArchiveFile(
                    outputPath,
                    missingSnapshotPath,
                    manifest,
                    new JsonSerializerOptions(),
                    CancellationToken.None,
                    overwrite: false));

            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.GetFiles(workDir));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void WriteExportArchiveFile_CanSkipArtifactMetadataForHumanOutput_Issue4827()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_export_without_attestation");
        try
        {
            var snapshotPath = TestProjectHelper.CreateProjectDb(workDir);
            SqliteConnection.ClearAllPools();
            var outputPath = Path.Combine(workDir, "codeindex.cdidx.zip");
            var manifest = new ExportImportCommandRunner.ExportManifest(
                "1",
                "test",
                0,
                null,
                null,
                new string('0', 64));

            var artifact = ExportImportCommandRunner.WriteExportArchiveFile(
                outputPath,
                snapshotPath,
                manifest,
                new JsonSerializerOptions(),
                CancellationToken.None,
                overwrite: false,
                includeArtifactMetadata: false);

            Assert.Null(artifact);
            Assert.True(File.Exists(outputPath));
            Assert.Empty(Directory.GetFiles(workDir, ".cdidx-*.tmp"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void WriteExportArchiveFile_ConcurrentDestinationCreationPreservesWinner_Issue4827()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_export_concurrent_destination");
        try
        {
            var snapshotPath = TestProjectHelper.CreateProjectDb(workDir);
            SqliteConnection.ClearAllPools();
            var outputPath = Path.Combine(workDir, "codeindex.cdidx.zip");
            var manifest = new ExportImportCommandRunner.ExportManifest(
                "1",
                "test",
                0,
                null,
                null,
                new string('0', 64));

            Assert.Throws<AtomicFileWriter.DestinationAlreadyExistsException>(() =>
                ExportImportCommandRunner.WriteExportArchiveFile(
                    outputPath,
                    snapshotPath,
                    manifest,
                    new JsonSerializerOptions(),
                    CancellationToken.None,
                    overwrite: false,
                    beforePublishForTesting: () => File.WriteAllText(outputPath, "concurrent winner")));

            Assert.Equal("concurrent winner", File.ReadAllText(outputPath));
            Assert.Empty(Directory.GetFiles(workDir, ".cdidx-*.tmp"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void WriteCtagsFile_FailurePreservesExistingTagfile()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_ctags");
        try
        {
            var outputPath = Path.Combine(workDir, "tags");
            File.WriteAllText(outputPath, "existing tags");

            Assert.Throws<IOException>(() =>
                ExportImportCommandRunner.WriteCtagsFile(
                    outputPath,
                    writer =>
                    {
                        writer.WriteLine("partial");
                        throw new IOException("simulated ctags failure");
                    }));

            Assert.Equal("existing tags", File.ReadAllText(outputPath));
            Assert.Single(Directory.GetFiles(workDir));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void WriteCtagsFile_RelativeOutputUsesInitialFullPathWhenCurrentDirectoryChanges_Issue3138()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var workDir = TestProjectHelper.CreateTempProject("ctags_full_output");
        var driftDir = TestProjectHelper.CreateTempProject("ctags_full_output_drift");
        try
        {
            Directory.SetCurrentDirectory(workDir);

            ExportImportCommandRunner.WriteCtagsFile(
                "tags",
                writer =>
                {
                    Directory.SetCurrentDirectory(driftDir);
                    writer.WriteLine("!_TAG_FILE_FORMAT\t2\t/extended format/");
                });

            Assert.True(File.Exists(Path.Combine(workDir, "tags")));
            Assert.False(File.Exists(Path.Combine(driftDir, "tags")));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            TestProjectHelper.DeleteDirectory(workDir);
            TestProjectHelper.DeleteDirectory(driftDir);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_MoveFailurePreservesExistingSidecars()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_import");
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(dbPath + "-wal", "existing wal");
            File.WriteAllText(dbPath + "-shm", "existing shm");
            var missingTempPath = Path.Combine(workDir, "missing.db");

            Assert.ThrowsAny<IOException>(() =>
                ExportImportCommandRunner.ReplaceImportedDatabase(missingTempPath, dbPath, CancellationToken.None));

            Assert.Equal("existing db", File.ReadAllText(dbPath));
            Assert.Equal("existing wal", File.ReadAllText(dbPath + "-wal"));
            Assert.Equal("existing shm", File.ReadAllText(dbPath + "-shm"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_SuccessDeletesDestinationSidecarsAfterMove()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_import");
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var tempPath = Path.Combine(workDir, "staged.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(dbPath + "-wal", "existing wal");
            File.WriteAllText(dbPath + "-shm", "existing shm");
            File.WriteAllText(tempPath, "imported db");

            ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath, CancellationToken.None);

            Assert.Equal("imported db", File.ReadAllText(dbPath));
            Assert.False(File.Exists(dbPath + "-wal"));
            Assert.False(File.Exists(dbPath + "-shm"));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_SidecarBackupCleanupFailureLeavesReplacementActive_Issue3410()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_import_cleanup");
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var tempPath = Path.Combine(workDir, "staged.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(dbPath + "-wal", "existing wal");
            File.WriteAllText(dbPath + "-shm", "existing shm");
            File.WriteAllText(tempPath, "imported db");
            ExportImportCommandRunner.DeleteSqliteSidecarForTesting = _ => throw new IOException("simulated sidecar cleanup failure");

            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath, CancellationToken.None);
                return 0;
            });

            Assert.Equal("imported db", File.ReadAllText(dbPath));
            Assert.False(File.Exists(tempPath));
            Assert.False(File.Exists(dbPath + "-wal"));
            Assert.False(File.Exists(dbPath + "-shm"));
            Assert.Contains("Warning: failed to delete import replaced database sidecar backup", stderr);
        }
        finally
        {
            ExportImportCommandRunner.DeleteSqliteSidecarForTesting = null;
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_PostMoveFailureRollsBackDestination_Issue3410()
    {
        var workDir = TestProjectHelper.CreateTempProject("cdidx_import_rollback");
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var tempPath = Path.Combine(workDir, "staged.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(dbPath + "-wal", "existing wal");
            File.WriteAllText(dbPath + "-shm", "existing shm");
            File.WriteAllText(tempPath, "imported db");
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = _ =>
                throw new IOException("simulated post-move failure");

            var ex = Assert.ThrowsAny<IOException>(() =>
                ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath, CancellationToken.None));

            Assert.Contains("rolled back", ex.Message, StringComparison.Ordinal);
            Assert.Equal("existing db", File.ReadAllText(dbPath));
            Assert.Equal("existing wal", File.ReadAllText(dbPath + "-wal"));
            Assert.Equal("existing shm", File.ReadAllText(dbPath + "-shm"));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = null;
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_AppliesPrivateFileMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workDir = TestProjectHelper.CreateTempProject("cdidx_import_mode");
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var tempPath = Path.Combine(workDir, "staged.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(tempPath, "imported db");
            File.SetUnixFileMode(tempPath, DataDirectorySecurity.PermissionBits);

            ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath, CancellationToken.None);

            var mode = File.GetUnixFileMode(dbPath) & DataDirectorySecurity.PermissionBits;
            Assert.Equal(DataDirectorySecurity.PrivateFileMode, mode);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void TryValidateDatabaseEntrySize_RejectsOversizedUncompressedLength()
    {
        var ok = ExportImportCommandRunner.TryValidateDatabaseEntrySize(
            uncompressedLength: ExportImportCommandRunner.MaxImportDatabaseBytes + 1,
            compressedLength: 1,
            message: out var message);

        Assert.False(ok);
        Assert.Contains("uncompressed exceeds the import limit", message);
    }

    [Fact]
    public void TryValidateDatabaseEntrySize_RejectsOversizedCompressedLength()
    {
        var ok = ExportImportCommandRunner.TryValidateDatabaseEntrySize(
            uncompressedLength: 1,
            compressedLength: ExportImportCommandRunner.MaxImportDatabaseBytes + 1,
            message: out var message);

        Assert.False(ok);
        Assert.Contains("compressed exceeds the import limit", message);
    }

    [Fact]
    public void CopyToWithLimit_ThrowsBeforeWritingPastLimit()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var target = new MemoryStream();

        var ex = Assert.Throws<InvalidDataException>(() => ExportImportCommandRunner.CopyToWithLimit(source, target, maxBytes: 3));

        Assert.Contains("codeindex.db exceeds the import limit", ex.Message);
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public void CopyToWithLimit_AllowsExactLimit()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var target = new MemoryStream();

        var copied = ExportImportCommandRunner.CopyToWithLimit(source, target, maxBytes: 4);

        Assert.Equal(4, copied);
        Assert.Equal([1, 2, 3, 4], target.ToArray());
    }

    [Fact]
    public void CopyToExactLength_ThrowsBeforeWritingPastCapturedLength_Issue3994()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var target = new MemoryStream();

        var ex = Assert.Throws<InvalidDataException>(() =>
            ExportImportCommandRunner.CopyToExactLength(source, target, expectedBytes: 3, "codeindex.db"));

        Assert.Contains("source grew beyond the expected snapshot length", ex.Message);
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public void CopyToExactLength_ThrowsWhenSourceEndsBeforeCapturedLength_Issue3994()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var target = new MemoryStream();

        var ex = Assert.Throws<EndOfStreamException>(() =>
            ExportImportCommandRunner.CopyToExactLength(source, target, expectedBytes: 5, "codeindex.db"));

        Assert.Contains("source ended after", ex.Message);
        Assert.Equal([1, 2, 3, 4], target.ToArray());
    }

    [Fact]
    public void Sha256StreamHasher_CancellationDuringRead_ThrowsOperationCanceled_Issue3797()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelAfterFirstReadStream([1, 2, 3, 4], cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            Sha256StreamHasher.ComputeHex(stream, cancellation.Token));
    }

    [Fact]
    public void CopyToWithLimit_CancellationDuringRead_ThrowsOperationCanceled_Issue3797()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new CancelAfterFirstReadStream([1, 2, 3, 4], cancellation);
        using var target = new MemoryStream();

        Assert.Throws<OperationCanceledException>(() =>
            ExportImportCommandRunner.CopyToWithLimit(source, target, maxBytes: 8, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void CopyToWithLimit_ReportsProgressAndHonorsCancellation_Issue3766()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var target = new MemoryStream();
        var progress = new RecordingProgress();

        var copied = ExportImportCommandRunner.CopyToWithLimit(source, target, 4, CancellationToken.None, progress);

        Assert.Equal(4, copied);
        Assert.Equal([1, 2, 3, 4], target.ToArray());
        Assert.Equal([4L], progress.Values);

        using var canceledSource = new MemoryStream([5]);
        using var canceledTarget = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ExportImportCommandRunner.CopyToWithLimit(canceledSource, canceledTarget, 1, cts.Token));
        Assert.Equal(0, canceledTarget.Length);
    }

    private static string CreateArchiveWithManifest(string workDir, string manifest)
    {
        return CreateArchiveWithTextEntries(workDir, "codeindex.cdidx.zip", ("manifest.json", manifest));
    }

    private static string CreateArchiveWithManifestAndDatabase(string workDir, string manifest, byte[] databaseBytes)
    {
        var archivePath = Path.Combine(workDir, "codeindex-with-db.cdidx.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifestEntry.Open()))
            writer.Write(manifest);

        var databaseEntry = archive.CreateEntry("codeindex.db");
        using (var stream = databaseEntry.Open())
            stream.Write(databaseBytes, 0, databaseBytes.Length);

        return archivePath;
    }

    private static string CreateArchiveWithTextEntries(
        string workDir,
        string archiveFileName,
        params (string EntryName, string Content)[] entries)
    {
        var archivePath = Path.Combine(workDir, archiveFileName);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return archivePath;
    }

    private static JsonDocument ExportArchiveManifest(string projectRoot, string dbPath)
    {
        var archivePath = ExportArchive(projectRoot, dbPath);
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("manifest.json entry was not found");
        using var stream = manifestEntry.Open();
        return JsonDocument.Parse(stream);
    }

    private static string ExportArchive(string projectRoot, string dbPath)
    {
        var archivePath = Path.Combine(projectRoot, $"codeindex-{Guid.NewGuid():N}.cdidx.zip");
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ExportImportCommandRunner.RunExport([archivePath, "--db", dbPath], new JsonSerializerOptions(), "test"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Contains("Exported CodeIndex archive", stdout);
        Assert.Equal(string.Empty, stderr);
        return archivePath;
    }

    private static void SetUnknownExtensionPathSamples(string dbPath, string[] paths)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.SetMeta(DbContext.UnknownExtensionFileCountMetaKey, paths.Length.ToString(CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.UnknownExtensionFilePathsMetaKey, JsonSerializer.Serialize(paths));
        writer.SetMeta(DbContext.UnknownExtensionFilesTruncatedMetaKey, bool.FalseString);
        writer.SetMeta(
            DbContext.UnknownExtensionFilePathLimitMetaKey,
            DbContext.UnknownExtensionFilePathSampleLimit.ToString(CultureInfo.InvariantCulture));
    }

    private static void AssertExportImportError(string stdout, string command, string phase, string errorCode)
    {
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("1", root.GetProperty("api_version").GetString());
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.Equal(phase, root.GetProperty("phase").GetString());
        Assert.Equal(errorCode, root.GetProperty("error_code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("hint").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("usage").GetString()));
    }

    private static string? ReadMetaValue(string dbPath, string key)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    private static byte[] ReadSqliteArtifactBytes(string path)
    {
        using var stream = BoundedFile.OpenReadForIndexContent(path);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private sealed class CancelAfterFirstReadStream(byte[] data, CancellationTokenSource cancellation) : Stream
    {
        private int offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int bufferOffset, int count)
        {
            if (offset >= data.Length)
                return 0;

            var read = Math.Min(count, data.Length - offset);
            Array.Copy(data, offset, buffer, bufferOffset, read);
            offset += read;
            cancellation.Cancel();
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long seekOffset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int bufferOffset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingProgress : IProgress<long>
    {
        public List<long> Values { get; } = [];

        public void Report(long value) => Values.Add(value);
    }
}
