using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Fact]
    public void Run_UpdateMode_RejectsNewSymbolKindFilterPolicy()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), """
                class App:
                    pass

                def helper():
                    return App()
                """);
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.AppendAllText(Path.Combine(projectRoot, "app.py"), "\n# touched\n");
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "app.py"), DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--exclude-symbol-kind", "function", "--files", "app.py", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("full index refresh", json.GetProperty("hint").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_RejectsRemovedSymbolKindFilterPolicy()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.py");
            File.WriteAllText(sourcePath, """
                class App:
                    pass

                def helper():
                    return App()
                """);
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--exclude-symbol-kind", "function", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.AppendAllText(sourcePath, "\n# touched\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.py", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("symbol-kind filter policy cannot change", json.GetProperty("message").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateNonCSharpFile_DoesNotResolveCSharpMetadataTargets()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_update_non_csharp_no_csharp_metadata");
        var ranCSharpPrepass = false;
        var resolvedMetadataTargets = false;
        var rebuiltTypeScriptAugmentation = false;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "interface AppApi { run(): void; }\n");
            File.WriteAllText(Path.Combine(projectRoot, "tool.py"), "def run():\n    return 1\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "tool.py"), "def run():\n    return 2\n");
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "tool.py"), DateTime.UtcNow.AddSeconds(2));

            IndexCommandRunner.UpdateCSharpPrepassForTesting = () => ranCSharpPrepass = true;
            IndexCommandRunner.UpdateCSharpMetadataResolveForTesting = () => resolvedMetadataTargets = true;
            IndexCommandRunner.UpdateTypeScriptAugmentationRebuildForTesting = () => rebuiltTypeScriptAugmentation = true;

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--files", "tool.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.False(ranCSharpPrepass);
            Assert.False(resolvedMetadataTargets);
            Assert.False(rebuiltTypeScriptAugmentation);
        }
        finally
        {
            IndexCommandRunner.UpdateCSharpPrepassForTesting = null;
            IndexCommandRunner.UpdateCSharpMetadataResolveForTesting = null;
            IndexCommandRunner.UpdateTypeScriptAugmentationRebuildForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_HardlinkedTargets_SkipsDuplicatePathWithWarning()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var original = Path.Combine(projectRoot, "original.cs");
            var duplicate = Path.Combine(projectRoot, "duplicate.cs");
            File.WriteAllText(original, "public class HardlinkFixture { }\n");
            CreateHardLink(original, duplicate);

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.AppendAllText(original, "public class HardlinkFixture2 { }\n");
            File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "original.cs", "duplicate.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("update", json.GetProperty("mode").GetString());
            var summary = json.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("updated").GetInt32());
            Assert.Equal(1, summary.GetProperty("warnings").GetInt32());
            var warning = Assert.Single(json.GetProperty("warnings").EnumerateArray());
            Assert.Contains("hardlinked", warning.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Single(ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_JsonWritesLivenessToStderrWithoutPollutingStdout()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "public class Program { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "public class Program { public void Run() { } }\n");

            var (exitCode, json, stderr) = RunAndCaptureJsonWithStderr([projectRoot, "--files", "Program.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("update", json.GetProperty("mode").GetString());
            Assert.Contains("cdidx: checking C# workspace contracts", stderr);
            Assert.Contains("cdidx: updating", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_NoOpAgainstSharedExplicitDb_DoesNotRewriteIndexedProjectRoot()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_shared_root_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRootA, "readme.md"), "# from a\n");
            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            Directory.CreateDirectory(Path.Combine(projectRootB, "docs"));
            File.WriteAllText(Path.Combine(projectRootB, "docs", "readme.txt"), "not indexable\n");

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRootB, "--db", dbPath, "--files", "docs/readme.txt", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.True(updateJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(updateJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(updateJson.GetProperty("fold_ready").GetBoolean());
            Assert.Equal(JsonValueKind.Null, updateJson.GetProperty("fold_ready_reason").ValueKind);
            Assert.Equal(JsonValueKind.Null, updateJson.GetProperty("degraded_reason").ValueKind);
            Assert.Equal(JsonValueKind.Null, updateJson.GetProperty("recommended_action").ValueKind);
            Assert.Equal(JsonValueKind.Null, updateJson.GetProperty("alternative_action").ValueKind);

            using (var db = new DbContext(dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRootA), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    var statusExitCode = QueryCommandRunner.RunStatus(["--db", dbPath, "--json"], _jsonOptions);
                    Assert.Equal(CommandExitCodes.Success, statusExitCode);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    Assert.Equal(Path.GetFullPath(projectRootA), document.RootElement.GetProperty("project_root").GetString());
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_NoOpAgainstSharedExplicitDb_PurgesUnsupportedReferencesWithoutRewritingIndexedProjectRoot()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_shared_stale_refs_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRootA, "app.py"), "print('from a')\n");
            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            long CountReferences()
            {
                using var db = new DbContext(dbPath);
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM symbol_references";
                return (long)cmd.ExecuteScalar()!;
            }

            var baselineReferenceCount = CountReferences();
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "docs/readme.toml",
                    Lang = "toml",
                    Size = 12,
                    Lines = 1,
                    Modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "stale-edge",
                });
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "LegacyLink",
                        ReferenceKind = "call",
                        Line = 1,
                        Column = 1,
                        Context = "LegacyLink",
                    },
                ]);
            }
            Assert.Equal(baselineReferenceCount + 1, CountReferences());

            Directory.CreateDirectory(Path.Combine(projectRootB, "docs"));
            File.WriteAllText(Path.Combine(projectRootB, "docs", "readme.txt"), "not indexable\n");

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRootB, "--db", dbPath, "--files", "docs/readme.txt", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(baselineReferenceCount, updateJson.GetProperty("summary").GetProperty("references_total").GetInt32());
            Assert.True(updateJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(updateJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(updateJson.GetProperty("fold_ready").GetBoolean());

            Assert.Equal(baselineReferenceCount, CountReferences());

            using (var db = new DbContext(dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRootA), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    var statusExitCode = QueryCommandRunner.RunStatus(["--db", dbPath, "--json"], _jsonOptions);
                    Assert.Equal(CommandExitCodes.Success, statusExitCode);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    Assert.Equal(Path.GetFullPath(projectRootA), document.RootElement.GetProperty("project_root").GetString());
                    Assert.Equal(baselineReferenceCount, document.RootElement.GetProperty("references").GetInt32());
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_ExplicitDb_RealMutationRewritesIndexedProjectRootMetadata()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_shared_rewrite_root_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRootA, "init");
            File.WriteAllText(Path.Combine(projectRootA, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRootA, "add", ".");
            RunGit(projectRootA, "commit", "-m", "init-a");
            var headA = RunGitCaptureStdOut(projectRootA, "rev-parse", "HEAD").Trim();

            RunGit(projectRootB, "init");
            var sourcePathB = Path.Combine(projectRootB, "app.cs");
            File.WriteAllText(sourcePathB, "public class App { public void Run() { } public void Extra() { } }\n");
            RunGit(projectRootB, "add", ".");
            RunGit(projectRootB, "commit", "-m", "init-b");
            var headB = RunGitCaptureStdOut(projectRootB, "rev-parse", "HEAD").Trim();
            File.SetLastWriteTimeUtc(sourcePathB, DateTime.UtcNow.AddSeconds(2));

            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            using (var db = new DbContext(dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRootA), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRootB, "--db", dbPath, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());

            using (var db = new DbContext(dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRootB), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(Path.GetFullPath(projectRootB), statusJson.GetProperty("project_root").GetString());
            Assert.Equal(headB, statusJson.GetProperty("git_head").GetString());
            Assert.NotEqual(headA, statusJson.GetProperty("git_head").GetString());
            Assert.False(statusJson.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_LegacySharedExplicitDb_NoOpDoesNotHijackMissingIndexedProjectRootMetadata()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_legacy_explicit_noop_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRootA, "init");
            File.WriteAllText(Path.Combine(projectRootA, "app.py"), "print('hello')\n");
            RunGit(projectRootA, "add", ".");
            RunGit(projectRootA, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DeleteIndexedProjectRootMetadata(dbPath);

            Directory.CreateDirectory(Path.Combine(projectRootB, "docs"));
            File.WriteAllText(Path.Combine(projectRootB, "docs", "readme.txt"), "not indexable\n");

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRootB, "--db", dbPath, "--files", "docs/readme.txt", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());

            using (var db = new DbContext(dbPath))
            {
                Assert.Null(db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Null(statusJson.GetProperty("project_root").GetString());
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_head").ValueKind);
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_is_dirty").ValueKind);
        }
        finally
        {
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_LegacyExplicitDb_SuccessfulFileUpdateBackfillsIndexedProjectRootMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_legacy_explicit_update_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DeleteIndexedProjectRootMetadata(dbPath);
            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());

            using (var db = new DbContext(dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRoot), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(Path.GetFullPath(projectRoot), statusJson.GetProperty("project_root").GetString());
            Assert.False(string.IsNullOrWhiteSpace(statusJson.GetProperty("git_head").GetString()));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_LegacyExplicitDb_PurgeOnlyNoOpDoesNotBackfillIndexedProjectRootMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_legacy_explicit_purge_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "readme.md"), "# hello\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DeleteIndexedProjectRootMetadata(dbPath);
            int CountReferences()
            {
                using var db = new DbContext(dbPath);
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM symbol_references";
                return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            var baselineReferenceCount = CountReferences();
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "docs/readme.toml",
                    Lang = "toml",
                    Size = 12,
                    Lines = 1,
                    Modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "stale-edge",
                });
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "LegacyLink",
                        ReferenceKind = "call",
                        Line = 1,
                        Column = 1,
                        Context = "LegacyLink",
                    },
                ]);
            }
            Assert.Equal(baselineReferenceCount + 1, CountReferences());

            Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
            File.WriteAllText(Path.Combine(projectRoot, "docs", "readme.txt"), "not indexable\n");

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--files", "docs/readme.txt", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(baselineReferenceCount, updateJson.GetProperty("summary").GetProperty("references_total").GetInt32());

            using (var db = new DbContext(dbPath))
            {
                Assert.Null(db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Null(statusJson.GetProperty("project_root").GetString());
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_head").ValueKind);
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_is_dirty").ValueKind);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_LegacyExplicitDb_RollbackedFirstMutationDoesNotBackfillIndexedProjectRootMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_legacy_explicit_rollback_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DeleteIndexedProjectRootMetadata(dbPath);
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TRIGGER fail_update
                    BEFORE UPDATE ON files
                    BEGIN
                        SELECT RAISE(FAIL, 'boom');
                    END;
                    """;
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());

            using (var db = new DbContext(dbPath))
            {
                Assert.Null(db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Null(statusJson.GetProperty("project_root").GetString());
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_head").ValueKind);
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_is_dirty").ValueKind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateFiles_UnchangedStatMatch_SkipsWithoutOpeningFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.py");
            const string content = "def run():\n    return 1\n";
            File.WriteAllText(sourcePath, content);
            File.SetLastWriteTimeUtc(sourcePath, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.SetUnixFileMode(sourcePath, UnixFileMode.None);
            try
            {
                var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.py", "--json"]);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal("success", json.GetProperty("status").GetString());
                Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
                Assert.Equal(1, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            }
            finally
            {
                File.SetUnixFileMode(sourcePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_UpdateFiles_RemovesOldPathWhenExtensionChanges()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var oldPath = Path.Combine(projectRoot, "foo.py");
            var newPath = Path.Combine(projectRoot, "foo.md");
            File.WriteAllText(oldPath, "# Title\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--files", "foo.py", "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.True(IndexedFileExists(projectRoot, "foo.py"));

            File.Move(oldPath, newPath);
            File.AppendAllText(newPath, "Updated during rename\n");
            File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "foo.md", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.True(IndexedFileExists(projectRoot, "foo.md"));
            Assert.False(IndexedFileExists(projectRoot, "foo.py"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_RemovesOldPathWhenExtensionChangesToUnsupported()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var oldPath = Path.Combine(projectRoot, "foo.py");
            var newPath = Path.Combine(projectRoot, "foo.bin");
            File.WriteAllText(oldPath, "print('hello')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--files", "foo.py", "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.True(IndexedFileExists(projectRoot, "foo.py"));

            File.Move(oldPath, newPath);

            var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "foo.bin", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.False(IndexedFileExists(projectRoot, "foo.py"));
            Assert.False(IndexedFileExists(projectRoot, "foo.bin"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_FailedFirstMutation_DemotesReadinessBeforeRollback()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var (_, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.True(initialJson.GetProperty("fold_ready").GetBoolean());
            Assert.Equal(JsonValueKind.Null, initialJson.GetProperty("fold_ready_reason").ValueKind);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TRIGGER fail_update
                    BEFORE UPDATE ON files
                    BEGIN
                        SELECT RAISE(FAIL, 'boom');
                    END;
                    """;
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.False(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.False(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("fold_ready").GetBoolean());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var verifyCmd = verify.CreateCommand();
            verifyCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)verifyCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.GraphReadyFlag);
            Assert.Equal(0, userVersion & DbContext.IssuesReadyFlag);
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_AllowsProjectRelativePathsStartingWithDotDotName()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var hiddenDir = Path.Combine(projectRoot, "..hidden");
            Directory.CreateDirectory(hiddenDir);
            File.WriteAllText(Path.Combine(hiddenDir, "sample.cs"), "class Sample {}\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "..hidden/sample.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_TypeScriptConfigChangeFallsBackToFullScanForAliasSymbols()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "components"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "app", "components"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "pages"));
            File.WriteAllText(Path.Combine(projectRoot, "tsconfig.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "components", "Button.tsx"), "export const Button = 1;\n");
            File.WriteAllText(Path.Combine(projectRoot, "app", "components", "Button.tsx"), "export const UpdatedButton = 1;\n");
            File.WriteAllText(Path.Combine(projectRoot, "src", "pages", "Page.tsx"), "import { Button } from \"@/components/Button\";\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Contains("src/components/Button.tsx", ReadImportSymbolNames(dbPath));

            File.WriteAllText(Path.Combine(projectRoot, "tsconfig.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["app/*"]
                    }
                  }
                }
                """);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "tsconfig.json", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            var imports = ReadImportSymbolNames(dbPath);
            Assert.Contains("app/components/Button.tsx", imports);
            Assert.DoesNotContain("src/components/Button.tsx", imports);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_JavaScriptExtendedConfigChangeFallsBackToFullScanForAliasSymbols()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "components"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "app", "components"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "pages"));
            File.WriteAllText(Path.Combine(projectRoot, "jsconfig.json"), """
                {
                  "extends": "./jsconfig.base.json"
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "jsconfig.base.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~/*": ["src/*"]
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "components", "Card.js"), "export const Card = 1;\n");
            File.WriteAllText(Path.Combine(projectRoot, "app", "components", "Card.js"), "export const UpdatedCard = 1;\n");
            File.WriteAllText(Path.Combine(projectRoot, "src", "pages", "Page.js"), "import { Card } from \"~/components/Card\";\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Contains("src/components/Card.js", ReadImportSymbolNames(dbPath));

            File.WriteAllText(Path.Combine(projectRoot, "jsconfig.base.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~/*": ["app/*"]
                    }
                  }
                }
                """);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "jsconfig.base.json", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            var imports = ReadImportSymbolNames(dbPath);
            Assert.Contains("app/components/Card.js", imports);
            Assert.DoesNotContain("src/components/Card.js", imports);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_SkipsPathsOutsideProjectRoot()
    {
        var projectRoot = CreateTempProject();
        var outsideFile = Path.Combine(Directory.GetParent(projectRoot)!.FullName, $"outside_{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllText(outsideFile, "class Outside {}\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", $"../{Path.GetFileName(outsideFile)}", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
        }
        finally
        {
            DeleteFile(outsideFile);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_CsharpStaticInterfaceContractChange_ReindexesImplementers()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            File.WriteAllText(
                interfacePath,
                """
                public interface IParseable<T>
                {
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                """
                public readonly struct Money : IParseable<Money>
                {
                    public static Money Parse(string s) => new();
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));

            File.WriteAllText(
                interfacePath,
                """
                public interface IParseable<T>
                {
                    static abstract T Parse(string s);
                }
                """);
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(2));

            var updateExitCode = IndexCommandRunner.Run([projectRoot, "--files", "IParseable.cs", "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_UpdateFiles_CsharpStaticInterfaceContractRemoval_ReindexesImplementers()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            File.WriteAllText(
                interfacePath,
                """
                public interface IParseable<T>
                {
                    static abstract T Parse(string s);
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                """
                public readonly struct Money : IParseable<Money>
                {
                    public static Money Parse(string s) => new();
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));

            File.WriteAllText(
                interfacePath,
                """
                public interface IParseable<T>
                {
                }
                """);
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(2));

            var updateExitCode = IndexCommandRunner.Run([projectRoot, "--files", "IParseable.cs", "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_UpdateFiles_CsharpStaticInterfaceContractDeletion_ReindexesImplementers()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            File.WriteAllText(
                interfacePath,
                """
                public interface IParseable<T>
                {
                    static abstract T Parse(string s);
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                """
                public readonly struct Money : IParseable<Money>
                {
                    public static Money Parse(string s) => new();
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));

            File.Delete(interfacePath);

            var updateExitCode = IndexCommandRunner.Run([projectRoot, "--files", "IParseable.cs", "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_UpdateModeFallbackFullScan_CancelledAfterReadinessDemotion_ReportsRolledBackProgress()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "bin/\n");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            File.AppendAllText(Path.Combine(projectRoot, ".gitignore"), "obj/\n");
            File.WriteAllText(Path.Combine(projectRoot, "later.cs"), "public class Later { }\n");
            using var cancellation = new CancellationTokenSource();
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = () => cancellation.Cancel();

            int interruptedExitCode;
            JsonElement interruptedJson;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    interruptedExitCode = IndexCommandRunner.Run([projectRoot, "--files", ".gitignore", "--json"], _jsonOptions, cancellation);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    interruptedJson = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                    IndexCommandRunner.FullScanWritePhaseStartedForTesting = null;
                }
            }

            Assert.Equal(CommandExitCodes.Interrupted, interruptedExitCode);
            Assert.Contains("full-scan progress was rolled back", interruptedJson.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("rolled back", interruptedJson.GetProperty("hint").GetString(), StringComparison.Ordinal);

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            var lastRun = statusJson.GetProperty("last_failed_or_partial_index_run");
            Assert.Equal("partial", lastRun.GetProperty("status").GetString());
            Assert.Equal("incremental", lastRun.GetProperty("mode").GetString());
            Assert.False(lastRun.GetProperty("progress_persisted").GetBoolean());
            Assert.Contains("rolled back", lastRun.GetProperty("recovery_hint").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_CancelledAfterCommittedFile_ReportsPersistedProgress()
    {
        var projectRoot = CreateTempProject();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public int Version => 1; }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var initialChecksum = ReadIndexedChecksum(dbPath, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public int Version => 2; }\n");

            var hookInvoked = false;
            IndexCommandRunner.UpdateFileCommittedForTesting = (filesProcessed, filesTotal) =>
            {
                Assert.Equal(1, filesProcessed);
                Assert.Equal(1, filesTotal);
                hookInvoked = true;
                cancellation.Cancel();
            };

            int interruptedExitCode;
            JsonElement interruptedJson;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    interruptedExitCode = IndexCommandRunner.Run([projectRoot, "--files", "app.cs", "--json"], _jsonOptions, cancellation);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    interruptedJson = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                    IndexCommandRunner.UpdateFileCommittedForTesting = null;
                }
            }

            Assert.True(hookInvoked);
            Assert.Equal(CommandExitCodes.Interrupted, interruptedExitCode);
            Assert.Equal("error", interruptedJson.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, interruptedJson.GetProperty("error_code").GetString());
            Assert.Contains("completed update progress was saved", interruptedJson.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("completed update-mode file transactions remain", interruptedJson.GetProperty("hint").GetString(), StringComparison.Ordinal);
            Assert.Contains("app.cs", ReadIndexedPaths(dbPath));
            Assert.NotEqual(initialChecksum, ReadIndexedChecksum(dbPath, "app.cs"));

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            var lastRun = statusJson.GetProperty("last_failed_or_partial_index_run");
            Assert.Equal("partial", lastRun.GetProperty("status").GetString());
            Assert.Equal("update", lastRun.GetProperty("mode").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, lastRun.GetProperty("error_code").GetString());
            Assert.Equal(1, lastRun.GetProperty("files_processed").GetInt64());
            Assert.Equal(1, lastRun.GetProperty("files_total").GetInt64());
            Assert.True(lastRun.GetProperty("progress_persisted").GetBoolean());
            Assert.Contains("remain in the index", lastRun.GetProperty("recovery_hint").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            IndexCommandRunner.UpdateFileCommittedForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_CancelledAfterTypeScriptCommit_ClearsAugmentationVersion()
    {
        var projectRoot = CreateTempProject();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "types.ts");
            File.WriteAllText(sourcePath, "export interface Options { value: string }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(dbPath))
            {
                Assert.Equal(
                    DbContext.TypeScriptAugmentationVersion.ToString(CultureInfo.InvariantCulture),
                    db.GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey));
            }

            File.WriteAllText(sourcePath, "export interface Options { value: string; enabled: boolean }\n");

            var hookInvoked = false;
            IndexCommandRunner.UpdateFileCommittedForTesting = (filesProcessed, filesTotal) =>
            {
                Assert.Equal(1, filesProcessed);
                Assert.Equal(1, filesTotal);
                hookInvoked = true;
                cancellation.Cancel();
            };

            int interruptedExitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    interruptedExitCode = IndexCommandRunner.Run([projectRoot, "--files", "types.ts", "--json"], _jsonOptions, cancellation);
                }
                finally
                {
                    Console.SetOut(originalOut);
                    IndexCommandRunner.UpdateFileCommittedForTesting = null;
                }
            }

            Assert.True(hookInvoked);
            Assert.Equal(CommandExitCodes.Interrupted, interruptedExitCode);
            using (var db = new DbContext(dbPath))
                Assert.Null(db.GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey));
        }
        finally
        {
            IndexCommandRunner.UpdateFileCommittedForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DeleteTypeScriptFile_RebuildsAugmentationReferences()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "types.ts");
            File.WriteAllText(sourcePath, "export interface Options { value: string }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(sourcePath);
            var rebuiltTypeScriptAugmentation = false;
            IndexCommandRunner.UpdateTypeScriptAugmentationRebuildForTesting = () => rebuiltTypeScriptAugmentation = true;

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--files", "types.ts", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.True(rebuiltTypeScriptAugmentation);
        }
        finally
        {
            IndexCommandRunner.UpdateTypeScriptAugmentationRebuildForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithOversizedFile_PrintsSkipWarningWithoutRecoveryWarning()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllBytes(Path.Combine(projectRoot, "huge.py"), new byte[10 * 1024 * 1024 + 1]);

            var (exitCode, _, stderr) = RunCliInSubprocess([projectRoot, "--files", "huge.py"], projectRoot);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.DoesNotContain("Some files failed to update", stderr);
            Assert.DoesNotContain("rerun `cdidx index", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_VerboseRedirectedOutput_DoesNotRepeatUpdatingBanner()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "app.cs"), DateTime.UtcNow.AddSeconds(2));

            var (exitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot, "--files", "app.cs", "--verbose"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, CountOccurrences(stdout, "Updating 1 file..."));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_VerboseJson_WritesStatusToStderrAndKeepsStdoutJson()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot, "--files", "app.cs", "--verbose", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var json = JsonDocument.Parse(stdout);
            Assert.Equal("success", json.RootElement.GetProperty("Status").GetString());
            Assert.DoesNotContain("[OK  ]", stdout);
            Assert.Contains("[OK  ] app.cs", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_JsonKeepsGraphAndIssuesReadyAfterHealthyScopedRefresh()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_RemovesIndexedFileThatIsNowIgnored()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "generated.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "generated.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_RemovesIndexedFileThatMatchesLeadingRightBracketCharacterClass()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "].cs"), "class Ignored { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("].cs", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "[]].cs\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "].cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("].cs", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_RemovesIndexedFileThatMatchesPosixPunctCharacterClass()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "!.cs"), "class Ignored { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("!.cs", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "[[:punct:]].cs\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "!.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("!.cs", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DoesNotDeleteIndexedFileForMalformedBracketIgnoreRule()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "[a.py"), "print('keep literal')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("[a.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "[a.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "[a.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore:1", json.GetProperty("warnings")[0].GetProperty("file").GetString());
            Assert.Contains("Invalid ignore rule skipped", json.GetProperty("warnings")[0].GetProperty("message").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("[a.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_FallsBackToFullScanWhenIgnoreFilesChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "generated.py\n");
            RunGit(projectRoot, "add", ".gitignore");
            RunGit(projectRoot, "commit", "-m", "ignore generated");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
            Assert.Contains(".gitignore", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_FallsBackToFullScanWhenIgnoreFilesChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "generated.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", ".gitignore", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
            Assert.Contains(".gitignore", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_SkipsMutationWhenIgnoreRulesAreUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        UnixFileMode? originalMode = null;
        try
        {
            File.WriteAllText(ignorePath, "secret.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "secret.py"), "print('secret')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.DoesNotContain("secret.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "secret.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore", json.GetProperty("warnings")[0].GetProperty("file").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("secret.py", indexedPaths);
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_SkipsMutationWhenIgnoreRulesAreUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        UnixFileMode? originalMode = null;
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "secret.py"), "print('secret v1')\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("secret.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(ignorePath, "secret.py\n");
            RunGit(projectRoot, "add", ".gitignore");
            RunGit(projectRoot, "commit", "-m", "ignore secret");

            File.WriteAllText(Path.Combine(projectRoot, "secret.py"), "print('secret v2')\n");
            RunGit(projectRoot, "add", "secret.py");
            RunGit(projectRoot, "commit", "-m", "update secret");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal(".gitignore", json.GetProperty("warnings")[0].GetProperty("file").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("secret.py", indexedPaths);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.True(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_UnreadableIgnoreRulesDemoteReadinessForUnchangedIndexedFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        UnixFileMode? originalMode = null;
        try
        {
            File.WriteAllText(ignorePath, "secret.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "secret.py"), "print('secret')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("keep.py", ReadIndexedPaths(dbPath));

            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "keep.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore", json.GetProperty("warnings")[0].GetProperty("file").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.True(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DemotesReadinessWhenIgnoreFileChangedThenBecameUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        UnixFileMode? originalMode = null;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "a.cs");
            File.WriteAllText(sourcePath, "public class A { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("a.cs", ReadIndexedPaths(dbPath));

            File.WriteAllText(ignorePath, "a.cs\n");
            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "a.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore", json.GetProperty("warnings")[0].GetProperty("file").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());
            Assert.Contains("a.cs", ReadIndexedPaths(dbPath));

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.True(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_UnreadableIgnoreRulesDemoteReadinessForChangedIndexedFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        UnixFileMode? originalMode = null;
        try
        {
            File.WriteAllText(ignorePath, "secret.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "secret.py"), "print('secret')\n");
            var keepPath = Path.Combine(projectRoot, "keep.py");
            File.WriteAllText(keepPath, "print('keep v1')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("keep.py", ReadIndexedPaths(dbPath));

            File.WriteAllText(keepPath, "print('keep v2')\n");
            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "keep.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore", json.GetProperty("warnings")[0].GetProperty("file").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.True(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WhenIgnoreFileChanges_FallsBackToFullScanAndPurgesNowIgnoredRows()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Contains("generated.py", ReadIndexedPaths(dbPath));

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "*.py\n!keep.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", ".gitignore", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("incremental", json.GetProperty("mode").GetString());

            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
            Assert.Contains(".gitignore", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_SubdirectoryProjectRoot_RespectsAncestorGitignore()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "ignored.py"), "print('indexed first')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("ignored.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "subproj/ignored.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "ignored.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("ignored.py", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_SubdirectoryProjectRoot_RespectsAncestorDirectoryGitignoreRule()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('indexed first')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("app.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "subproj/\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("app.py", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_SubdirectoryProjectRoot_FallsBackToFullScanWhenAncestorIgnoreFileChanges()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var ancestorIgnorePath = Path.Combine(repoRoot, ".gitignore");
            File.WriteAllText(ancestorIgnorePath, "subproj/generated.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", ancestorIgnorePath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_SubdirectoryProjectRoot_FallsBackToFullScanWhenAncestorDirectoryIgnoreRuleChanges()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var ancestorIgnorePath = Path.Combine(repoRoot, ".gitignore");
            File.WriteAllText(ancestorIgnorePath, "subproj/\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", ancestorIgnorePath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_ProjectRootNamedNodeModules_UpdatesIndexedFile()
    {
        var tempRoot = CreateTempProject();
        var projectRoot = Path.Combine(tempRoot, "node_modules");
        try
        {
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.js"), "console.log('ignored root dir');\n");

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.js", "javascript", "console.log('stale');\n");
            Assert.Contains("app.js", ReadIndexedPaths(dbPath));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.js", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.Contains("app.js", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_SubdirectoryProjectRoot_UsesRepositoryRelativePaths()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            var appPath = Path.Combine(projectRoot, "app.py");
            File.WriteAllText(appPath, "print('v1')\n");
            RunGit(repoRoot, "add", ".");
            RunGit(repoRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var initialChecksum = ReadIndexedChecksum(dbPath, "app.py");

            File.WriteAllText(appPath, "print('v2 with more content')\n");
            RunGit(repoRoot, "add", "subproj/app.py");
            RunGit(repoRoot, "commit", "-m", "update app");
            var commitId = RunGitCaptureStdOut(repoRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.NotEqual(initialChecksum, ReadIndexedChecksum(dbPath, "app.py"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_SubdirectoryProjectRoot_FallsBackToFullScanWhenAncestorIgnoreFileChanges()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");
            RunGit(repoRoot, "add", ".");
            RunGit(repoRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "subproj/generated.py\n");
            RunGit(repoRoot, "add", ".gitignore");
            RunGit(repoRoot, "commit", "-m", "ignore generated");
            var commitId = RunGitCaptureStdOut(repoRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DoesNotPurgeOldRenamePathUnlessExplicitlyListed()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var srcDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(srcDir);
            var oldPath = Path.Combine(srcDir, "OldName.cs");
            var newPath = Path.Combine(srcDir, "NewName.cs");

            File.WriteAllText(oldPath, "public class OldName { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Move(oldPath, newPath);
            File.WriteAllText(newPath, "public class NewName { }\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "src/NewName.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("src/OldName.cs", indexedPaths);
            Assert.Contains("src/NewName.cs", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_PurgesOldRenamePath()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var srcDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(srcDir);
            var oldPath = Path.Combine(srcDir, "OldName.cs");
            var newPath = Path.Combine(srcDir, "NewName.cs");

            File.WriteAllText(oldPath, "public class OldName { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Move(oldPath, newPath);
            File.WriteAllText(newPath, "public class NewName { }\n");
            RunGit(projectRoot, "add", "-A");
            RunGit(projectRoot, "commit", "-m", "rename");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("src/OldName.cs", indexedPaths);
            Assert.Contains("src/NewName.cs", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetween_UpdatesNewPathAndRemovesRenamedOldPath()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var srcDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(srcDir);
            var oldPath = Path.Combine(srcDir, "OldName.cs");
            var newPath = Path.Combine(srcDir, "NewName.cs");

            File.WriteAllText(oldPath, "public class SameName { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            RunGit(projectRoot, "branch", "before-switch");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Move(oldPath, newPath);
            RunGit(projectRoot, "add", "-A");
            RunGit(projectRoot, "commit", "-m", "rename");
            RunGit(projectRoot, "branch", "after-switch");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "before-switch", "after-switch", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("src/OldName.cs", indexedPaths);
            Assert.Contains("src/NewName.cs", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetween_RemovesDeletedPath_2987()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var changelogDir = Path.Combine(projectRoot, "changelog.d", "unreleased");
            Directory.CreateDirectory(changelogDir);
            var deletedPath = Path.Combine(changelogDir, "+trimmed-release-json.fixed.md");

            File.WriteAllText(
                deletedPath,
                """
                ---
                category: fixed
                ---

                ## English

                - Placeholder.

                ## 日本語

                - プレースホルダー。
                """);
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            RunGit(projectRoot, "branch", "before-delete");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(deletedPath);
            RunGit(projectRoot, "add", "-A");
            RunGit(projectRoot, "commit", "-m", "delete fragment");
            RunGit(projectRoot, "branch", "after-delete");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "before-delete", "after-delete", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("changelog.d/unreleased/+trimmed-release-json.fixed.md", indexedPaths);

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", Path.Combine(projectRoot, ".cdidx", "codeindex.db"), "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("workspace_check").GetProperty("matches_workspace").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetween_FallsBackToFullScanWhenIgnoreFilesChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            RunGit(projectRoot, "branch", "before-switch");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "generated.py\n");
            RunGit(projectRoot, "add", ".gitignore");
            RunGit(projectRoot, "commit", "-m", "ignore generated");
            RunGit(projectRoot, "branch", "after-switch");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "before-switch", "after-switch", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
            Assert.Contains(".gitignore", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetweenMissingRef_ReturnsUsageError()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(projectRoot);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "HEAD", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("--changed-between requires exactly two refs", json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_RemovesIndexedScriptThatLosesShebang()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var toolPath = Path.Combine(projectRoot, "tool");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(toolPath, "plain text now\n");
            File.SetLastWriteTimeUtc(toolPath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "tool", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("tool", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_RemovesIndexedScriptThatLosesShebang()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var toolPath = Path.Combine(projectRoot, "tool");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(toolPath, "plain text now\n");
            RunGit(projectRoot, "add", "tool");
            RunGit(projectRoot, "commit", "-m", "remove shebang");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("tool", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DoesNotRemoveUnreadableExtensionlessScript()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var toolPath = Path.Combine(projectRoot, "tool");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SetUnixPermissions(toolPath, UnixFileMode.None);
            File.SetLastWriteTimeUtc(toolPath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "tool", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.False(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("tool", json.GetProperty("errors")[0].GetProperty("file").GetString());
            Assert.Equal("Could not probe file for indexability/language.", json.GetProperty("errors")[0].GetProperty("message").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("tool", indexedPaths);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.False(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            var toolPath = Path.Combine(projectRoot, "tool");
            if (File.Exists(toolPath))
                SetUnixPermissions(toolPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DemotesReadinessForUnreadableKnownExtensionFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "a.cs");
            File.WriteAllText(sourcePath, "public class A { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SetUnixPermissions(sourcePath, UnixFileMode.None);
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "a.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.False(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("a.cs", json.GetProperty("errors")[0].GetProperty("file").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.False(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            var sourcePath = Path.Combine(projectRoot, "a.cs");
            if (File.Exists(sourcePath))
                SetUnixPermissions(sourcePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DemotesReadinessForUnreadableNewKnownExtensionFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var sourcePath = Path.Combine(projectRoot, "b.cs");
            File.WriteAllText(sourcePath, "public class B { }\n");
            SetUnixPermissions(sourcePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "b.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.False(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("b.cs", json.GetProperty("errors")[0].GetProperty("file").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.False(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            var sourcePath = Path.Combine(projectRoot, "b.cs");
            if (File.Exists(sourcePath))
                SetUnixPermissions(sourcePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_JsonReportsDegradedReadinessWhenBitsStayDown()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var (_, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.True(initialJson.GetProperty("fold_ready").GetBoolean());
            Assert.Equal(JsonValueKind.Null, initialJson.GetProperty("fold_ready_reason").ValueKind);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.False(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_HumanOutputShowsDegradedReadinessWhenBitsStayDown()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, output) = RunAndCaptureOutput([projectRoot, "--files", "app.cs"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Graph    : degraded", output);
            Assert.Contains("Issues   : degraded", output);
            Assert.Contains("SQL graph: ready", output);
            Assert.Contains("Fold     : degraded", output);
            var readinessLines = output.Split('\n')
                .Where(line => line.Contains(": ready", StringComparison.Ordinal) || line.Contains(": degraded", StringComparison.Ordinal))
                .ToList();
            Assert.All(readinessLines, line => Assert.Equal(readinessLines[0].IndexOf(':', StringComparison.Ordinal), line.IndexOf(':', StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_JsonPreservesGraphAndIssuesWhenOnlyFoldIsMissing()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET name_folded = NULL;
                    UPDATE symbol_references SET symbol_name_folded = NULL, container_name_folded = NULL;
                    PRAGMA user_version = 3
                    """;
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DegradedWarningUsesResolvedProjectDbPathWhenCwdDiffers()
    {
        var projectRoot = CreateTempProject();
        var otherCwd = Path.Combine(Path.GetTempPath(), $"cdidx_other_cwd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(otherCwd);
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, _, errorOutput) = RunCliInSubprocess([projectRoot, "--files", "app.cs"], otherCwd);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Index completed with degraded readiness", errorOutput);
            Assert.Contains("graph_table_available=false", errorOutput);
            Assert.Contains("issues_table_available=false", errorOutput);
            Assert.Contains("fold_ready=false", errorOutput);
            Assert.Contains($"cdidx status --db \"{dbPath}\" --json", errorOutput);
        }
        finally
        {
            DeleteDirectory(otherCwd);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DegradedWarningUsesExplicitDbPath()
    {
        var projectRoot = CreateTempProject();
        var customDbDir = Path.Combine(Path.GetTempPath(), $"cdidx_custom_db_{Guid.NewGuid():N}");
        Directory.CreateDirectory(customDbDir);
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            var customDbPath = Path.Combine(customDbDir, "custom-index.db");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", customDbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            using (var conn = OpenNonPoolingConnection(customDbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, _, errorOutput) = RunCliInSubprocess([projectRoot, "--db", customDbPath, "--files", "app.cs"], projectRoot);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Index completed with degraded readiness", errorOutput);
            Assert.Contains("graph_table_available=false", errorOutput);
            Assert.Contains("issues_table_available=false", errorOutput);
            Assert.Contains("fold_ready=false", errorOutput);
            Assert.Contains($"cdidx status --db \"{customDbPath}\" --json", errorOutput);
        }
        finally
        {
            DeleteDirectory(customDbDir);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_Json_ReportsFoldOnlyRemediation()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version'";
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("update", json.GetProperty("mode").GetString());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("stale_fold_key_version", json.GetProperty("fold_ready_reason").GetString());
            Assert.Contains("older fold-key version", json.GetProperty("degraded_reason").GetString());
            Assert.Contains("cdidx backfill-fold --db", json.GetProperty("recommended_action").GetString());
            Assert.Contains(dbPath, json.GetProperty("recommended_action").GetString());
            Assert.Contains("--rebuild", json.GetProperty("alternative_action").GetString());
            Assert.Contains(dbPath, json.GetProperty("alternative_action").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_PreservesGraphAndIssuesOnPre86Db_WithoutStampingFold()
    {
        // Codex #86 second-pass regression: pre-#86 DB has user_version=3 (Graph|Issues).
        // Before this fix, `wasFullyReady = user_version == CurrentSchemaVersion (=7)` returned
        // false, so update mode cleared all 3 bits and restamped none — silently breaking
        // references/callers/callees/impact for the whole workspace even though only the
        // Fold bit was missing. After the fix, Graph/Issues must survive a partial update on
        // a pre-#86 DB; only Fold stays off (needs full rebuild).
        // pre-#86 DB (user_version=3) に対する partial update で Graph/Issues が落ちず、
        // Fold だけが未 stamp のまま残ることを確認する回帰テスト。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            // Initial full scan stamps user_version = CurrentSchemaVersion (7 = Graph|Issues|Fold).
            // 初回 full scan で user_version = 7（全 bit stamp）。
            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            // Simulate a pre-#86 DB by stripping the Fold bit (and wiping name_folded rows to
            // reflect a pre-#86 writer that did not populate them). User_version = 3.
            // pre-#86 DB を模擬: Fold bit を落とし、name_folded も NULL に戻す。
            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE symbols SET name_folded = NULL; UPDATE symbol_references SET symbol_name_folded = NULL, container_name_folded = NULL; PRAGMA user_version = 3";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            // Partial update via --files. Must NOT strip Graph/Issues trust just because Fold
            // was missing. After run: Graph+Issues still stamped, Fold stays off.
            // --files で partial update。Graph/Issues は維持、Fold は未 stamp のまま。
            var targetFile = Path.Combine(projectRoot, "app.cs");
            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--files", targetFile, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var verifyCmd = verify.CreateCommand();
            verifyCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)verifyCmd.ExecuteScalar()!;
            Assert.NotEqual(0, userVersion & DbContext.GraphReadyFlag);
            Assert.NotEqual(0, userVersion & DbContext.IssuesReadyFlag);
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DoesNotRestampFoldReadyWhenFoldKeyVersionMismatches()
    {
        // Codex #86 fourth-pass regression: when a future NameFold.Version bump ships, the
        // stored fold_key_version on existing DBs becomes stale. A partial --files / --commits
        // update can only re-fold touched rows with the new version; untouched rows keep the
        // OLD folded keys. Restamping FoldReady + overwriting fold_key_version to the new
        // version would let the reader advertise full Unicode-exact readiness while silently
        // mismatching on untouched rows. The correct behavior is to leave FoldReady off until
        // a full --rebuild regenerates every row at the current version.
        // Simulate by writing an older fold_key_version into codeindex_meta before the update.
        // 将来の version bump 後の partial update で FoldReady を restamp しないことを確認する。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            // Initial index stamps the current fold-key version.
            // 初回 index で現在の fold-key version が stamp される。
            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            // Simulate a future version bump: the DB was stamped by a binary that wrote
            // fold_key_version=0 (pretend old). The current binary expects the latest
            // NameFold.Version
            // so the reader sees a mismatch and falls back to NOCASE. A partial update must
            // preserve that state, not silently restamp the current version on mixed-state rows.
            // version 不一致を模擬: codeindex_meta の fold_key_version を 0 に書き換え。
            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version'";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            // Run a partial update. FoldReady bit AND version must NOT advance to the new state
            // because untouched rows still carry the old version's fold keys.
            // partial update 実行。FoldReady bit も version も新状態に進めてはいけない。
            var targetFile = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(targetFile, "public class App { public void Run() { } }\n");
            File.SetLastWriteTimeUtc(targetFile, DateTime.UtcNow.AddSeconds(2));
            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--files", targetFile, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_version'";
            var storedVersion = versionCmd.ExecuteScalar() as string;
            // Stored version may stay at "0" (what we wrote) or be unset; critically it must
            // NOT have advanced to the current NameFold.Version because that would let the
            // reader treat mixed-state rows as fully fold-ready.
            // version は "0" のままで OK。現在の NameFold.Version に昇格してはいけない。
            Assert.NotEqual(NameFold.Version.ToString(), storedVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DoesNotRestampFoldReadyWhenSymbolExtractorVersionMismatches()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");
            File.WriteAllText(Path.Combine(projectRoot, "untouched.cs"), "public class Untouched { }");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"UPDATE codeindex_meta SET value = '0' WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}'";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var targetFile = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(targetFile, "public class App { public void Run() { } }\n");
            File.SetLastWriteTimeUtc(targetFile, DateTime.UtcNow.AddSeconds(2));
            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--files", targetFile, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = $"SELECT value FROM codeindex_meta WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}'";
            var storedVersion = versionCmd.ExecuteScalar() as string;
            Assert.NotEqual(SymbolExtractor.GetContractVersion("csharp").ToString(System.Globalization.CultureInfo.InvariantCulture), storedVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_RestampsHotspotFamilyTrustOnOversizedFileSkip()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }");

            var (exitCode1, json1) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            Assert.Equal("success", json1.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var seededDb = new DbContext(dbPath))
                Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), seededDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            WriteOversizedAsciiFile(Path.Combine(projectRoot, "app.cs"));

            var (exitCode2, json2) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode2);
            Assert.Equal("success", json2.GetProperty("status").GetString());
            Assert.Equal(0, json2.GetProperty("summary").GetProperty("errors").GetInt32());

            using var verifyDb = new DbContext(dbPath);
            Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DoesNotRestampHotspotFamilyReadyWhenMarkerFingerprintChanges()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Caller.cs"), "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");

            var (exitCode1, json1) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            Assert.Equal("success", json1.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var seededDb = new DbContext(dbPath))
                Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), seededDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            File.WriteAllText(Path.Combine(projectRoot, "Extra.csproj"), "<Project />");

            var (exitCode2, json2) = RunAndCaptureJson([projectRoot, "--files", "Extra.csproj", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode2);
            Assert.Equal("success", json2.GetProperty("status").GetString());

            using var verifyDb = new DbContext(dbPath);
            Assert.Null(verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
            Assert.Null(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_Update_WhenHotspotFamilyMetadataCannotBeRestamped_ReportsDegradedReadiness()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            var callerPath = Path.Combine(projectRoot, "src", "Caller.cs");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(callerPath, "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");

            var (initialExitCode, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal("success", initialJson.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
            }

            File.WriteAllText(callerPath, "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); api.Run(); } }");
            File.SetLastWriteTimeUtc(callerPath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--files", "src/Caller.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.False(updateJson.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.Contains("hotspot_family_support_not_indexed=csharp", updateJson.GetProperty("hotspot_family_degraded_reason").GetString());

            File.WriteAllText(callerPath, "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); api.Run(); api.Run(1); } }");
            File.SetLastWriteTimeUtc(callerPath, DateTime.UtcNow.AddSeconds(4));

            var (subprocessExitCode, _, errorOutput) = RunCliInSubprocess([projectRoot, "--files", "src/Caller.cs"], projectRoot);
            Assert.Equal(CommandExitCodes.Success, subprocessExitCode);
            Assert.Contains("Index completed with degraded readiness", errorOutput);
            Assert.Contains("hotspot_family_ready=false", errorOutput);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_Update_RollsBackHotspotFamilyRestampWhenCommitIsInterrupted()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            var callerPath = Path.Combine(projectRoot, "src", "Caller.cs");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(callerPath, "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");

            var (initialExitCode, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.True(initialJson.GetProperty("hotspot_family_ready").GetBoolean());

            File.WriteAllText(callerPath, "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); api.Run(); } }");
            File.SetLastWriteTimeUtc(callerPath, DateTime.UtcNow.AddSeconds(2));

            IndexCommandRunner.HotspotFamilyUpdateRestampReadyForCommitForTesting = () =>
                throw new InvalidOperationException("simulate crash after hotspot restamp");

            Assert.Throws<InvalidOperationException>(() =>
                RunAndCaptureJson([projectRoot, "--files", "src/Caller.cs", "--json"]));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var verifyDb = new DbContext(dbPath);
            Assert.Null(verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
            Assert.Null(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp")));
        }
        finally
        {
            IndexCommandRunner.HotspotFamilyUpdateRestampReadyForCommitForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DoesNotRestampFoldReadyWhenFoldFingerprintMismatches()
    {
        // #97: partial update must not restamp FoldReady when the stored runtime canary
        // fingerprint differs from the current binary/runtime, even if NameFold.Version is
        // unchanged. Untouched rows still carry keys generated under the old runtime tables.
        // #97: version が同じでも fingerprint がズレた DB は partial update で restamp しない。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = 'DEADBEEFDEADBEEF' WHERE key = 'fold_key_fingerprint'";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var targetFile = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(targetFile, "public class App { public void Run() { } }\n");
            File.SetLastWriteTimeUtc(targetFile, DateTime.UtcNow.AddSeconds(2));
            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--files", targetFile, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);

            using var fingerprintCmd = verify.CreateCommand();
            fingerprintCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_fingerprint'";
            var storedFingerprint = fingerprintCmd.ExecuteScalar() as string;
            Assert.NotEqual(NameFold.Fingerprint(), storedFingerprint);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DoesNotOverwriteIndexedHeadCommit()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");
            var initialHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            RunGit(projectRoot, "checkout", "-b", "feature");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "feature");

            // `--files` is a user-driven partial update. It must NOT republish the captured
            // HEAD; the next default full scan is what advances the stale marker. Issue #1508.
            // `--files` は利用者指定の部分更新。HEAD を進めず、次の full scan で初めて更新する。
            var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var db = new DbContext(dbPath);
            Assert.Equal(initialHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }
}
