using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
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
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_manifest_json_error_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
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
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void RunImport_JsonSqliteValidationErrorIncludesPhaseAndCode_Issue3548()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_import_sqlite_json_error_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
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
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void RunImport_RejectsOversizedManifestBeforeDatabaseEntry()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_manifest_size_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
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
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void RunImport_RejectsDeepManifestBeforeDatabaseEntry()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_manifest_depth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
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
            Directory.Delete(workDir, recursive: true);
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
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_ctags_missing_db_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
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
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void RunExportCtags_JsonReportsFiltersAndMetadata_Issue3551()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ctags_json_filters");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "public class App { public void Run() {} }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "tests/AppTests.cs", "csharp", "public class AppTests { public void Run() {} }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Generated.cs", "csharp", "public class Generated { }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/tool.py", "python", "def run():\n    pass\n");
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
                        "src/Generated*",
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
            Assert.Equal("src/Generated*", filters.GetProperty("exclude_path")[0].GetString());
            Assert.True(filters.GetProperty("exclude_tests").GetBoolean());
            Assert.Contains(root.GetProperty("metadata_fields").EnumerateArray(), field => field.GetString() == "language");

            var tags = File.ReadAllText(outputPath);
            Assert.Contains("App\tsrc/App.cs", tags);
            Assert.Contains("language:csharp", tags);
            Assert.DoesNotContain("AppTests", tags);
            Assert.DoesNotContain("Generated", tags);
            Assert.DoesNotContain("tool.py", tags);
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
        PathCasing.ResetCacheForTests();
        var projectRoot = TestProjectHelper.CreateTempProject("export_path_case_moved_stamp");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(dbPath))
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
            using (var db = new DbContext(dbPath))
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
                ExportImportCommandRunner.RunExport([outputDirectory, "--db", dbPath], new JsonSerializerOptions(), "test"));

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

            ExportImportCommandRunner.CreateDatabaseSnapshot(sourceDbPath, snapshotPath);

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
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
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
                    new JsonSerializerOptions()));

            Assert.Equal("existing archive", File.ReadAllText(outputPath));
            Assert.Single(Directory.GetFiles(workDir));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void WriteCtagsFile_FailurePreservesExistingTagfile()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_ctags_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
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
            Directory.Delete(workDir, recursive: true);
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
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(dbPath + "-wal", "existing wal");
            File.WriteAllText(dbPath + "-shm", "existing shm");
            var missingTempPath = Path.Combine(workDir, "missing.db");

            Assert.ThrowsAny<IOException>(() =>
                ExportImportCommandRunner.ReplaceImportedDatabase(missingTempPath, dbPath));

            Assert.Equal("existing db", File.ReadAllText(dbPath));
            Assert.Equal("existing wal", File.ReadAllText(dbPath + "-wal"));
            Assert.Equal("existing shm", File.ReadAllText(dbPath + "-shm"));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_SuccessDeletesDestinationSidecarsAfterMove()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var tempPath = Path.Combine(workDir, "staged.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(dbPath + "-wal", "existing wal");
            File.WriteAllText(dbPath + "-shm", "existing shm");
            File.WriteAllText(tempPath, "imported db");

            ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath);

            Assert.Equal("imported db", File.ReadAllText(dbPath));
            Assert.False(File.Exists(dbPath + "-wal"));
            Assert.False(File.Exists(dbPath + "-shm"));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_SidecarBackupCleanupFailureLeavesReplacementActive_Issue3410()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_import_cleanup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
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
                ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath);
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
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_PostMoveFailureRollsBackDestination_Issue3410()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_import_rollback_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
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

            var ex = Assert.Throws<IOException>(() =>
                ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath));

            Assert.Contains("rolled back", ex.Message, StringComparison.Ordinal);
            Assert.Equal("existing db", File.ReadAllText(dbPath));
            Assert.Equal("existing wal", File.ReadAllText(dbPath + "-wal"));
            Assert.Equal("existing shm", File.ReadAllText(dbPath + "-shm"));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = null;
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_AppliesPrivateFileMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_import_mode_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var tempPath = Path.Combine(workDir, "staged.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(tempPath, "imported db");
            File.SetUnixFileMode(tempPath, DataDirectorySecurity.PermissionBits);

            ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath);

            var mode = File.GetUnixFileMode(dbPath) & DataDirectorySecurity.PermissionBits;
            Assert.Equal(DataDirectorySecurity.PrivateFileMode, mode);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
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

    private static string CreateArchiveWithManifest(string workDir, string manifest)
    {
        var archivePath = Path.Combine(workDir, "codeindex.cdidx.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("manifest.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(manifest);
        return archivePath;
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
        using var db = new DbContext(dbPath);
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
}
