using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class ExportImportCommandRunnerIssue5195Tests
{
    [Fact]
    public void RunExportArchive_DefaultPreservesPathCompatibility_Issue5195()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_default_paths_5195");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var archivePath = Path.Combine(projectRoot, "default.cdidx.zip");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [archivePath, "--db", dbPath, "--json"],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var result = JsonDocument.Parse(stdout);
            Assert.Equal(Path.GetFullPath(archivePath), result.RootElement.GetProperty("archive_path").GetString());
            Assert.Equal(Path.GetFullPath(dbPath), result.RootElement.GetProperty("db_path").GetString());
            Assert.False(result.RootElement.GetProperty("path_redaction_requested").GetBoolean());
            Assert.False(result.RootElement.GetProperty("path_redaction_complete").GetBoolean());
            Assert.Empty(result.RootElement.GetProperty("path_redaction_omitted_categories").EnumerateArray());
            Assert.Equal(
                Path.GetFullPath(projectRoot),
                result.RootElement.GetProperty("manifest").GetProperty("project_root").GetString());

            var extractedDb = ExtractDatabase(projectRoot, archivePath, "default.db");
            Assert.Equal(Path.GetFullPath(projectRoot), ReadMetaValue(extractedDb, DbContext.IndexedProjectRootMetaKey));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_RedactsManifestSnapshotAndOutputAndImports_Issue5195()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_redacted_paths_5195");
        var importRoot = TestProjectHelper.CreateTempProject("import_redacted_paths_5195");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "public class App { public void Run() { } }");
            const string posixSample = "/Users/alice/work/private-repository/secret.foo";
            const string windowsSample = @"C:\Users\Alice\work\private-repository\secret.bar";
            SetUnknownExtensionPaths(dbPath, [posixSample, windowsSample, "docs/relative.baz"]);
            SetWorkspaceVerificationPendingPaths(dbPath, [posixSample, "src/App.cs"]);
            SqliteConnection.ClearAllPools();
            var sourceBytesBefore = File.ReadAllBytes(dbPath);
            var archivePath = Path.Combine(projectRoot, "redacted.cdidx.zip");
            File.WriteAllText(archivePath, "replace me");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [archivePath, "--db", dbPath, "--overwrite", "--redact-paths", "--json"],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.DoesNotContain(projectRoot, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(dbPath, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(archivePath, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(posixSample, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(windowsSample, stdout, StringComparison.Ordinal);
            using var result = JsonDocument.Parse(stdout);
            Assert.Equal("[redacted]", result.RootElement.GetProperty("archive_path").GetString());
            Assert.Equal("[redacted]", result.RootElement.GetProperty("db_path").GetString());
            Assert.True(result.RootElement.GetProperty("path_redaction_requested").GetBoolean());
            Assert.True(result.RootElement.GetProperty("path_redaction_complete").GetBoolean());
            var manifest = result.RootElement.GetProperty("manifest");
            Assert.Equal(JsonValueKind.Null, manifest.GetProperty("project_root").ValueKind);
            Assert.True(manifest.GetProperty("path_redaction_requested").GetBoolean());
            Assert.True(manifest.GetProperty("path_redaction_complete").GetBoolean());
            Assert.Contains(
                manifest.GetProperty("path_redaction_omitted_categories").EnumerateArray(),
                category => category.GetString() == "project_root");
            Assert.Contains(
                manifest.GetProperty("path_redaction_omitted_categories").EnumerateArray(),
                category => category.GetString() == "unknown_extension_files");
            Assert.Contains(
                manifest.GetProperty("path_redaction_omitted_categories").EnumerateArray(),
                category => category.GetString() == "unknown_extension_groups");
            Assert.Equal("[redacted]", manifest.GetProperty("unknown_extension_files")[0].GetString());
            Assert.Equal("[redacted]", manifest.GetProperty("unknown_extension_files")[1].GetString());
            Assert.Equal("docs/relative.baz", manifest.GetProperty("unknown_extension_files")[2].GetString());

            var extractedDb = ExtractDatabase(projectRoot, archivePath, "redacted.db");
            Assert.Null(ReadMetaValue(extractedDb, DbContext.IndexedProjectRootMetaKey));
            using (var paths = JsonDocument.Parse(ReadMetaValue(extractedDb, DbContext.UnknownExtensionFilePathsMetaKey)!))
            {
                Assert.Equal("[redacted]", paths.RootElement[0].GetString());
                Assert.Equal("[redacted]", paths.RootElement[1].GetString());
                Assert.Equal("docs/relative.baz", paths.RootElement[2].GetString());
            }
            var groups = UnknownExtensionClassifier.DeserializeGroups(
                ReadMetaValue(extractedDb, DbContext.UnknownExtensionGroupsMetaKey));
            Assert.NotNull(groups);
            var groupedSamples = groups.SelectMany(group => group.SamplePaths).ToArray();
            Assert.DoesNotContain(posixSample, groupedSamples);
            Assert.DoesNotContain(windowsSample, groupedSamples);
            Assert.Equal(2, groupedSamples.Count(path => path == "[redacted]"));
            Assert.Contains("docs/relative.baz", groupedSamples);
            Assert.DoesNotContain(projectRoot, Encoding.UTF8.GetString(File.ReadAllBytes(extractedDb)), StringComparison.Ordinal);
            var pendingPaths = JsonStringListCodec.Deserialize(
                ReadMetaValue(extractedDb, DbContext.WorkspaceVerificationPendingPathsMetaKey));
            Assert.NotNull(pendingPaths);
            Assert.Equal(["[redacted]", "src/App.cs"], pendingPaths);
            Assert.Equal(
                bool.FalseString,
                ReadMetaValue(extractedDb, DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(extractedDb))).ToLowerInvariant(),
                manifest.GetProperty("database_sha256").GetString());
            Assert.Equal(sourceBytesBefore, File.ReadAllBytes(dbPath));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    DataDirectorySecurity.PrivateFileMode,
                    File.GetUnixFileMode(archivePath) & DataDirectorySecurity.PermissionBits);
            }

            var humanArchivePath = Path.Combine(projectRoot, "redacted-human.cdidx.zip");
            var humanExport = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [humanArchivePath, "--db", dbPath, "--redact-paths"],
                    jsonOptions,
                    "test"));
            Assert.Equal(CommandExitCodes.Success, humanExport.ExitCode);
            Assert.Equal(string.Empty, humanExport.Stderr);
            Assert.Equal("Exported path-redacted CodeIndex archive." + Environment.NewLine, humanExport.Stdout);
            Assert.DoesNotContain(projectRoot, humanExport.Stdout, StringComparison.Ordinal);

            var importedDb = Path.Combine(importRoot, ".cdidx", "codeindex.db");
            var dryRun = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport(
                    [archivePath, "--db", importedDb, "--dry-run", "--no-backup", "--json"],
                    jsonOptions));
            Assert.Equal(CommandExitCodes.Success, dryRun.ExitCode);
            Assert.Equal(string.Empty, dryRun.Stderr);
            using (var dryRunJson = JsonDocument.Parse(dryRun.Stdout))
            {
                Assert.True(dryRunJson.RootElement.GetProperty("path_redaction_requested").GetBoolean());
                Assert.True(dryRunJson.RootElement.GetProperty("path_redaction_complete").GetBoolean());
            }
            Assert.False(File.Exists(importedDb));

            var import = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport(
                    [archivePath, "--db", importedDb, "--no-backup", "--json"],
                    jsonOptions));
            Assert.Equal(CommandExitCodes.Success, import.ExitCode);
            Assert.Equal(string.Empty, import.Stderr);
            Assert.Null(ReadMetaValue(importedDb, DbContext.IndexedProjectRootMetaKey));

            var query = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    ["symbols", "App", "--db", importedDb, "--json", "--limit", "10"],
                    appVersion: "test"));
            Assert.Equal(CommandExitCodes.Success, query.ExitCode);
            Assert.Equal(string.Empty, query.Stderr);
            Assert.Contains("src/App.cs", query.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(importRoot);
        }
    }

    [Fact]
    public void RunExportArchive_RedactsLargeListsAndDeletesUnsafePathMetadata_Issue5195()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_unsafe_path_metadata_5195");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var largePaths = Enumerable.Range(0, 400)
                .Select(index => @"C:\" + new string('\\', 120) + $"secret-{index}.foo")
                .ToArray();
            var largePathsJson = JsonStringListCodec.Serialize(largePaths);
            Assert.True(largePathsJson.Length > 64 * 1024);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.UnknownExtensionFilePathsMetaKey, largePathsJson);
                writer.SetMeta(
                    DbContext.UnknownExtensionGroupsMetaKey,
                    "[{\"extension\":\".foo\",\"sample_paths\":[\"/Users/alice/group-secret.foo\"]");
                writer.SetMeta(
                    DbContext.WorkspaceVerificationPendingPathsMetaKey,
                    JsonStringListCodec.Serialize(
                        ["/" + new string('x', JsonStringListCodec.MaxRawJsonCharacters)]));
                writer.SetMeta(
                    DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey,
                    bool.TrueString);
            }
            SqliteConnection.ClearAllPools();

            var archivePath = Path.Combine(projectRoot, "unsafe-metadata.cdidx.zip");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [archivePath, "--db", dbPath, "--redact-paths", "--json"],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var result = JsonDocument.Parse(stdout);
            Assert.True(result.RootElement.GetProperty("path_redaction_complete").GetBoolean());
            var categories = result.RootElement
                .GetProperty("path_redaction_omitted_categories")
                .EnumerateArray();
            Assert.Contains(categories, category => category.GetString() == "unknown_extension_files");
            Assert.Contains(categories, category => category.GetString() == "unknown_extension_groups");
            Assert.Contains(categories, category => category.GetString() == "workspace_pending_paths");

            var extractedDb = ExtractDatabase(projectRoot, archivePath, "unsafe-metadata.db");
            var redactedPaths = JsonStringListCodec.Deserialize(
                ReadMetaValue(extractedDb, DbContext.UnknownExtensionFilePathsMetaKey));
            Assert.NotNull(redactedPaths);
            Assert.Equal(largePaths.Length, redactedPaths.Count);
            Assert.All(redactedPaths, path => Assert.Equal("[redacted]", path));
            Assert.Null(ReadMetaValue(extractedDb, DbContext.UnknownExtensionGroupsMetaKey));
            Assert.Empty(JsonStringListCodec.Deserialize(
                ReadMetaValue(extractedDb, DbContext.WorkspaceVerificationPendingPathsMetaKey))!);
            Assert.Equal(
                bool.FalseString,
                ReadMetaValue(extractedDb, DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey));
            var extractedBytes = Encoding.UTF8.GetString(File.ReadAllBytes(extractedDb));
            Assert.DoesNotContain("group-secret.foo", extractedBytes, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-0.foo", extractedBytes, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportArchive_RedactsAbsoluteScopeFormsAndKeepsResolvedRelativePaths_Issue5195()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_redacted_scope_5195");
        try
        {
            var projectPath = TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/App/App.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            TestProjectHelper.WriteTextFile(projectRoot, "src/App/App.cs", "public class App { }");
            var solutionPath = TestProjectHelper.WriteTextFile(
                projectRoot,
                "App.sln",
                "Project(\"{11111111-1111-1111-1111-111111111111}\") = \"App\", \"src/App/App.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\nEndProject\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App/App.cs", "csharp", "public class App { }");
            var archivePath = Path.Combine(projectRoot, "scoped.cdidx.zip");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [
                        archivePath,
                        "--db", dbPath,
                        "--project", $" {Path.GetFullPath(projectPath)} ",
                        "--solution", Path.GetFullPath(solutionPath),
                        "--path", "/Users/alice/work/private-repository/**",
                        "--exclude-path", @"C:\Users\Alice\work\private-repository\**",
                        "--redact-paths",
                        "--json",
                    ],
                    jsonOptions,
                    "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.DoesNotContain(projectRoot, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("/Users/alice", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\Users\Alice", stdout, StringComparison.Ordinal);
            using var result = JsonDocument.Parse(stdout);
            var scope = result.RootElement.GetProperty("scope");
            Assert.True(scope.GetProperty("scoped").GetBoolean());
            Assert.Equal("[redacted]", scope.GetProperty("project")[0].GetString());
            Assert.Equal("[redacted]", scope.GetProperty("solution").GetString());
            Assert.Equal("[redacted]", scope.GetProperty("path")[0].GetString());
            Assert.Equal("[redacted]", scope.GetProperty("exclude_path")[0].GetString());
            Assert.Equal("src/App/*", scope.GetProperty("resolved_project_path")[0].GetString());
            Assert.Equal(1, scope.GetProperty("exported_file_count").GetInt64());
            var categories = result.RootElement.GetProperty("path_redaction_omitted_categories").EnumerateArray();
            Assert.Contains(categories, category => category.GetString() == "scope.project");
            Assert.Contains(categories, category => category.GetString() == "scope.solution");
            Assert.Contains(categories, category => category.GetString() == "scope.path");
            Assert.Contains(categories, category => category.GetString() == "scope.exclude_path");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImport_RejectsUnverifiedCompletedRedactionClaim_Issue5195()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("import_unverified_redaction_5195");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var archivePath = Path.Combine(projectRoot, "unverified-redaction.cdidx.zip");
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var export = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    [archivePath, "--db", dbPath, "--json"],
                    jsonOptions,
                    "test"));
            Assert.Equal(CommandExitCodes.Success, export.ExitCode);

            RewriteManifest(archivePath, manifest =>
            {
                manifest["project_root"] = null;
                manifest["path_redaction_requested"] = true;
                manifest["path_redaction_complete"] = true;
                manifest["path_redaction_omitted_categories"] = new JsonArray("project_root");
            });

            var importedDb = Path.Combine(projectRoot, "forged-import.db");
            var import = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport(
                    [archivePath, "--db", importedDb, "--dry-run", "--no-backup", "--json"],
                    jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, import.ExitCode);
            Assert.Equal(string.Empty, import.Stderr);
            using var error = JsonDocument.Parse(import.Stdout);
            Assert.Equal("sqlite_validate", error.RootElement.GetProperty("phase").GetString());
            Assert.Equal("import_manifest_mismatch", error.RootElement.GetProperty("error_code").GetString());
            Assert.Contains(
                "embedded project root metadata",
                error.RootElement.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.False(File.Exists(importedDb));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void MatchProject_PrefersExactAbsolutePathCasing_Issue5195()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project-case-5195"));
        var upperDirectory = Path.Combine(root, "src", "Foo");
        var lowerDirectory = Path.Combine(root, "src", "foo");
        var projects = new DotNetProjectInfo[]
        {
            new("Upper", "src/Foo/App.csproj", upperDirectory),
            new("Lower", "src/foo/App.csproj", lowerDirectory),
        };

        var match = SolutionProjectResolver.MatchProject(
            projects,
            Path.Combine(lowerDirectory, "App.csproj"));

        Assert.NotNull(match);
        Assert.Equal("Lower", match.Name);
        Assert.Equal(lowerDirectory, match.DirectoryPath);
    }

    private static void SetUnknownExtensionPaths(string dbPath, string[] paths)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.SetMeta(
            DbContext.UnknownExtensionDiagnosticsVersionMetaKey,
            DbContext.UnknownExtensionDiagnosticsVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.UnknownExtensionFileCountMetaKey, paths.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.UnknownExtensionFilePathsMetaKey, JsonSerializer.Serialize(paths));
        var classification = UnknownExtensionClassifier.Classify(paths);
        writer.SetMeta(
            DbContext.UnknownExtensionGroupsMetaKey,
            UnknownExtensionClassifier.SerializeGroups(classification.Groups));
        writer.SetMeta(DbContext.UnknownExtensionFilesTruncatedMetaKey, bool.FalseString);
        writer.SetMeta(
            DbContext.UnknownExtensionFilePathLimitMetaKey,
            DbContext.UnknownExtensionFilePathSampleLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void SetWorkspaceVerificationPendingPaths(string dbPath, string[] paths)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.SetMeta(
            DbContext.WorkspaceVerificationPendingPathsMetaKey,
            JsonStringListCodec.Serialize(paths));
        writer.SetMeta(
            DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey,
            bool.TrueString);
    }

    private static void RewriteManifest(string archivePath, Action<JsonObject> rewrite)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("manifest.json entry was not found");
        JsonObject manifest;
        using (var stream = manifestEntry.Open())
        {
            manifest = JsonNode.Parse(stream)?.AsObject()
                ?? throw new InvalidOperationException("manifest.json did not contain an object");
        }

        rewrite(manifest);
        manifestEntry.Delete();
        var replacement = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
        writer.Write(manifest.ToJsonString());
    }

    private static string ExtractDatabase(string projectRoot, string archivePath, string fileName)
    {
        var extractedDb = Path.Combine(projectRoot, fileName);
        using var archive = ZipFile.OpenRead(archivePath);
        archive.GetEntry("codeindex.db")!.ExtractToFile(extractedDb);
        return extractedDb;
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
}
