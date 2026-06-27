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
    public void Run_FullScanJson_ProjectMarkerBudgetWarningIncludesTruncatedWarning()
    {
        var projectRoot = CreateTempProject();
        var previousEnumerator = FileIndexer.EnumerateProjectMarkerDirectoriesForTesting;
        try
        {
            var childDir = Path.Combine(projectRoot, "nested");
            Directory.CreateDirectory(childDir);
            File.WriteAllText(Path.Combine(projectRoot, "App.cs"), "public class App { }\n");
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting =
                _ => Enumerable.Repeat(childDir, 8192);

            var (exitCode, json, _) = RunAndCaptureJsonWithStderr([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains(
                json.GetProperty("warnings").EnumerateArray(),
                warning =>
                    warning.GetProperty("message").GetString()!.Contains("Project marker discovery truncated", StringComparison.Ordinal)
                    && warning.GetProperty("message").GetString()!.Contains("directory budget", StringComparison.Ordinal));
        }
        finally
        {
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting = previousEnumerator;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ExcludeSymbolKindDropsMatchingSymbols()
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

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--exclude-symbol-kind", "function", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("symbols_dropped_by_kind_filter").GetInt32());
            Assert.Equal(["function"], json.GetProperty("symbol_kind_filter").GetProperty("exclude").EnumerateArray().Select(value => value.GetString()).ToArray());

            var counts = ReadSymbolKindCounts(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.True(counts.GetValueOrDefault("class") > 0);
            Assert.False(counts.ContainsKey("function"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_IncludeSymbolKindKeepsOnlyMatchingSymbols()
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

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--include-symbol-kind", "class", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("summary").GetProperty("symbols_dropped_by_kind_filter").GetInt32() > 0);

            var counts = ReadSymbolKindCounts(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.True(counts.GetValueOrDefault("class") > 0);
            Assert.DoesNotContain(counts.Keys, kind => !string.Equals(kind, "class", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_FinalizesMutualRecursionAfterBulkReferenceInsert()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "cycle_a.cs"), """
                public static class FullScanCycleA
                {
                    public static void CrossCycleA() { CrossCycleB(); }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "cycle_b.cs"), """
                public static class FullScanCycleB
                {
                    public static void CrossCycleB() { CrossCycleA(); }
                }
                """);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(CountMutualRecursionReferences(Path.Combine(projectRoot, ".cdidx", "codeindex.db")) >= 2);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_SkipsOversizedGitExclude()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var excludePath = Path.Combine(projectRoot, ".git", "info", "exclude");
            File.WriteAllText(excludePath, new string('x', IndexCommandRunner.MaxGitExcludeBytes + 1));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(IndexCommandRunner.MaxGitExcludeBytes + 1, File.ReadAllText(excludePath).Length);
            Assert.DoesNotContain("cdidx (CodeIndex)", File.ReadAllText(excludePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanAfterHeadChange_DoesNotPreExtractUnchangedFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_head_changed_skip_before_extract");
        bool? parallelized = null;
        string? reason = null;
        var loadedPaths = new ConcurrentBag<string>();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "export interface AppApi { run(): void; }\n");
            RunGit(projectRoot, "add", "app.cs", "app.ts");
            RunGit(projectRoot, "commit", "-m", "initial");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "feature.cs"), "public class Feature { public void Run() { } }\n");
            RunGit(projectRoot, "add", "feature.cs");
            RunGit(projectRoot, "commit", "-m", "next");

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanExtractionSchedulingForTesting = (enabled, why) =>
                    {
                        parallelized = enabled;
                        reason = why;
                    };
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

                    var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.Success, refreshExitCode);
                    Assert.Equal("success", refreshJson.GetProperty("status").GetString());
                    Assert.True(refreshJson.GetProperty("head_changed").GetBoolean());
                    Assert.False(parallelized);
                    Assert.Null(reason);
                    Assert.Equal(2, refreshJson.GetProperty("summary").GetProperty("files_skipped").GetInt32());
                    Assert.DoesNotContain("app.cs", loadedPaths);
                    Assert.DoesNotContain("app.ts", loadedPaths);
                    Assert.Contains("feature.cs", loadedPaths);
                }
                finally
                {
                    IndexCommandRunner.FullScanExtractionSchedulingForTesting = null;
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanAfterTypeScriptConfigChange_ReprocessesUnchangedTypeScriptFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_tsconfig_refresh");
        var loadedPaths = new ConcurrentBag<string>();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "export interface AppApi { run(): void; }\n");
            File.WriteAllText(Path.Combine(projectRoot, "tsconfig.json"), "{ \"compilerOptions\": { \"baseUrl\": \".\" } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "tsconfig.json"), "{ \"compilerOptions\": { \"baseUrl\": \".\", \"paths\": {} } }\n");
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "tsconfig.json"), DateTime.UtcNow.AddSeconds(2));

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

                    var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.Success, refreshExitCode);
                    Assert.Equal("success", refreshJson.GetProperty("status").GetString());
                    Assert.Contains("app.ts", loadedPaths);
                    Assert.Contains("tsconfig.json", loadedPaths);
                }
                finally
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanAfterTypeScriptConfigContentChangeWithStableStat_ReprocessesUnchangedTypeScriptFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_tsconfig_stable_stat");
        var loadedPaths = new ConcurrentBag<string>();
        try
        {
            var configPath = Path.Combine(projectRoot, "tsconfig.json");
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "export interface AppApi { run(): void; }\n");
            File.WriteAllText(configPath, "{ \"compilerOptions\": { \"baseUrl\": \".\" } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var originalStat = new FileInfo(configPath);
            var replacement = "{ \"compilerOptions\": { \"baseUrl\": \"x\" } }\n";
            Assert.Equal(originalStat.Length, Encoding.UTF8.GetByteCount(replacement));
            File.WriteAllText(configPath, replacement);
            File.SetLastWriteTimeUtc(configPath, originalStat.LastWriteTimeUtc);

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

                    var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.Success, refreshExitCode);
                    Assert.Equal("success", refreshJson.GetProperty("status").GetString());
                    Assert.Contains("app.ts", loadedPaths);
                    Assert.Contains("tsconfig.json", loadedPaths);
                }
                finally
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanAfterDerivedTypeScriptConfigDelete_ReprocessesUnchangedTypeScriptFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_tsconfig_base_delete");
        var loadedPaths = new ConcurrentBag<string>();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "export interface AppApi { run(): void; }\n");
            File.WriteAllText(Path.Combine(projectRoot, "tsconfig.base.json"), "{ \"compilerOptions\": { \"baseUrl\": \".\" } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(Path.Combine(projectRoot, "tsconfig.base.json"));

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

                    var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.Success, refreshExitCode);
                    Assert.Equal("success", refreshJson.GetProperty("status").GetString());
                    Assert.Contains("app.ts", loadedPaths);
                }
                finally
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanWithSequentialExtraction_PersistsValidationIssues()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_sequential_validation");
        bool? parallelized = null;
        int? queueCapacity = null;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                "public class App\rpublic class Other\n");

            IndexCommandRunner.FullScanExtractionSchedulingForTesting = (enabled, _) => parallelized = enabled;
            IndexCommandRunner.FullScanExtractionQueueCapacityForTesting = capacity => queueCapacity = capacity;

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--exclude-symbol-kind", "test.method", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.False(parallelized);
            Assert.Equal(1, queueCapacity);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var issue = Assert.Single(ReadFileIssues(dbPath, "mixed_line_endings"));
            Assert.Equal("app.cs", issue.Path);
        }
        finally
        {
            IndexCommandRunner.FullScanExtractionSchedulingForTesting = null;
            IndexCommandRunner.FullScanExtractionQueueCapacityForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanWithoutCSharp_DoesNotRunCSharpPrepass()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_no_csharp_prepass");
        var ranCSharpPrepass = false;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "interface AppApi { run(): void; }\n");
            File.WriteAllText(Path.Combine(projectRoot, "tool.py"), "def run():\n    return 1\n");

            IndexCommandRunner.FullScanCSharpPrepassForTesting = () => ranCSharpPrepass = true;

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.False(ranCSharpPrepass);
        }
        finally
        {
            IndexCommandRunner.FullScanCSharpPrepassForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanAfterHeadChange_WithPostExtractionHooksKeepsSequentialReferences()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_head_changed_hooks_sequential");
        bool? parallelized = null;
        var originalHooksDir = Environment.GetEnvironmentVariable("CDIDX_HOOKS_DIR");
        try
        {
            var hooksDir = Path.Combine(projectRoot, "hooks");
            Directory.CreateDirectory(hooksDir);
            File.Copy(typeof(SamplePostExtractionHook).Assembly.Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));
            Environment.SetEnvironmentVariable("CDIDX_HOOKS_DIR", hooksDir);

            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "initial");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.AppendAllText(Path.Combine(projectRoot, "app.cs"), "public class Next { public void Run() { } }\n");
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "next");

            IndexCommandRunner.FullScanExtractionSchedulingForTesting = (enabled, _) => parallelized = enabled;

            var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            Assert.Contains(refreshJson.GetProperty("status").GetString(), ["success", "partial"]);
            Assert.False(parallelized);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_HOOKS_DIR", originalHooksDir);
            IndexCommandRunner.FullScanExtractionSchedulingForTesting = null;
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanExplicitDb_FailedFirstMutation_DoesNotRewriteIndexedProjectRootMetadata()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_fullscan_explicit_rollback_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRootA, "init");
            var sourcePathA = Path.Combine(projectRootA, "app.cs");
            File.WriteAllText(sourcePathA, "public class AppA { public void Run() { } }\n");
            RunGit(projectRootA, "add", ".");
            RunGit(projectRootA, "commit", "-m", "init-a");
            var headA = RunGitCaptureStdOut(projectRootA, "rev-parse", "HEAD").Trim();

            RunGit(projectRootB, "init");
            var sourcePathB = Path.Combine(projectRootB, "app.cs");
            File.WriteAllText(sourcePathB, "public class AppB { public void Run() { } public void Extra() { } }\n");
            RunGit(projectRootB, "add", ".");
            RunGit(projectRootB, "commit", "-m", "init-b");
            File.SetLastWriteTimeUtc(sourcePathB, DateTime.UtcNow.AddSeconds(2));

            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

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

            var (exitCode, json) = RunAndCaptureJson([projectRootB, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());

            using (var db = new DbContext(dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRootA), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(Path.GetFullPath(projectRootA), statusJson.GetProperty("project_root").GetString());
            Assert.Equal(headA, statusJson.GetProperty("git_head").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_FullScanExplicitDb_SuccessfulNoOpBackfillsMissingIndexedProjectRootMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_fullscan_explicit_noop_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");
            var head = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DeleteIndexedProjectRootMetadata(dbPath);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());

            using (var db = new DbContext(dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRoot), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(Path.GetFullPath(projectRoot), statusJson.GetProperty("project_root").GetString());
            Assert.Equal(head, statusJson.GetProperty("git_head").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_FullScan_CsharpStaticInterfaceMembersAcrossFiles_IndexesImplicitImplementationReference()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "IParseable.cs"),
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

            var exitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode);

            using var conn = OpenNonPoolingConnection(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM symbol_references r
                JOIN files f ON f.id = r.file_id
                JOIN reference_lines rl ON rl.id = r.reference_line_id
                WHERE f.path = 'Money.cs'
                  AND r.symbol_name = 'Parse'
                  AND r.reference_kind = 'implicit_implementation'
                  AND rl.context = 'public static Money Parse(string s) => new();'";
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(1, count);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_ConfiguredGeneratedCodePatternsKeepChunksButSkipExtraction()
    {
        using var env = EnvironmentVariableScope.Capture(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable);
        var projectRoot = CreateTempProject();
        try
        {
            env.Set(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable, "src/generated/**");
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "generated"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "generated", "GeneratedClient.cs"),
                """
                public class GeneratedClient
                {
                    public string Lookup() => "generated";
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "NormalClient.cs"),
                """
                public class NormalClient
                {
                    public string Lookup() => "normal";
                }
                """);

            var exitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var generatedCmd = conn.CreateCommand();
                generatedCmd.CommandText = """
                    SELECT f.generated,
                           (SELECT COUNT(*) FROM chunks c WHERE c.file_id = f.id AND c.content LIKE '%GeneratedClient%'),
                           (SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id),
                           (SELECT COUNT(*) FROM symbol_references r WHERE r.file_id = f.id),
                           (SELECT COUNT(*) FROM file_issues i WHERE i.file_id = f.id AND i.kind = @issueKind)
                    FROM files f
                    WHERE f.path = @path
                    """;
                generatedCmd.Parameters.AddWithValue("@issueKind", FileIndexer.GeneratedCodeExtractionSkippedIssueKind);
                generatedCmd.Parameters.AddWithValue("@path", "src/generated/GeneratedClient.cs");
                using (var reader = generatedCmd.ExecuteReader())
                {
                    Assert.True(reader.Read());
                    Assert.Equal(0, reader.GetInt32(0));
                    Assert.True(reader.GetInt32(1) > 0);
                    Assert.Equal(0, reader.GetInt32(2));
                    Assert.Equal(0, reader.GetInt32(3));
                    Assert.Equal(1, reader.GetInt32(4));
                }

                using var normalCmd = conn.CreateCommand();
                normalCmd.CommandText = """
                    SELECT COUNT(*)
                    FROM symbols s
                    JOIN files f ON f.id = s.file_id
                    WHERE f.path = 'NormalClient.cs'
                      AND s.name = 'NormalClient'
                    """;
                Assert.Equal(1L, (long)normalCmd.ExecuteScalar()!);
            }

            env.Set(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable, null);
            var updateExitCode = IndexCommandRunner.Run([projectRoot, "--files", "src/generated/GeneratedClient.cs", "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);

            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var updatedCmd = conn.CreateCommand();
                updatedCmd.CommandText = """
                    SELECT f.generated,
                           (SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id AND s.name = 'GeneratedClient'),
                           (SELECT COUNT(*) FROM file_issues i WHERE i.file_id = f.id AND i.kind = @issueKind)
                    FROM files f
                    WHERE f.path = @path
                    """;
                updatedCmd.Parameters.AddWithValue("@issueKind", FileIndexer.GeneratedCodeExtractionSkippedIssueKind);
                updatedCmd.Parameters.AddWithValue("@path", "src/generated/GeneratedClient.cs");
                using var reader = updatedCmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(0, reader.GetInt32(0));
                Assert.Equal(1, reader.GetInt32(1));
                Assert.Equal(0, reader.GetInt32(2));
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_WithOversizedFile_PrintsSkipWarningWithoutRecoveryWarning()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllBytes(Path.Combine(projectRoot, "huge.py"), new byte[10 * 1024 * 1024 + 1]);

            var (exitCode, _, stderr) = RunCliInSubprocess([projectRoot], projectRoot);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("[WARN] File too large", stderr);
            Assert.DoesNotContain("Some files failed to index", stderr);
            Assert.DoesNotContain("rerun `cdidx index", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RedirectedOutput_PrintsIndexingBannerOnce()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllText(Path.Combine(projectRoot, "util.py"), "def helper():\n    return 1\n");

            var (exitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, CountOccurrences(stdout, "Indexing..."));
            Assert.Contains("0.0%", stdout);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_CancelledAfterReadinessDemotion_RollsBackExistingIndex()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            int initialReadiness;
            using (var db = new DbContext(dbPath))
                initialReadiness = db.GetUserVersion();
            Assert.Equal(DbContext.CurrentSchemaVersion, initialReadiness);
            Assert.Contains("app.cs", ReadIndexedPaths(dbPath));

            File.WriteAllText(Path.Combine(projectRoot, "later.cs"), "public class Later { }\n");
            using var cancellation = new CancellationTokenSource();
            var hookInvoked = false;
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = () =>
            {
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
                    interruptedExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions, cancellation);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    interruptedJson = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                    IndexCommandRunner.FullScanWritePhaseStartedForTesting = null;
                }
            }

            Assert.True(hookInvoked);
            Assert.Equal(CommandExitCodes.Interrupted, interruptedExitCode);
            Assert.Equal("error", interruptedJson.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, interruptedJson.GetProperty("error_code").GetString());
            Assert.Contains("full-scan progress was rolled back", interruptedJson.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("rolled back", interruptedJson.GetProperty("hint").GetString(), StringComparison.Ordinal);
            var reopenWarning = ConsoleCapture.CaptureError(() =>
            {
                using var db = new DbContext(dbPath);
                Assert.Equal(initialReadiness, db.GetUserVersion());
            });
            Assert.DoesNotContain("Last batch did not complete", reopenWarning);
            Assert.DoesNotContain("later.cs", ReadIndexedPaths(dbPath));
            Assert.Contains("app.cs", ReadIndexedPaths(dbPath));

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            var lastRun = statusJson.GetProperty("last_failed_or_partial_index_run");
            Assert.Equal("partial", lastRun.GetProperty("status").GetString());
            Assert.Equal("incremental", lastRun.GetProperty("mode").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, lastRun.GetProperty("error_code").GetString());
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
    public void Run_FullScan_WithMalformedIgnoreRule_ReturnsSuccessWithWarningInsteadOfCrashing()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "[z-a].py\nignored.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "ignored.py"), "print('ignored')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore:1", json.GetProperty("warnings")[0].GetProperty("file").GetString());
            Assert.Contains("Invalid ignore rule skipped", json.GetProperty("warnings")[0].GetProperty("message").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("ignored.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
            Assert.Contains(".gitignore", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_SubdirectoryProjectRoot_UsesRepositoryIgnoreCaseConfigWhenTrue()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            RunGit(repoRoot, "config", "core.ignorecase", "true");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "FOO.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "foo.py"), "print('ignored')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("foo.py", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_FullScan_SubdirectoryProjectRoot_UsesRepositoryIgnoreCaseConfigWhenFalse()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            RunGit(repoRoot, "config", "core.ignorecase", "false");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "FOO.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "foo.py"), "print('kept')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("foo.py", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_FullScan_SubdirectoryProjectRoot_RespectsAncestorGitignore()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "subproj/ignored.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "ignored.py"), "print('ignored')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('kept')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("ignored.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_FullScan_SubdirectoryProjectRoot_RespectsAncestorDirectoryGitignoreRule()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "subproj/\n");
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('ignored root dir')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

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
    public void Run_FullScan_ProjectRootNamedNodeModules_IndexesExplicitProjectRoot()
    {
        var tempRoot = CreateTempProject();
        var projectRoot = Path.Combine(tempRoot, "node_modules");
        try
        {
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.js"), "console.log('ignored root dir');\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("app.js", indexedPaths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RemovesIndexedScriptThatLosesShebang()
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

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_scanned").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_purged").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("tool", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DoesNotPurgeFilesFromUnreadableDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal("secret", json.GetProperty("errors")[0].GetProperty("file").GetString());
            Assert.Equal("Could not scan directory due to permissions.", json.GetProperty("errors")[0].GetProperty("message").GetString());

            var (humanExitCode, _, stderr) = RunAndCaptureStreams([projectRoot]);
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("secret", stderr);
            Assert.Contains("Could not scan directory due to permissions.", stderr);

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("secret/a.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_WritesCheckpointAndUsesItOnSuccessfulRetry()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var srcDir = Path.Combine(projectRoot, "src");
        var secretDir = Path.Combine(projectRoot, "secret");
        var srcFile = Path.Combine(srcDir, "b.cs");
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(srcFile, "public class B { }\n");
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SetUnixPermissions(secretDir, UnixFileMode.None);
            var (partialExitCode, partialJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());
            var checkpointPath = Path.Combine(projectRoot, ".cdidx", "scan-checkpoint.json");
            Assert.True(File.Exists(checkpointPath));

            SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Delete(srcFile);
            var (retryExitCode, retryJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, retryExitCode);
            Assert.Equal("success", retryJson.GetProperty("status").GetString());
            Assert.False(File.Exists(checkpointPath));

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("src/b.cs", indexedPaths);
            Assert.Contains("secret/a.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReportsCheckpointSaveFailureAsWarning()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SetUnixPermissions(secretDir, UnixFileMode.None);
            IndexCommandRunner.WriteScanCheckpointForTesting = _ => throw new IOException("checkpoint save denied");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Contains(
                json.GetProperty("warnings").EnumerateArray(),
                warning =>
                    warning.GetProperty("file").GetString() == "<scan_checkpoint>"
                    && warning.GetProperty("message").GetString()!.Contains("scan checkpoint save failed", StringComparison.Ordinal)
                    && warning.GetProperty("message").GetString()!.Contains("IOException", StringComparison.Ordinal));
        }
        finally
        {
            IndexCommandRunner.WriteScanCheckpointForTesting = null;
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReportsCheckpointDeleteFailureAsWarning()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SetUnixPermissions(secretDir, UnixFileMode.None);
            var (partialExitCode, partialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());

            var checkpointPath = Path.Combine(projectRoot, ".cdidx", "scan-checkpoint.json");
            Assert.True(File.Exists(checkpointPath));

            SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            IndexCommandRunner.DeleteScanCheckpointForTesting = _ => throw new IOException("checkpoint delete denied");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Contains(
                json.GetProperty("warnings").EnumerateArray(),
                warning =>
                    warning.GetProperty("file").GetString() == "<scan_checkpoint>"
                    && warning.GetProperty("message").GetString()!.Contains("scan checkpoint delete failed", StringComparison.Ordinal)
                    && warning.GetProperty("message").GetString()!.Contains("IOException", StringComparison.Ordinal));
            Assert.True(File.Exists(checkpointPath));
        }
        finally
        {
            IndexCommandRunner.DeleteScanCheckpointForTesting = null;
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_IgnoresOversizedCheckpoint()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "a.cs"), "public class A { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            var head = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var checkpointPath = Path.Combine(projectRoot, ".cdidx", "scan-checkpoint.json");
            Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);
            var checkpoint = $$"""
                {
                  "Version": 1,
                  "GitHead": "{{head}}",
                  "Directories": [
                    "src"
                  ]
                }
                """;
            var padding = new System.Text.StringBuilder(IndexCommandRunner.MaxScanCheckpointBytes + 2048);
            while (checkpoint.Length + padding.Length <= IndexCommandRunner.MaxScanCheckpointBytes)
                padding.Append(' ', 1024).Append('\n');
            File.WriteAllText(checkpointPath, checkpoint + padding);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains(
                json.GetProperty("warnings").EnumerateArray(),
                warning =>
                    warning.GetProperty("file").GetString() == "<scan_checkpoint>"
                    && warning.GetProperty("message").GetString()!.Contains("file exceeds the scan checkpoint size limit", StringComparison.Ordinal));

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("src/a.cs", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_PurgesStaleRowsWithinListedDirectoriesEvenWhenAnotherDirectoryIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            var toolPath = Path.Combine(projectRoot, "tool");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(toolPath, "plain text now\n");
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Equal("secret", json.GetProperty("errors")[0].GetProperty("file").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("tool", indexedPaths);
            Assert.Contains("secret/a.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_HumanOutput_ExplainsPartialPurgeScopeWhenAnotherDirectoryIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            var toolPath = Path.Combine(projectRoot, "tool");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(toolPath, "plain text now\n");
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (humanExitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot]);

            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("positively observed as no longer indexable or missing from directories whose file listing completed successfully", stdout);
            Assert.Contains("Skipped authoritative purge outside directories whose file listing completed successfully", stderr);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_PurgesDeletedFilesWithinFullyScannedDirectoriesEvenWhenAnotherDirectoryIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            var deletedPath = Path.Combine(projectRoot, "src", "a.cs");
            File.WriteAllText(deletedPath, "public class Deleted { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(deletedPath);
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Equal("secret", json.GetProperty("errors")[0].GetProperty("file").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("src/a.cs", indexedPaths);
            Assert.Contains("secret/a.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_PurgesDeletedRootFileWhenSiblingDirectoryIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            var deletedPath = Path.Combine(projectRoot, "direct.cs");
            File.WriteAllText(deletedPath, "public class Direct { }\n");
            File.WriteAllText(Path.Combine(secretDir, "hidden.cs"), "public class Hidden { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(deletedPath);
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Equal("secret", json.GetProperty("errors")[0].GetProperty("file").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("direct.cs", indexedPaths);
            Assert.Contains("secret/hidden.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_PurgesDeletedFilesWhenUnreadableDescendantExistsUnderSameParentDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var srcDir = Path.Combine(projectRoot, "src");
        var secretDir = Path.Combine(srcDir, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            var deletedPath = Path.Combine(srcDir, "direct.cs");
            File.WriteAllText(deletedPath, "public class Direct { }\n");
            File.WriteAllText(Path.Combine(secretDir, "hidden.cs"), "public class Hidden { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(deletedPath);
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Equal("src/secret", json.GetProperty("errors")[0].GetProperty("file").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("src/direct.cs", indexedPaths);
            Assert.Contains("src/secret/hidden.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_PurgesDeletedFilesWithinDirectoryWhenExtensionlessSiblingProbeFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var srcDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(srcDir);
            var deletedPath = Path.Combine(srcDir, "old.cs");
            var toolPath = Path.Combine(srcDir, "tool");
            File.WriteAllText(deletedPath, "public class Old { }\n");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(deletedPath);
            SetUnixPermissions(toolPath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal("src/tool", json.GetProperty("errors")[0].GetProperty("file").GetString());
            Assert.Equal("Could not probe file for indexability/language.", json.GetProperty("errors")[0].GetProperty("message").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("src/old.cs", indexedPaths);
            Assert.Contains("src/tool", indexedPaths);
        }
        finally
        {
            var toolPath = Path.Combine(projectRoot, "src", "tool");
            if (File.Exists(toolPath))
                SetUnixPermissions(toolPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_OutputReportsReadinessInJsonAndHumanModes()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var (jsonExitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, jsonExitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            var (humanExitCode, output) = RunAndCaptureOutput([projectRoot]);
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("Graph    : ready", output);
            Assert.Contains("Issues   : ready", output);
            Assert.Contains("SQL graph: ready", output);
            Assert.Contains("Hotspots : ready", output);
            Assert.Contains("Fold     : ready", output);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReindexesUnchangedCSharpFilesWhenCanonicalNameContractChanged()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "money.cs"),
                """
                public struct Money
                {
                    public static explicit operator Money(decimal d) => new();
                }

                public class Bag
                {
                    public string this[int index] => "";
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET name = 'explicit' WHERE name = 'explicit operator Money';
                    UPDATE symbols SET name = 'this' WHERE name = 'Item';
                    DELETE FROM codeindex_meta WHERE key = 'csharp_symbol_name_contract_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());
            Assert.True(json.GetProperty("csharp_symbol_name_ready").GetBoolean());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var exactNameCmd = verify.CreateCommand();
            exactNameCmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE name = 'explicit operator Money'";
            Assert.Equal(1L, (long)exactNameCmd.ExecuteScalar()!);

            using var itemCmd = verify.CreateCommand();
            itemCmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE name = 'Item'";
            Assert.Equal(1L, (long)itemCmd.ExecuteScalar()!);

            using var legacyNameCmd = verify.CreateCommand();
            legacyNameCmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE name IN ('explicit', 'this')";
            Assert.Equal(0L, (long)legacyNameCmd.ExecuteScalar()!);

            using var contractCmd = verify.CreateCommand();
            contractCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'csharp_symbol_name_contract_version'";
            Assert.Equal(
                DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contractCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RestampsExtractorVersionWhenOnlyStaleLanguageWasReindexed()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }");
            File.WriteAllText(
                Path.Combine(projectRoot, "lib.py"),
                """
                def target():
                    return 1
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET signature = 'def stale():' WHERE name = 'target';
                    UPDATE codeindex_meta SET value = '0' WHERE key = 'symbol_extractor_version_python';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("summary").GetProperty("files_skipped").GetInt32() > 0);
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var signatureCmd = verify.CreateCommand();
            signatureCmd.CommandText = "SELECT signature FROM symbols WHERE name = 'target'";
            Assert.Equal("def target():", signatureCmd.ExecuteScalar() as string);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'symbol_extractor_version_python'";
            Assert.Equal("1", versionCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReindexesUnchangedSqlFilesWhenSqlGraphContractChanged()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "target.sql"),
                """
                CREATE FUNCTION dbo.fn_Target()
                RETURNS INT
                AS
                BEGIN
                    RETURN 1;
                END;
                GO
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "caller.sql"),
                """
                CREATE PROCEDURE dbo.usp_Caller
                AS
                BEGIN
                    SELECT dbo.fn_Target();
                END;
                GO
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbol_references
                    SET symbol_name = 'fn_Target',
                        symbol_name_folded = 'fn_target',
                        column_number = 1
                    WHERE symbol_name = 'dbo.fn_Target';
                    DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());
            Assert.True(json.GetProperty("sql_graph_contract_ready").GetBoolean());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var referenceCmd = verify.CreateCommand();
            referenceCmd.CommandText = """
                SELECT symbol_name, column_number
                FROM symbol_references
                WHERE container_name = 'dbo.usp_Caller'
                LIMIT 1
                """;
            using var reader = referenceCmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("fn_Target", reader.GetString(0));
            Assert.NotEqual(1L, reader.GetInt64(1));

            using var contractCmd = verify.CreateCommand();
            contractCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'sql_graph_contract_version'";
            Assert.Equal(
                DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contractCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RewritesStaleCSharpExtractorContractForRazorDirectives()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var pagesDir = Path.Combine(projectRoot, "Pages");
            Directory.CreateDirectory(pagesDir);
            var sourcePath = Path.Combine(pagesDir, "Product.razor");
            File.WriteAllText(
                sourcePath,
                """
                @page "/products/{id:int}"
                @implements IDisposable
                @attribute [Authorize]
                @layout MainLayout

                @code {
                    public void Dispose() { }
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    DELETE FROM symbols WHERE kind IN ('route', 'implements', 'attribute', 'layout');
                    UPDATE codeindex_meta
                    SET value = '0'
                    WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var symbolsCmd = verify.CreateCommand();
            symbolsCmd.CommandText = """
                SELECT kind, name
                FROM symbols
                WHERE kind IN ('route', 'implements', 'attribute', 'layout')
                ORDER BY kind, name
                """;
            var symbols = new List<(string Kind, string Name)>();
            using (var reader = symbolsCmd.ExecuteReader())
            {
                while (reader.Read())
                    symbols.Add((reader.GetString(0), reader.GetString(1)));
            }

            Assert.Contains(("attribute", "Authorize"), symbols);
            Assert.Contains(("implements", "IDisposable"), symbols);
            Assert.Contains(("layout", "MainLayout"), symbols);
            Assert.Contains(("route", "/products/{id:int}"), symbols);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = $"SELECT value FROM codeindex_meta WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}'";
            Assert.Equal(
                SymbolExtractor.CSharpContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DegradedWarningSummarizesRemainingFoldGap()
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

            var (exitCode, _, errorOutput) = RunCliInSubprocess([projectRoot], projectRoot);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Index completed with fold-only degraded readiness (fold_ready=false).", errorOutput);
            Assert.Contains("older fold-key version", errorOutput);
            Assert.Contains("cdidx backfill-fold --db", errorOutput);
            Assert.Contains("cdidx index", errorOutput);
            Assert.Contains("--rebuild", errorOutput);
            Assert.Contains("fold_ready=false", errorOutput);
            Assert.DoesNotContain("Run `cdidx status --db", errorOutput);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DoesNotStampFoldReadyWhenLegacyRowsRemain()
    {
        // Codex #86 review regression: on a legacy DB (pre-#86) opened by a new binary, the
        // incremental default of `cdidx index .` skips unchanged files via GetUnchangedFileId.
        // Their old rows stay NULL in name_folded. Stamping FoldReady would flip readers onto
        // the folded-equality path and silently miss those rows. Verify the stamp is withheld.
        // Legacy 行が残っているときに FoldReady が stamp されないことを確認する回帰テスト。
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");

            // Initial index — writes every row with name_folded populated, stamps FoldReady.
            // 初回 index: 全行 folded 付き、FoldReady stamp される。
            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            // Simulate pre-#86 legacy state: wipe folded columns + FoldReady bit on the existing
            // row to model an upgrade from a binary that did not populate name_folded yet.
            // pre-#86 を模擬: folded 列を NULL に戻し、FoldReady bit も落とす。
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE symbols SET name_folded = NULL; UPDATE symbol_references SET symbol_name_folded = NULL, container_name_folded = NULL; PRAGMA user_version = 3";
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // Incremental re-run skips the unchanged file — legacy rows with NULL folded columns
            // still exist, so FoldReady MUST NOT be restamped.
            // 再 index は unchanged file を skip するため legacy 行が残る → FoldReady は立てない。
            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var verifyCmd = verify.CreateCommand();
            verifyCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)verifyCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RestampsHotspotFamilyReadyWhenMarkerFingerprintChanges()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"),
                """
                public partial class Api
                {
                    public void Run() { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"),
                """
                public partial class Api
                {
                    public void Run(int value) { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "Caller.cs"),
                """
                public class Caller
                {
                    public void Call(Api api)
                    {
                        api.Run();
                        api.Run(1);
                    }
                }
                """);

            var (exitCode1, json1) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            Assert.Equal("success", json1.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var seededDb = new DbContext(dbPath))
                Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), seededDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            File.WriteAllText(Path.Combine(projectRoot, "Extra.csproj"), "<Project />");

            var (exitCode2, json2) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode2);
            Assert.Equal("success", json2.GetProperty("status").GetString());

            using var verifyDb = new DbContext(dbPath);
            Assert.Equal(
                DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
            Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"))));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_KeepsCsharpHotspotFamilyTrustWhenOnlyVbMarkersChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Caller.cs"), "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");

            var (initialExitCode, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal("success", initialJson.GetProperty("status").GetString());

            File.WriteAllText(Path.Combine(projectRoot, "Unrelated.vbproj"), "<Project />");

            var (rerunExitCode, rerunJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, rerunExitCode);
            Assert.Equal("success", rerunJson.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var verifyDb = new DbContext(dbPath);
            Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            var (hotspotsExitCode, hotspotsJson) = RunHotspotsJson(dbPath, "csharp", "function");
            Assert.True(hotspotsExitCode is CommandExitCodes.Success or CommandExitCodes.NotFound);
            Assert.True(hotspotsJson.GetProperty("hotspot_family_ready").GetBoolean());
            if (hotspotsJson.TryGetProperty("degraded", out var degraded))
                Assert.False(degraded.GetBoolean());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RestampsHotspotFamilyTrustWhenOnlyMetadataWasCleared()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Caller.cs"), "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");

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

            var (rerunExitCode, rerunJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, rerunExitCode);
            Assert.Equal("success", rerunJson.GetProperty("status").GetString());
            Assert.True(rerunJson.GetProperty("summary").GetProperty("files_skipped").GetInt32() > 0);
            Assert.True(rerunJson.GetProperty("hotspot_family_ready").GetBoolean());

            using (var verifyDb = new DbContext(dbPath))
            {
                Assert.Equal(
                    DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
                Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"))));
            }

            var (hotspotsExitCode, hotspotsJson) = RunHotspotsJson(dbPath, "csharp", "function");
            Assert.Equal(CommandExitCodes.Success, hotspotsExitCode);
            Assert.True(hotspotsJson.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.Equal(2, hotspotsJson.GetProperty("count").GetInt32());
            if (hotspotsJson.TryGetProperty("degraded", out var degraded))
                Assert.False(degraded.GetBoolean());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_MarkerlessMultiSubtreePartialsStaySeparatedInHotspots()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "projA", "src"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "projB", "src"));

            File.WriteAllText(Path.Combine(projectRoot, "projA", "src", "Api.Part1.cs"),
                """
                namespace Shared;

                public partial class Api
                {
                    public void Run()
                    {
                    }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "projA", "src", "Api.Part2.cs"),
                """
                namespace Shared;

                public partial class Api
                {
                    public void Run(int value) { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "projA", "src", "Caller.cs"),
                """
                namespace Shared;

                public class Caller
                {
                    public void Call(Api api)
                    {
                        api.Run();
                    }
                }
                """);

            File.WriteAllText(Path.Combine(projectRoot, "projB", "src", "Api.Part1.cs"),
                """
                namespace Shared;

                public partial class Api
                {
                    public void Run()
                    {
                    }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "projB", "src", "Api.Part2.cs"),
                """
                namespace Shared;

                public partial class Api
                {
                    public void Run(int value) { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "projB", "src", "Caller.cs"),
                """
                namespace Shared;

                public class Caller
                {
                    public void Call(Api api)
                    {
                        api.Run();
                    }
                }
                """);

            var (indexExitCode, indexJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal("success", indexJson.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (hotspotsExitCode, hotspotsJson) = RunHotspotsJsonWithPaths(dbPath, "csharp", "function", ["projA/", "projB/"]);

            Assert.Equal(CommandExitCodes.Success, hotspotsExitCode);
            Assert.True(hotspotsJson.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.Equal(0, hotspotsJson.GetProperty("count").GetInt32());
            Assert.Empty(hotspotsJson.GetProperty("hotspots").EnumerateArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_MarkerlessRootLevelPartialsStayVisibleInHotspots()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "Api.Part1.cs"),
                """
                public partial class Api
                {
                    public void Run() { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "Api.Part2.cs"),
                """
                public partial class Api
                {
                    public void Run(int value) { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "Caller.cs"),
                """
                public class Caller
                {
                    public void Call(Api api)
                    {
                        api.Run();
                        api.Run(1);
                    }
                }
                """);

            var (indexExitCode, indexJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal("success", indexJson.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (hotspotsExitCode, hotspotsJson) = RunHotspotsJson(dbPath, "csharp", "function");

            Assert.Equal(CommandExitCodes.Success, hotspotsExitCode);
            Assert.True(hotspotsJson.GetProperty("hotspot_family_ready").GetBoolean());

            var runRows = hotspotsJson.GetProperty("hotspots")
                .EnumerateArray()
                .Where(item => item.GetProperty("name").GetString() == "Run")
                .ToList();

            var runRow = Assert.Single(runRows);
            Assert.Matches(@"Api\.Part[12]\.cs", runRow.GetProperty("path").GetString() ?? string.Empty);
            Assert.Equal(2, runRow.GetProperty("reference_count").GetInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DoesNotRestampFoldReadyWhenFoldKeyVersionMismatches()
    {
        // Normal non-rebuild `cdidx index .` is still incremental: unchanged rows are skipped.
        // If an existing DB carries old-version fold keys, a full scan must not advertise the
        // new version unless every row is rewritten (that requires --rebuild).
        // 通常の full scan も skip を使うため、旧 version key が残る DB では FoldReady を
        // restamp してはいけない。安全に昇格できるのは --rebuild のみ。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "intl.py"), "def Straße():\n    pass\n");
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
                cmd.CommandText = """
                    UPDATE symbols SET name_folded = 'straße' WHERE name = 'Straße';
                    UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version';
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            // Add a new file so the next non-rebuild scan mixes freshly-written v2 rows with
            // untouched v1-style rows. The run must leave FoldReady off.
            // 新規ファイルを追加して mixed-state を作る。FoldReady は off のままであるべき。
            File.WriteAllText(Path.Combine(projectRoot, "new.cs"), "public class NewFile { }");

            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
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
            Assert.NotEqual(NameFold.Version.ToString(), storedVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DoesNotRestampFoldReadyWhenFoldFingerprintMismatchesAndFilesAreSkipped()
    {
        // #97 codex review: a normal `index .` run still skips unchanged files, so a stale
        // fold_key_fingerprint must not be overwritten with the current runtime fingerprint
        // unless every row was regenerated. Otherwise skipped rows keep old physical keys.
        // #97: 通常の `index .` で unchanged 行が skip される場合、stale fingerprint を
        // current 値へ再 stamp してはいけない。全件再生成できたときだけ trusted に戻せる。
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

            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
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
            Assert.Equal("DEADBEEFDEADBEEF", storedFingerprint);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DoesNotRestampFoldReadyWhenSkippedRowsCarryStaleFoldKeys()
    {
        // Issue #2066: current fold metadata alone is not enough to trust skipped rows.
        // If a legacy/corrupt row carries a non-NULL folded key that no longer matches
        // NameFold.Fold(name), an unchanged full scan must keep FoldReady demoted.
        // Issue #2066: metadata が current でも、skip 行の実 folded key が現在の
        // NameFold.Fold(name) と違うなら FoldReady を回復してはいけない。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class Straße { }");
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
                cmd.CommandText = """
                    PRAGMA user_version = 0;
                    UPDATE symbols SET name_folded = 'straße' WHERE name = 'Straße';
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);

            using var foldedCmd = verify.CreateCommand();
            foldedCmd.CommandText = "SELECT name_folded FROM symbols WHERE name = 'Straße'";
            Assert.Equal("straße", foldedCmd.ExecuteScalar() as string);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RestampsFoldReadyWhenUserVersionWasClearedButFoldMetadataStillMatches()
    {
        // #97 codex review: if a previous refresh cleared user_version before restamping
        // FoldReady, a normal unchanged full scan should recover trust when the stored fold
        // version/fingerprint still match the current runtime and every folded column is
        // already backfilled.
        // #97: 途中中断で user_version だけ落ちた current DB は、fold metadata が current と
        // 一致していれば通常の unchanged full scan で FoldReady を回復できる必要がある。
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
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.NotEqual(0, userVersion & DbContext.FoldReadyFlag);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_version'";
            var storedVersion = versionCmd.ExecuteScalar() as string;
            Assert.Equal(NameFold.Version.ToString(), storedVersion);

            using var fingerprintCmd = verify.CreateCommand();
            fingerprintCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_fingerprint'";
            var storedFingerprint = fingerprintCmd.ExecuteScalar() as string;
            Assert.Equal(NameFold.Fingerprint(), storedFingerprint);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    // Issue #1508: full scans must capture the current HEAD so a subsequent default
    // incremental run after `git switch <branch>` can detect that the DB no longer
    // mirrors the worktree and recommend `--rebuild`.
    // Issue #1508: full scan が HEAD を保存することで、後続の incremental が branch 切替を検知できる。
    [Fact]
    public void Run_FullScan_PersistsCurrentHeadCommit()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");
            var expectedHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var exitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var db = new DbContext(dbPath);
            Assert.Equal(expectedHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_AfterBranchSwitch_JsonReportsHeadChangedAndWarning()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");
            var firstHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            // Branch + commit to advance HEAD without changing on-disk app.cs.
            // ブランチ作成と新規コミットで HEAD だけを動かす。
            RunGit(projectRoot, "checkout", "-b", "feature");
            File.WriteAllText(Path.Combine(projectRoot, "feature.cs"), "public class Feature { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "feature");
            var secondHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            Assert.NotEqual(firstHead, secondHead);

            // A subsequent default incremental full scan should flag the HEAD change.
            // 既定の incremental full scan が HEAD 差分を通知する。
            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("head_changed").GetBoolean());
            Assert.Equal(firstHead, json.GetProperty("prior_indexed_head_commit").GetString());
            Assert.Equal(secondHead, json.GetProperty("current_head_commit").GetString());
            var notice = json.GetProperty("head_change_notice").GetString();
            Assert.NotNull(notice);
            Assert.Contains("--rebuild", notice);

            // After a successful re-scan the HEAD pointer should be updated to the new value.
            // 再スキャン成功後は HEAD が新しい値に更新される。
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var db = new DbContext(dbPath);
            Assert.Equal(secondHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_LegacyDbWithoutCapturedHead_DoesNotReportHeadChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            // Simulate a legacy DB by removing the captured HEAD meta row.
            // legacy DB を再現するため HEAD メタを削除する。
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedHeadCommitMetaKey);
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("head_changed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("prior_indexed_head_commit").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("head_change_notice").ValueKind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanJson_WritesLivenessToStderrOnly()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var (exitCode, json, stderr) = RunAndCaptureJsonWithStderr([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Contains("cdidx: scanning files...", stderr);
            Assert.Contains("cdidx: preparing index writes...", stderr);
            Assert.Contains("cdidx: preparing C# workspace symbols...", stderr);
            Assert.Contains("cdidx: indexed 0/1 file(s)...", stderr);
            Assert.Contains("cdidx: indexed 1/1 file(s)...", stderr);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_NonGitWorkspace_DoesNotReportHeadChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("head_changed").GetBoolean());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_Rebuild_DoesNotReportHeadChangeEvenIfHeadDiffers()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            RunGit(projectRoot, "checkout", "-b", "feature");
            File.WriteAllText(Path.Combine(projectRoot, "feature.cs"), "public class Feature { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "feature");

            // --rebuild already wipes the DB, so HEAD divergence is irrelevant on that path.
            // --rebuild は DB を消すため HEAD 差分の警告は不要。
            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--rebuild", "--yes", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("head_changed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("head_change_notice").ValueKind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }
}
