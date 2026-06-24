using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunFilesAndSearch_IndexRepositoryConfigurationFiles_Issue3898()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_config_files_issue3898");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "tests"));
            Directory.CreateDirectory(Path.Combine(projectRoot, ".codex", "rules"));
            File.WriteAllText(
                Path.Combine(projectRoot, "nuget.config"),
                """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="signatureValidationMode" value="require" />
                  </config>
                </configuration>
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "tests", "CodeIndex.Tests.runsettings"),
                """
                <?xml version="1.0" encoding="utf-8"?>
                <RunSettings>
                  <RunConfiguration>
                    <TestSessionTimeout>2700000</TestSessionTimeout>
                  </RunConfiguration>
                </RunSettings>
                """);
            File.WriteAllText(Path.Combine(projectRoot, ".gitattributes"), "*.cs text eol=lf\n");
            File.WriteAllText(
                Path.Combine(projectRoot, ".codex", "rules", "codeindex.rules"),
                "prefix_rule(pattern = [\"rg\"], decision = \"forbidden\")\n");

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertIndexedFilePath(dbPath, "nuget.config", "xml");
            AssertIndexedFilePath(dbPath, "tests/CodeIndex.Tests.runsettings", "xml");
            AssertIndexedFilePath(dbPath, ".gitattributes", "gitattributes");
            AssertIndexedFilePath(dbPath, ".codex/rules/codeindex.rules", "config");
            AssertSearchesPath(dbPath, "signatureValidationMode", "nuget.config");
            AssertSearchesPath(dbPath, "TestSessionTimeout", "tests/CodeIndex.Tests.runsettings");
            AssertSearchesPath(dbPath, "prefix_rule", ".codex/rules/codeindex.rules");
            AssertSearchesPath(dbPath, "eol", ".gitattributes");

            var (statusExitCode, statusStdout, statusStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));
            using var statusDocument = ParseJsonOutput(statusStdout);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Equal(string.Empty, statusStderr);
            Assert.Equal(0, statusDocument.RootElement.GetProperty("unknown_extension_file_count").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CanceledToken_RethrowsInsteadOfDatabaseError_Issue3723()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_cancel_issue3723");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions,
                cancellationToken: cancellation.Token));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_MissingSinceValueShowsPerFlagHint_Issue1507()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(["--since"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: --since requires a value.", stderr);
        Assert.Contains("Hint: pass an ISO 8601 datetime", stderr);
        Assert.Contains("--since 2024-01-01", stderr);
    }

    private void AssertIndexedFilePath(string dbPath, string path, string language)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
            ["--db", dbPath, "--json", "--path", path],
            _jsonOptions));
        using var document = ParseJsonOutput(stdout);
        var root = document.RootElement;

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(path, root.GetProperty("path").GetString());
        Assert.Equal(language, root.GetProperty("lang").GetString());
    }

    private void AssertSearchesPath(string dbPath, string query, string path)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            [query, "--db", dbPath, "--json=array", "--path", path],
            _jsonOptions));
        using var document = ParseJsonOutput(stdout);
        var results = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.NotEmpty(results);
        Assert.Contains(results, result => result.GetProperty("path").GetString() == path);
    }

    [Fact]
    public void RunFiles_PathFilterAcceptsLeadingDashValue()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_files_path_leading_dash");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--path", "-foo", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("0", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_ExcludePathFilterAcceptsLeadingDashValue()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_files_exclude_path_leading_dash");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--exclude-path", "-foo", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("0", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_PathFilterAcceptsRecognizedOptionTokenViaInlineValue()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_files_path_inline_recognized_option");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/--json-dir/Demo.cs",
                "csharp",
                "class Demo {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                [$"--db={dbPath}", "--path=--json-dir", "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_ExcludePathFilterAcceptsRecognizedOptionTokenViaInlineValue()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_files_exclude_path_inline_recognized_option");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/--count-dir/Demo.cs",
                "csharp",
                "class Demo {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                [$"--db={dbPath}", "--exclude-path=--count-dir", "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsHotspotFamilyTrustSignals()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_hotspots_family_signal");
        try
        {
            var dbPath = CreateHotspotFamilyFixtureDb(projectRoot, markHotspotFamilyReady: false);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.Contains("csharp", json.GetProperty("hotspot_family_degraded_reason").GetString());
            Assert.Contains("DEGRADED", json.GetProperty("summary").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsHookCandidatesWithoutLoadingAssemblies_3142()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_hook_metadata_3142");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable);
            try
            {
                var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                var hookPath = Path.Combine(hooksDir, "broken.dll");
                File.WriteAllText(hookPath, "not a real assembly");
                env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);

                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                    ["--db", dbPath, "--json"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                var hook = Assert.Single(document.RootElement.GetProperty("hooks").EnumerateArray());
                Assert.Equal("broken", hook.GetProperty("name").GetString());
                Assert.EndsWith("broken.dll", hook.GetProperty("assembly_path").GetString(), StringComparison.Ordinal);
                Assert.Equal(string.Empty, hook.GetProperty("type_name").GetString());
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsHookDiscoveryLimitDiagnostics_3456()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_hook_cap_3456");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(
                PostExtractionHookRunner.HooksDirectoryEnvironmentVariable,
                PostExtractionHookRunner.DiscoveryLimitEnvironmentVariable);
            try
            {
                var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.WriteAllText(Path.Combine(hooksDir, "a.dll"), "not a real assembly");
                File.WriteAllText(Path.Combine(hooksDir, "b.dll"), "not a real assembly");
                File.WriteAllText(Path.Combine(hooksDir, "c.dll"), "not a real assembly");
                env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);
                env.Set(PostExtractionHookRunner.DiscoveryLimitEnvironmentVariable, "2");

                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                    ["--db", dbPath, "--json"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                Assert.Equal(2, document.RootElement.GetProperty("hooks").GetArrayLength());
                var diagnostic = Assert.Single(
                    document.RootElement.GetProperty("hook_diagnostics").EnumerateArray(),
                    item => item.GetProperty("message").GetString()!.Contains("candidate limit", StringComparison.Ordinal));
                Assert.EndsWith("hooks", diagnostic.GetProperty("assembly_path").GetString(), StringComparison.Ordinal);
                Assert.DoesNotContain(projectRoot, diagnostic.GetProperty("assembly_path").GetString(), StringComparison.Ordinal);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsAcceptedExtensionTrustOverrides_3735()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_trust_overrides_3735");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(
                PostExtractionHookRunner.HooksDirectoryEnvironmentVariable,
                ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            try
            {
                var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "true");

                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                    ["--db", dbPath, "--json"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                var trustOverrides = document.RootElement.GetProperty("trust_overrides").EnumerateArray().ToArray();
                Assert.Equal(2, trustOverrides.Length);

                var pluginOverride = Assert.Single(
                    trustOverrides,
                    item => item.GetProperty("kind").GetString() == "workspace_plugin_directory");
                Assert.Equal(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, pluginOverride.GetProperty("environment_variable").GetString());
                Assert.Equal("true", pluginOverride.GetProperty("value").GetString());
                Assert.EndsWith(".cdidx/plugins", pluginOverride.GetProperty("path").GetString(), StringComparison.Ordinal);
                Assert.DoesNotContain(projectRoot, pluginOverride.GetProperty("path").GetString(), StringComparison.Ordinal);
                Assert.Contains("workspace plugin", pluginOverride.GetProperty("message").GetString(), StringComparison.Ordinal);

                var hookOverride = Assert.Single(
                    trustOverrides,
                    item => item.GetProperty("kind").GetString() == "hook_directory_override");
                Assert.Equal(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hookOverride.GetProperty("environment_variable").GetString());
                Assert.EndsWith("hooks", hookOverride.GetProperty("value").GetString(), StringComparison.Ordinal);
                Assert.EndsWith("hooks", hookOverride.GetProperty("path").GetString(), StringComparison.Ordinal);
                Assert.DoesNotContain(projectRoot, hookOverride.GetProperty("value").GetString(), StringComparison.Ordinal);
                Assert.DoesNotContain(projectRoot, hookOverride.GetProperty("path").GetString(), StringComparison.Ordinal);
                Assert.Contains("hook assemblies execute", hookOverride.GetProperty("message").GetString(), StringComparison.Ordinal);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void RunStatus_Json_CapsSymbolKindCountsAndNames_3134()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_symbol_kind_caps_3134");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            InsertStatusKindCapFixture(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var symbolKinds = json.GetProperty("symbol_kinds");
            var symbolsByLanguage = json.GetProperty("symbols_by_language").GetProperty("csharp");

            Assert.Equal(QueryCommandRunner.MaxStatusSymbolKindEntries, symbolKinds.EnumerateObject().Count());
            Assert.Equal(QueryCommandRunner.MaxStatusSymbolKindEntries, symbolsByLanguage.EnumerateObject().Count());
            Assert.Equal(QueryCommandRunner.MaxStatusSymbolKindEntries, json.GetProperty("symbol_kind_limit").GetInt32());
            Assert.Equal(QueryCommandRunner.MaxStatusSymbolKindNameLength, json.GetProperty("symbol_kind_name_limit").GetInt32());
            Assert.Equal(SymbolKindCatalog.SymbolKinds.Length + 1, json.GetProperty("symbol_kind_total_count").GetInt32());
            Assert.Equal(SymbolKindCatalog.SymbolKinds.Length + 1 - QueryCommandRunner.MaxStatusSymbolKindEntries, json.GetProperty("symbol_kind_omitted_count").GetInt32());
            Assert.True(json.GetProperty("symbol_kind_names_truncated").GetBoolean());
            Assert.Equal(SymbolKindCatalog.SymbolKinds.Length + 1, json.GetProperty("symbols_by_language_kind_total_counts").GetProperty("csharp").GetInt32());
            Assert.Equal(SymbolKindCatalog.SymbolKinds.Length + 1 - QueryCommandRunner.MaxStatusSymbolKindEntries, json.GetProperty("symbols_by_language_kind_omitted_counts").GetProperty("csharp").GetInt32());
            Assert.Contains("csharp", json.GetProperty("symbols_by_language_kind_names_truncated").EnumerateArray().Select(value => value.GetString()));
            Assert.Contains(symbolKinds.EnumerateObject(), property => property.Name.EndsWith("...", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Human_MarksOmittedSymbolKinds_3134()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_symbol_kind_human_3134");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            InsertStatusKindCapFixture(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("Kinds:", stdout, StringComparison.Ordinal);
            Assert.Contains("kinds omitted (limit 32, names capped at 64 chars)", stdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsFoldOnlyRemediationHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_fold_only_json");
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version'";
                cmd.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("stale_fold_key_version", json.GetProperty("fold_ready_reason").GetString());
            Assert.Contains("older fold-key version", json.GetProperty("degraded_reason").GetString());
            Assert.Contains("cdidx backfill-fold --db", json.GetProperty("recommended_action").GetString());
            Assert.Contains("--rebuild", json.GetProperty("alternative_action").GetString());
            Assert.Contains("DEGRADED", json.GetProperty("summary").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static void InsertStatusKindCapFixture(string dbPath)
    {
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/StatusKinds.cs",
            Lang = "csharp",
            Size = 1,
            Lines = 1,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = "status-kind-cap-fixture",
        });

        writer.InsertSymbols(SymbolKindCatalog.SymbolKinds
            .Select((kind, index) => new SymbolRecord
            {
                FileId = fileId,
                Kind = kind,
                Name = $"S{index}",
                Line = index + 1,
                StartLine = index + 1,
                EndLine = index + 1,
            })
            .ToList());

        using var pragma = db.Connection.CreateCommand();
        pragma.CommandText = "PRAGMA ignore_check_constraints=ON";
        pragma.ExecuteNonQuery();

        using var insert = db.Connection.CreateCommand();
        insert.CommandText = @"
            INSERT INTO symbols (file_id, kind, name, line, start_line, end_line)
            VALUES (@file_id, @kind, @name, @line, @line, @line)";
        var longKind = new string('k', QueryCommandRunner.MaxStatusSymbolKindNameLength + 16);
        for (var i = 0; i < 4; i++)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("@file_id", fileId);
            insert.Parameters.AddWithValue("@kind", longKind);
            insert.Parameters.AddWithValue("@name", $"LongKind{i}");
            insert.Parameters.AddWithValue("@line", SymbolKindCatalog.SymbolKinds.Length + i + 1);
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsWorktreeHeadChangedWhenIndexedHeadDiffersFromRuntime()
    {
        // #1512: after `git worktree add` / `git switch` inside a worktree, the runtime HEAD
        // diverges from the HEAD captured at index time. status JSON must surface that so MCP /
        // automation clients can warn before issuing further queries against a stale index.
        // #1512: worktree branch / HEAD 切替後、runtime HEAD は index 時点の HEAD と乖離する。
        // status JSON でこれを surface し、後続クエリ前に stale を検知可能にする。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_worktree_head_changed_json");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var staleHead = new string('b', 40);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, staleHead);
                writer.SetMeta(DbContext.IndexedHeadShaMetaKey, staleHead);
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(staleHead, json.GetProperty("indexed_head_commit").GetString());
            Assert.Equal(staleHead, json.GetProperty("indexed_head_sha").GetString());
            Assert.True(json.GetProperty("worktree_head_changed").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsWorktreeHeadChangedWhenBranchBecomesDetachedAtSameCommit()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_detached_head_changed_json");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var indexedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            var indexedBranch = TestProjectHelper.RunGit(projectRoot, "rev-parse", "--abbrev-ref", "HEAD").Trim();
            TestProjectHelper.RunGit(projectRoot, "checkout", "--detach", indexedHead);

            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, indexedHead);
                writer.SetMeta(DbContext.IndexedHeadCommitBranchMetaKey, indexedBranch);
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(indexedHead, json.GetProperty("indexed_head_commit").GetString());
            Assert.Equal(indexedHead, json.GetProperty("indexed_head_sha").GetString());
            Assert.True(json.GetProperty("worktree_head_changed").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_DoesNotReportDetachedHeadChangedAfterPartialUpdateAtSameCommit()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_detached_head_after_partial_json");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var indexedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            TestProjectHelper.RunGit(projectRoot, "checkout", "--detach", indexedHead);
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() {} }\n");

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--files", "app.cs", "--json", "--quiet"], _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(indexedHead, json.GetProperty("indexed_head_commit").GetString());
            Assert.Equal(indexedHead, json.GetProperty("indexed_head_sha").GetString());
            Assert.False(json.GetProperty("worktree_head_changed").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_HumanOutput_WarnsWhenWorktreeHeadHasSwitchedSinceIndex()
    {
        // #1512: surface a `WARN` line plus an Indexed-HEAD echo and a ready-to-run reindex
        // command when the runtime HEAD no longer matches the HEAD captured at index time.
        // #1512: index 時点と runtime の HEAD が異なるとき、`WARN` 行と Indexed-HEAD・再 index コマンド
        // を出力する。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_worktree_head_changed_human");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var staleHead = new string('c', 40);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, staleHead);
                writer.SetMeta(DbContext.IndexedHeadShaMetaKey, staleHead);
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("WARN     : worktree HEAD changed since the index was built", stdout);
            Assert.Contains(staleHead[..12], stdout);
            Assert.Contains("cdidx index", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_HumanOutput_WarnsWhenOnlyFoldReadinessIsDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_fold_only_human");
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version'";
                cmd.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("older fold-key version", stdout);
            Assert.Contains("Hint     : run `cdidx backfill-fold --db", stdout);
            Assert.Contains("Hint     : or run `cdidx index", stdout);
            Assert.Contains("--rebuild", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_ReadOnlyUriFoldRemediationUsesWritableDbPath()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_fold_only_uri");
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version'";
                cmd.ExecuteNonQuery();

                using var checkpoint = db.Connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", readOnlyUri, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("stale_fold_key_version", json.GetProperty("fold_ready_reason").GetString());
            Assert.Contains(dbPath, json.GetProperty("recommended_action").GetString());
            Assert.Contains(dbPath, json.GetProperty("alternative_action").GetString());
            Assert.DoesNotContain("immutable=1", json.GetProperty("recommended_action").GetString());
            Assert.DoesNotContain("immutable=1", json.GetProperty("alternative_action").GetString());
            Assert.DoesNotContain("file:", json.GetProperty("recommended_action").GetString());
            Assert.DoesNotContain("file:", json.GetProperty("alternative_action").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RunStatus_Json_RelativeReadOnlyUriFoldRemediationUsesWorkingDirectoryDbPath()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_fold_only_relative_uri_json");
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var dbDirectory = Path.GetDirectoryName(dbPath)!;
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version'";
                cmd.ExecuteNonQuery();

                using var checkpoint = db.Connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var (exitCode, stdout, stderr) = RunBuiltCli(
                ["status", "--db", "file:codeindex.db?immutable=1", "--json"],
                dbDirectory);

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("stale_fold_key_version", json.GetProperty("fold_ready_reason").GetString());
            Assert.Contains(dbPath, json.GetProperty("recommended_action").GetString());
            Assert.Contains(dbPath, json.GetProperty("alternative_action").GetString());
            Assert.DoesNotContain("<writable-db-path>", json.GetProperty("recommended_action").GetString());
            Assert.DoesNotContain("<writable-db-path>", json.GetProperty("alternative_action").GetString());
            Assert.DoesNotContain("file:", json.GetProperty("recommended_action").GetString());
            Assert.DoesNotContain("file:", json.GetProperty("alternative_action").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RunStatus_HumanOutput_RelativeReadOnlyUriUsesWorkingDirectoryDbPath()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_fold_only_relative_uri_human");
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var dbDirectory = Path.GetDirectoryName(dbPath)!;
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version'";
                cmd.ExecuteNonQuery();

                using var checkpoint = db.Connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var (exitCode, stdout, stderr) = RunBuiltCli(
                ["status", "--db", "file:codeindex.db?mode=ro"],
                dbDirectory);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains(dbPath, stdout);
            Assert.Contains("cdidx backfill-fold --db", stdout);
            Assert.Contains("cdidx index", stdout);
            Assert.DoesNotContain("<writable-db-path>", stdout);
            Assert.DoesNotContain("file:codeindex.db", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RunStatus_HumanOutput_WarnsWhenHotspotFamilyTrustIsDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_hotspots_family_human");
        try
        {
            var dbPath = CreateHotspotFamilyFixtureDb(projectRoot, markHotspotFamilyReady: false);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("cross-file hotspot family grouping", stdout);
            Assert.Contains("authoritative cross-file hotspot families", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_ReportsDegradedSqlGraphContractTrust()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);

            var (jsonExitCode, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using (var jsonDocument = ParseJsonOutput(jsonStdout))
            {
                var json = jsonDocument.RootElement;
                Assert.Equal(CommandExitCodes.Success, jsonExitCode);
                Assert.Equal(string.Empty, jsonStderr);
                Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
                Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
                Assert.Contains("DEGRADED", json.GetProperty("summary").GetString());
            }

            var (humanExitCode, humanStdout, humanStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Equal(string.Empty, humanStderr);
            Assert.Contains("SQL graph/dependency results may be stale.", humanStdout);
            Assert.Contains(Path.GetFullPath(projectRoot), humanStdout);
            Assert.Contains(Path.GetFullPath(dbPath), humanStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_ZeroJson_OnEmptyIndex_EmitsNullIndexedAt()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_zero_json_empty_index");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["definitely-missing-path", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("files").GetArrayLength());
            Assert.Equal(0, json.GetProperty("indexed_file_count").GetInt64());
            Assert.True(json.GetProperty("freshness_available").GetBoolean());
            Assert.True(json.TryGetProperty("indexed_at", out var indexedAt));
            Assert.Equal(JsonValueKind.Null, indexedAt.ValueKind);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_ZeroJson_OnLegacyReadOnlyDb_EmitsFreshnessDegradedSignal()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_zero_json_legacy_freshness");
        try
        {
            var dbPath = CreateLegacyDbWithoutIndexedAt(projectRoot);
            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["definitely-missing-path", "--db", readOnlyUri, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("files").GetArrayLength());
            Assert.Equal(1, json.GetProperty("indexed_file_count").GetInt64());
            Assert.False(json.GetProperty("freshness_available").GetBoolean());
            Assert.Contains("files.indexed_at column missing", json.GetProperty("freshness_degraded_reason").GetString());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("indexed_at").ValueKind);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_ZeroResultJson_EmitsStructuredPayloadWithFreshnessHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_zero_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing-file-fragment", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("files").GetArrayLength());
            Assert.Equal(1, json.GetProperty("indexed_file_count").GetInt32());
            Assert.True(json.TryGetProperty("indexed_at", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_ZeroResultJson_EmptyIndexEmitsNullIndexedAt()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_zero_json_empty");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing-file-fragment", "--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("files").GetArrayLength());
            Assert.Equal(0, json.GetProperty("indexed_file_count").GetInt32());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("indexed_at").ValueKind);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_ZeroResultJson_CountOnlyEmitsFreshnessHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_zero_json_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing-file-fragment", "--db", dbPath, "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("indexed_file_count").GetInt32());
            Assert.True(json.TryGetProperty("indexed_at", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_ZeroResultJson_CountOnlyEmptyIndexEmitsNullIndexedAt()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_zero_json_count_empty");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing-file-fragment", "--db", dbPath, "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("indexed_file_count").GetInt32());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("indexed_at").ValueKind);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_HumanReadableIncludesGitMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.LastIndexRunStartedAtMetaKey, "2030-01-02T03:04:05.0000000Z");
            }

            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("Files    : 1", stdout);
            Assert.Contains("Freshened: 2030-01-02T03:04:05.0000000Z", stdout);
            Assert.Contains($"Git HEAD : {expectedHead}", stdout);
            Assert.Contains("Git Dirty: True", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsLastWorkspaceFreshenedAt()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_freshened_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.LastIndexRunStartedAtMetaKey, "2030-01-02T03:04:05.0000000Z");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            Assert.Equal("2030-01-02T03:04:05Z", json.GetProperty("last_workspace_freshened_at").GetString());
            Assert.NotEqual(
                json.GetProperty("indexed_at").GetString(),
                json.GetProperty("last_workspace_freshened_at").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_HumanOutput_TranslatesReadinessFields()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_readiness");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("Readiness:", stdout);
            Assert.Contains("Reference graph table", stdout);
            Assert.Contains("Validation issues data", stdout);
            Assert.Contains("Unicode exact-name fold contract", stdout);
            Assert.Contains("C# metadata target contract", stdout);
            Assert.Contains("cdidx backfill-fold", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Explain_PrintsReadinessFieldDescriptionWithoutDatabase()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "fold_ready"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Unicode exact-name fold contract (fold_ready)", stdout);
        Assert.Contains("Ready:", stdout);
        Assert.Contains("Degraded:", stdout);
        Assert.Contains("Remediation:", stdout);
        Assert.Contains("cdidx backfill-fold", stdout);
    }

    [Fact]
    public void RunStatus_Explain_PrintsVisibleStatusFieldDescriptionWithoutDatabase_Issue3936()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "path_case_sensitive"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Filesystem case sensitivity (path_case_sensitive)", stdout);
        Assert.Contains("case-sensitive", stdout);
        Assert.Contains("case-insensitive", stdout);
        Assert.Contains("cdidx index <projectPath>", stdout);
    }

    [Fact]
    public void RunStatus_Explain_PrintsHeadFreshnessFieldDescriptionWithoutDatabase_Issue3911()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "indexed_head_commit"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Legacy full-scan HEAD stamp (indexed_head_commit)", stdout);
        Assert.Contains("full-scan-only", stdout);
        Assert.Contains("indexed_head_sha", stdout);
    }

    [Fact]
    public void RunStatus_Explain_RejectsUnknownStatusField()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "nope"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("unknown status field", stderr);
        Assert.Contains("fold_ready", stderr);
        Assert.Contains("path_case_sensitive", stderr);
    }

    [Fact]
    public void RunStatus_ExplainJson_PrintsMachineReadableDescription()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "fold_ready", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        Assert.Equal("1", json.GetProperty("api_version").GetString());
        Assert.Equal("fold_ready", json.GetProperty("field").GetString());
        Assert.Equal("Unicode exact-name fold contract", json.GetProperty("label").GetString());
        Assert.Contains("Unicode NFKC", json.GetProperty("ready").GetString());
        Assert.Contains("ASCII COLLATE NOCASE", json.GetProperty("degraded").GetString());
        Assert.Contains("cdidx backfill-fold", json.GetProperty("remediation").GetString());
        Assert.Contains("fold_ready", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("indexed_head_sha", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("indexed_head_commit", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("path_case_sensitive", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void RunStatus_ExplainJson_PrintsHeadFreshnessFieldDescription_Issue3911()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "indexed_head_sha", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        Assert.Equal("indexed_head_sha", json.GetProperty("field").GetString());
        Assert.Equal("Latest index HEAD stamp", json.GetProperty("label").GetString());
        Assert.Contains("including incremental updates", json.GetProperty("ready").GetString());
        Assert.Contains("indexed_head_commit", json.GetProperty("remediation").GetString());
    }

    [Theory]
    [InlineData("~/cdidx-logs", "cdidx-logs")]
    [InlineData("$HOME/cdidx-logs", "cdidx-logs")]
    [InlineData("${HOME}/cdidx-logs", "cdidx-logs")]
    public void RunStatus_LogPath_ExpandsUserHomeOverrides(string overrideValue, string childDirectory)
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            "XDG_STATE_HOME",
            "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR");
        env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", overrideValue);
        env.Set("XDG_STATE_HOME", null);
        env.Set("XDG_CACHE_HOME", null);
        env.Set("XDG_RUNTIME_DIR", null);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--log-path"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, childDirectory)), stdout.Trim());
    }

    [Fact]
    public void RunStatus_LogPath_JsonPrintsResolvedDirectoryWithoutDatabase()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_status_log_path_{Guid.NewGuid():N}");
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            "XDG_STATE_HOME",
            "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR");
        env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
        env.Set("XDG_STATE_HOME", null);
        env.Set("XDG_CACHE_HOME", null);
        env.Set("XDG_RUNTIME_DIR", null);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--log-path", "--json"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(Path.GetFullPath(logDir), document.RootElement.GetProperty("log_path").GetString());
    }

    [Fact]
    public void RunStatus_LogPath_JsonHonorsXdgCacheHome()
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            "XDG_STATE_HOME",
            "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR");
        var cacheHome = Path.Combine(Path.GetTempPath(), $"cdidx_status_log_path_xdg_cache_{Guid.NewGuid():N}");
        try
        {
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", null);
            env.Set("XDG_STATE_HOME", null);
            env.Set("XDG_CACHE_HOME", cacheHome);
            env.Set("XDG_RUNTIME_DIR", Path.Combine(Path.GetTempPath(), "ignored-runtime"));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--log-path", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(Path.Combine(cacheHome, "cdidx", "logs"), document.RootElement.GetProperty("log_path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(cacheHome);
        }
    }

    [Fact]
    public void RunStatus_Json_UsesIndexedAndSourceFreshnessInsteadOfClockAge()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_freshness");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var indexedAt = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n", modified);
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "UPDATE files SET indexed_at = @indexed_at WHERE path = @path";
                cmd.Parameters.AddWithValue("@indexed_at", indexedAt);
                cmd.Parameters.AddWithValue("@path", "src/app.cs");
                cmd.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("index fresh", json.GetProperty("summary").GetString());
            Assert.DoesNotContain("index stale", json.GetProperty("summary").GetString());
            var pragmas = json.GetProperty("db_pragma_settings");
            Assert.Equal("wal", pragmas.GetProperty("journal_mode").GetString());
            Assert.Equal(DbContext.DefaultSynchronousMode, pragmas.GetProperty("synchronous").GetString());
            Assert.Equal(DbContext.DefaultWalAutocheckpointPages, pragmas.GetProperty("wal_autocheckpoint").GetInt32());
            Assert.Equal(DbPragmaPolicy.DefaultBusyTimeoutMs, pragmas.GetProperty("busy_timeout_ms").GetInt32());
            var preparedCommandCache = json.GetProperty("prepared_command_cache");
            Assert.Equal(PreparedCommandCache.DefaultCapacity, preparedCommandCache.GetProperty("capacity").GetInt32());
            Assert.True(preparedCommandCache.GetProperty("count").GetInt32() >= 0);
            Assert.True(preparedCommandCache.GetProperty("miss_count").GetInt64() >= 0);
            Assert.True(preparedCommandCache.GetProperty("hit_count").GetInt64() >= 0);
            Assert.True(preparedCommandCache.GetProperty("eviction_count").GetInt64() >= 0);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_UsesSourceNewerThanIndexAsStale()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_stale");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var modified = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            var indexedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n", modified);
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "UPDATE files SET indexed_at = @indexed_at WHERE path = @path";
                cmd.Parameters.AddWithValue("@indexed_at", indexedAt);
                cmd.Parameters.AddWithValue("@path", "src/app.cs");
                cmd.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("index stale", json.GetProperty("summary").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsStructuredGuidanceForMultipleReadinessDegradations()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_multi_degraded");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkBatchInProgress();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var degradations = json.GetProperty("readiness_degradations");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Last batch did not complete", stderr);
            Assert.True(json.GetProperty("migration_in_progress").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("file_issues_data_current").GetBoolean());
            Assert.Equal("migration_in_progress", json.GetProperty("degraded_root_cause").GetString());
            Assert.Contains("DEGRADED", json.GetProperty("summary").GetString());
            Assert.Contains(degradations.EnumerateArray(), item =>
                item.GetProperty("field").GetString() == "migration_in_progress"
                && item.GetProperty("root_cause").GetString() == "migration_in_progress");
            Assert.Contains(degradations.EnumerateArray(), item =>
                item.GetProperty("field").GetString() == "file_issues_data_current"
                && item.GetProperty("root_cause").GetString() == "file_issues_data_current=false");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_ReturnsSuccessWhenIndexMatchesWorkspace()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_match");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            MarkStatusReadinessReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var check = json.GetProperty("workspace_check");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("index_matches_workspace").GetBoolean());
            Assert.True(check.GetProperty("checked").GetBoolean());
            Assert.True(check.GetProperty("matches_workspace").GetBoolean());
            Assert.Equal("matched", check.GetProperty("reason").GetString());
            Assert.Equal(1, check.GetProperty("matched_file_count").GetInt32());
            Assert.Contains("index fresh", json.GetProperty("summary").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_ReportsEffectiveStaleThreshold()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_stale_after_json");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            MarkStatusReadinessReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--stale-after", "30m", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(30 * 60, json.GetProperty("stale_after_seconds").GetInt64());
            Assert.True(json.GetProperty("index_age_seconds").GetInt64() >= 0);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckHuman_PrintsEffectiveStaleThreshold()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_stale_after_human");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            MarkStatusReadinessReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--stale-after", "7d"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("threshold: 7d", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_IgnoresInvalidStaleAfterEnvironmentWithoutCheck()
    {
        var prior = Environment.GetEnvironmentVariable(QueryCommandRunner.StaleAfterEnvironmentVariable);
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_stale_after_env_plain");
        try
        {
            Environment.SetEnvironmentVariable(QueryCommandRunner.StaleAfterEnvironmentVariable, "bad-value");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.TryGetProperty("stale_after_seconds", out _));
            Assert.False(json.TryGetProperty("index_age_seconds", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(QueryCommandRunner.StaleAfterEnvironmentVariable, prior);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_ReturnsStaleIndexWhenContentChecksumDiffers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_changed");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            MarkStatusReadinessReady(dbPath);
            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var check = json.GetProperty("workspace_check");

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("index_matches_workspace").GetBoolean());
            Assert.Equal("workspace_stale", json.GetProperty("failed_checks")[0].GetString());
            Assert.True(check.GetProperty("checked").GetBoolean());
            Assert.False(check.GetProperty("matches_workspace").GetBoolean());
            Assert.Equal("changed_files", check.GetProperty("reason").GetString());
            Assert.Equal(1, check.GetProperty("changed_file_count").GetInt32());
            Assert.Equal("src/app.cs", check.GetProperty("changed_files")[0].GetString());
            var repairCommand = Assert.Single(json.GetProperty("repair_commands").EnumerateArray());
            Assert.Equal("cdidx", repairCommand.GetProperty("name").GetString());
            Assert.Equal("workspace_stale", repairCommand.GetProperty("reason").GetString());
            var repairArgs = repairCommand.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()).ToArray();
            Assert.Contains("index", repairArgs);
            Assert.Contains(projectRoot, repairArgs);
            Assert.Contains("--db", repairArgs);
            Assert.Contains(dbPath, repairArgs);
            Assert.NotEmpty(repairCommand.GetProperty("safety_notes").EnumerateArray());
            Assert.Contains("index stale", json.GetProperty("summary").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_DetectsMissingAndUnindexedFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_paths");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "new.cs"), "class NewFile {}\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/old.cs", "csharp", "class OldFile {}\n");
            MarkStatusReadinessReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var check = document.RootElement.GetProperty("workspace_check");

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(document.RootElement.GetProperty("index_matches_workspace").GetBoolean());
            Assert.Equal("workspace_stale", document.RootElement.GetProperty("failed_checks")[0].GetString());
            Assert.Equal(1, check.GetProperty("missing_file_count").GetInt32());
            Assert.Equal(1, check.GetProperty("unindexed_file_count").GetInt32());
            Assert.Equal("src/old.cs", check.GetProperty("missing_files")[0].GetString());
            Assert.Equal("src/new.cs", check.GetProperty("unindexed_files")[0].GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_ReportsBackfillRepairCommandForFoldDegradation_Issue3567()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_repair_fold");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkIssuesReady();
                writer.MarkCSharpSymbolNameContractReady();
                writer.MarkMetadataTargetReady("csharp");
                writer.MarkSqlGraphContractReady();
                writer.MarkHotspotFamilyReady("csharp", "test");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var repairCommand = Assert.Single(json.GetProperty("repair_commands").EnumerateArray());
            var repairArgs = repairCommand.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()).ToArray();

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("fold_ready", json.GetProperty("failed_checks")[0].GetString());
            Assert.Equal("cdidx", repairCommand.GetProperty("name").GetString());
            Assert.Equal("fold_ready", repairCommand.GetProperty("reason").GetString());
            Assert.Equal("backfill-fold", repairArgs[0]);
            Assert.Contains("--db", repairArgs);
            Assert.Contains(dbPath, repairArgs);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_ReportsLastFailedOrPartialIndexRun_Issue3567()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_failed_run");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            MarkStatusReadinessReady(dbPath);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.LastFailedIndexRunStatusMetaKey, "partial");
                writer.SetMeta(DbContext.LastFailedIndexRunModeMetaKey, "update");
                writer.SetMeta(DbContext.LastFailedIndexRunStartedAtMetaKey, "2026-06-11T00:00:00.0000000Z");
                writer.SetMeta(DbContext.LastFailedIndexRunDurationMsMetaKey, "1234");
                writer.SetMeta(DbContext.LastFailedIndexRunFilesProcessedMetaKey, "3");
                writer.SetMeta(DbContext.LastFailedIndexRunFilesTotalMetaKey, "9");
                writer.SetMeta(DbContext.LastFailedIndexRunErrorCodeMetaKey, CommandErrorCodes.Interrupted);
                writer.SetMeta(DbContext.LastFailedIndexRunReasonMetaKey, "interrupted");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var run = document.RootElement.GetProperty("last_failed_or_partial_index_run");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("partial", run.GetProperty("status").GetString());
            Assert.Equal("update", run.GetProperty("mode").GetString());
            Assert.Equal(1234, run.GetProperty("duration_ms").GetInt64());
            Assert.Equal(3, run.GetProperty("files_processed").GetInt64());
            Assert.Equal(9, run.GetProperty("files_total").GetInt64());
            Assert.Equal(CommandErrorCodes.Interrupted, run.GetProperty("error_code").GetString());
            Assert.Equal("interrupted", run.GetProperty("reason").GetString());
            Assert.False(run.TryGetProperty("exception", out _));
            Assert.False(run.TryGetProperty("active_path", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_MatchesNfcIndexedPathsAfterNfdWorkspaceSort()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_nfd_path");
        try
        {
            var accentContent = "def accent():\n    return 1\n";
            var asciiContent = "def ascii_neighbor():\n    return 2\n";
            var nfdFileName = "e\u0301.py";
            var nfcFileName = "\u00e9.py";
            File.WriteAllText(Path.Combine(projectRoot, nfdFileName), accentContent);
            File.WriteAllText(Path.Combine(projectRoot, "f.py"), asciiContent);

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, nfcFileName, "python", accentContent);
            TestProjectHelper.InsertIndexedFile(dbPath, "f.py", "python", asciiContent);
            MarkStatusReadinessReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var check = document.RootElement.GetProperty("workspace_check");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(document.RootElement.GetProperty("index_matches_workspace").GetBoolean());
            Assert.Equal("matched", check.GetProperty("reason").GetString());
            Assert.Equal(2, check.GetProperty("indexed_file_count").GetInt32());
            Assert.Equal(2, check.GetProperty("workspace_file_count").GetInt32());
            Assert.Equal(0, check.GetProperty("missing_file_count").GetInt32());
            Assert.Equal(0, check.GetProperty("unindexed_file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_ReclassifiesSkipWorktreePathsAsOutsideSparseCone()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_sparse_cone");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var insidePath = Path.Combine(projectRoot, "src", "inside.cs");
            var outsidePath = Path.Combine(projectRoot, "src", "outside.cs");
            File.WriteAllText(insidePath, "class Inside {}\n");
            File.WriteAllText(outsidePath, "class Outside {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/inside.cs", "src/outside.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            // Flag src/outside.cs skip-worktree and remove it from disk to mimic a sparse-checkout
            // working tree. The freshness checker must classify it as "outside sparse cone",
            // not as a true "missing" file.
            // src/outside.cs に skip-worktree を立て disk からも消し sparse-checkout を再現する。
            // freshness checker は "outside sparse cone" として分類し "missing" を立ててはいけない。
            TestProjectHelper.RunGit(projectRoot, "update-index", "--skip-worktree", "src/outside.cs");
            File.Delete(outsidePath);

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/inside.cs", "csharp", "class Inside {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/outside.cs", "csharp", "class Outside {}\n");
            MarkStatusReadinessReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var check = document.RootElement.GetProperty("workspace_check");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(document.RootElement.GetProperty("index_matches_workspace").GetBoolean());
            Assert.Equal(0, check.GetProperty("missing_file_count").GetInt32());
            Assert.Equal(1, check.GetProperty("outside_sparse_cone_file_count").GetInt32());
            Assert.Equal("src/outside.cs", check.GetProperty("outside_sparse_cone_files")[0].GetString());
            Assert.Equal("matched", check.GetProperty("reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_KeepsTrulyMissingFilesSeparateFromSparseCone()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_sparse_mix");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var keptPath = Path.Combine(projectRoot, "src", "kept.cs");
            var sparsePath = Path.Combine(projectRoot, "src", "sparse.cs");
            File.WriteAllText(keptPath, "class Kept {}\n");
            File.WriteAllText(sparsePath, "class Sparse {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/kept.cs", "src/sparse.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            // src/sparse.cs is skip-worktree → outside cone. src/deleted.cs is indexed only,
            // never tracked by git → must remain a real "missing" entry.
            // src/sparse.cs は skip-worktree → cone 外。src/deleted.cs は DB のみで git 追跡無し
            // → 本当の "missing" として残らなければならない。
            TestProjectHelper.RunGit(projectRoot, "update-index", "--skip-worktree", "src/sparse.cs");
            File.Delete(sparsePath);

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/kept.cs", "csharp", "class Kept {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/sparse.cs", "csharp", "class Sparse {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/deleted.cs", "csharp", "class Deleted {}\n");
            MarkStatusReadinessReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var check = document.RootElement.GetProperty("workspace_check");

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(document.RootElement.GetProperty("index_matches_workspace").GetBoolean());
            Assert.Equal("workspace_stale", document.RootElement.GetProperty("failed_checks")[0].GetString());
            Assert.Equal(1, check.GetProperty("missing_file_count").GetInt32());
            Assert.Equal("src/deleted.cs", check.GetProperty("missing_files")[0].GetString());
            Assert.Equal(1, check.GetProperty("outside_sparse_cone_file_count").GetInt32());
            Assert.Equal("src/sparse.cs", check.GetProperty("outside_sparse_cone_files")[0].GetString());
            Assert.Equal("missing_indexed_files", check.GetProperty("reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckHuman_SuccessKeepsOutputSilent()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_sparse_human");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sparsePath = Path.Combine(projectRoot, "src", "sparse.cs");
            File.WriteAllText(sparsePath, "class Sparse {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/sparse.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            TestProjectHelper.RunGit(projectRoot, "update-index", "--skip-worktree", "src/sparse.cs");
            File.Delete(sparsePath);

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/sparse.cs", "csharp", "class Sparse {}\n");
            MarkStatusReadinessReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(string.Empty, stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckHuman_WritesStaleDiagnosticToStderr()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_stderr");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            MarkStatusReadinessReady(dbPath);
            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check"],
                _jsonOptions));

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("[stale] workspace_check reason=changed_files", stderr);
            Assert.Contains("changed=1", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJsonScopedFold_ReportsOnlyFoldDegradation()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_fold_scope");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check=fold", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var failedChecks = json.GetProperty("failed_checks");

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Single(failedChecks.EnumerateArray());
            Assert.Equal("fold_ready", failedChecks[0].GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("graph", "graph_table_available")]
    [InlineData("issues", "file_issues_data_current")]
    [InlineData("hotspot", "hotspot_family_ready")]
    [InlineData("csharp", "csharp_symbol_name_ready")]
    [InlineData("sql", "sql_graph_contract_ready")]
    [InlineData("newer", "index_newer_than_reader")]
    public void RunStatus_CheckJsonScopedReadiness_ReportsOnlyRequestedSubsystem(string scope, string expectedFailure)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"cdidx_query_runner_status_check_scope_{scope}");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            File.WriteAllText(Path.Combine(projectRoot, "src", "query.sql"), "SELECT run_me();\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/query.sql", "sql", "SELECT run_me();\n");
            MarkStatusReadinessReady(dbPath);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                switch (scope)
                {
                    case "graph":
                        ExecuteNonQuery(db, $"PRAGMA user_version = {DbContext.CurrentSchemaVersion & ~DbContext.GraphReadyFlag}");
                        break;
                    case "issues":
                        ExecuteNonQuery(db, $"PRAGMA user_version = {DbContext.CurrentSchemaVersion & ~DbContext.IssuesReadyFlag}");
                        break;
                    case "hotspot":
                        writer.ClearHotspotFamilyReady();
                        break;
                    case "csharp":
                        writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, null);
                        break;
                    case "sql":
                        writer.SetMeta(DbContext.SqlGraphContractVersionMetaKey, null);
                        break;
                    case "newer":
                        writer.SetMeta("fold_key_version", (NameFold.Version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        break;
                }
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, $"--check={scope}", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var failedChecks = document.RootElement.GetProperty("failed_checks").EnumerateArray().Select(e => e.GetString()).ToArray();

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal([expectedFailure], failedChecks);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_UsesRepositoryRootIgnoreRulesForSubdirectoryIndex()
    {
        var repoRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_parent_ignore");
        try
        {
            TestProjectHelper.InitializeGitRepo(repoRoot);
            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "sub/generated/\n");

            var projectRoot = Path.Combine(repoRoot, "sub");
            Directory.CreateDirectory(Path.Combine(projectRoot, "generated"));
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App {}\n");
            File.WriteAllText(Path.Combine(projectRoot, "generated", "ignored.cs"), "class Ignored {}\n");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var check = document.RootElement.GetProperty("workspace_check");

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(document.RootElement.GetProperty("index_matches_workspace").GetBoolean());
            Assert.Equal(1, check.GetProperty("indexed_file_count").GetInt32());
            Assert.Equal(1, check.GetProperty("workspace_file_count").GetInt32());
            Assert.Equal(0, check.GetProperty("unindexed_file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void RunStatus_ReadOnlyUriForExplicitDb_UsesPersistedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_uri");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_query_runner_status_{Guid.NewGuid():N}.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", readOnlyUri, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(projectRoot, json.GetProperty("project_root").GetString());
            Assert.Equal(expectedHead, json.GetProperty("git_head").GetString());
            Assert.True(json.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void RunStatus_CustomDbUnderCdidx_UsesPersistedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_custom_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_custom_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "shared.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
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

            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(projectRoot, json.GetProperty("project_root").GetString());
            Assert.Equal(expectedHead, json.GetProperty("git_head").GetString());
            Assert.True(json.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void RunStatus_ExplicitProjectLocalDb_LeavesWorkspaceMetadataNullWhenMetadataIsMissing()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_project_local_explicit");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("project_root").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("git_head").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("git_is_dirty").ValueKind);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_ExplicitProjectLocalReadOnlyUri_LeavesWorkspaceMetadataNullWhenMetadataIsMissing()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_project_local_uri");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", readOnlyUri, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("project_root").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("git_head").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("git_is_dirty").ValueKind);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RunStatus_ExplicitExternalCodeIndexDb_UsesPersistedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_codeindex_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_codeindex_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
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

            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(projectRoot, json.GetProperty("project_root").GetString());
            Assert.Equal(expectedHead, json.GetProperty("git_head").GetString());
            Assert.True(json.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void RunStatus_ExplicitExternalCodeIndexDb_IgnoresSingleSiblingPathCollision()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_codeindex_collision_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_codeindex_collision_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            Directory.CreateDirectory(Path.Combine(dbContainerRoot, "src"));

            const string content = "class App {}\n";
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), content);
            File.WriteAllText(Path.Combine(dbContainerRoot, "src", "app.cs"), content);
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
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", content);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
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
    public void RunStatus_ExplicitExternalCodeIndexDbWithoutMetadata_IgnoresSiblingPathCollisionAndLeavesWorkspaceMetadataNull()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_codeindex_missing_meta_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_codeindex_missing_meta_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            TestProjectHelper.InitializeGitRepo(dbContainerRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            Directory.CreateDirectory(Path.Combine(dbContainerRoot, "src"));

            const string indexedContent = "class App {}\n";
            const string siblingContent = "class App { void Different() {} }\n";
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), indexedContent);
            File.WriteAllText(Path.Combine(dbContainerRoot, "src", "app.cs"), siblingContent);
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            TestProjectHelper.RunGit(dbContainerRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(dbContainerRoot, "commit", "-m", "initial");

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", indexedContent);
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("project_root").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("git_head").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("git_is_dirty").ValueKind);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void RunStatus_MissingDatabaseReturnsGuidance()
    {
        var missingDbPath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.db");

        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--db", missingDbPath],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error [E001_DB_NOT_FOUND]: --db", stderr);
        // Verify full (absolute) path is shown, not just the basename / フルパス表示を検証
        Assert.Contains(Path.GetFullPath(missingDbPath), stderr);
        Assert.Contains("does not point to an existing database file", stderr);
        Assert.Contains("Hint: create or refresh the index with `cdidx index <projectPath>`", stderr);
    }

    [Fact]
    public void RunFiles_HumanOutputFormatsSizesAndBytesFlagKeepsRawCounts()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_size_units");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/big.cs", "csharp", "class Big {}\n");
            SetIndexedFileSize(dbPath, "src/big.cs", 5L * 1024 * 1024 * 1024);

            var (formattedExit, formattedStdout, formattedStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath],
                _jsonOptions));
            var (rawExit, rawStdout, rawStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--bytes"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, formattedExit);
            Assert.Equal(CommandExitCodes.Success, rawExit);
            Assert.Contains("5.0 GiB", formattedStdout);
            Assert.DoesNotContain("5368709120 bytes", formattedStdout);
            Assert.Contains("5368709120 bytes", rawStdout);
            Assert.Equal("(1 files)" + Environment.NewLine, formattedStderr);
            Assert.Equal("(1 files)" + Environment.NewLine, rawStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_CountBytesIncludesSizeAggregates_Issue3948()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_count_bytes");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/a-small.cs", "csharp", "class Small {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/z-large.cs", "csharp", "class Large {}\n");
            SetIndexedFileSize(dbPath, "src/a-small.cs", 10);
            SetIndexedFileSize(dbPath, "src/z-large.cs", 1_000);

            var (jsonExit, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--json", "--count", "--bytes"],
                _jsonOptions));
            var (humanExit, humanStdout, humanStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--count", "--bytes"],
                _jsonOptions));

            using var document = ParseJsonOutput(jsonStdout);
            var root = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, jsonExit);
            Assert.Equal(CommandExitCodes.Success, humanExit);
            Assert.Equal(string.Empty, jsonStderr);
            Assert.Equal(string.Empty, humanStderr);
            Assert.Equal(2, root.GetProperty("count").GetInt32());
            Assert.Equal(2, root.GetProperty("files").GetInt32());
            Assert.Equal(2, root.GetProperty("file_count").GetInt32());
            Assert.Equal(1_010, root.GetProperty("total_bytes").GetInt64());
            Assert.Equal(505, root.GetProperty("average_bytes").GetDouble());
            Assert.Equal(1_000, root.GetProperty("max_bytes").GetInt64());
            Assert.Equal("src/z-large.cs", root.GetProperty("max_bytes_path").GetString());
            Assert.True(root.GetProperty("bytes_authoritative").GetBoolean());
            Assert.Contains("2 files, 1010 bytes total", humanStdout);
            Assert.Contains("average 505 bytes", humanStdout);
            Assert.Contains("max 1000 bytes (src/z-large.cs)", humanStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_BytesOrdersBySizeBeforeLimit_Issue2994()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_size_order");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/a-small.cs", "csharp", "class Small {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/z-large.cs", "csharp", "class Large {}\n");
            SetIndexedFileSize(dbPath, "src/a-small.cs", 10);
            SetIndexedFileSize(dbPath, "src/z-large.cs", 1_000);

            var (defaultExit, defaultStdout, defaultStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--json", "--limit", "1"],
                _jsonOptions));
            var (bytesExit, bytesStdout, bytesStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--json", "--bytes", "--limit", "1"],
                _jsonOptions));

            using var defaultDocument = ParseJsonOutput(defaultStdout);
            using var bytesDocument = ParseJsonOutput(bytesStdout);

            Assert.Equal(CommandExitCodes.Success, defaultExit);
            Assert.Equal(CommandExitCodes.Success, bytesExit);
            Assert.Equal(string.Empty, defaultStderr);
            Assert.Equal(string.Empty, bytesStderr);
            Assert.Equal("src/a-small.cs", defaultDocument.RootElement.GetProperty("path").GetString());
            Assert.Equal("src/z-large.cs", bytesDocument.RootElement.GetProperty("path").GetString());
            Assert.Equal(1_000, bytesDocument.RootElement.GetProperty("size").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_JsonOutputKeepsRawSizeInteger()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_size_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/big.cs", "csharp", "class Big {}\n");
            SetIndexedFileSize(dbPath, "src/big.cs", 5L * 1024 * 1024 * 1024);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(5L * 1024 * 1024 * 1024, document.RootElement.GetProperty("size").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_JsonArray_EmitsSingleArray_Issue2993()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_json_array");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--json=array"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var files = document.RootElement.EnumerateArray().ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var file = Assert.Single(files);
            Assert.Equal("src/app.cs", file.GetProperty("path").GetString());
            Assert.Equal("csharp", file.GetProperty("lang").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_JsonArray_ZeroResultsEmitsEmptyArray_Issue2993()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_json_array_zero");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing", "--db", dbPath, "--json=array"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(document.RootElement.EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_ReadOnlyFlagOpensImmutableUriAndReportsModeFields()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_readonly");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var options = QueryCommandRunner.ParseArgs(
                ["--db", dbPath, "--read-only", "--json"],
                jsonDefault: false,
                allowStatusCheck: true,
                validateDefaultLimit: false,
                validateDefaultSnippetLines: false,
                validateDefaultMaxLineWidth: false);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--read-only", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.StartsWith("file:", options.DbPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("immutable=1", options.DbPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mode=ro", options.DbPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(document.RootElement.GetProperty("read_only_fallback").GetBoolean());
            Assert.False(document.RootElement.GetProperty("wal_checkpoint_attempted").GetBoolean());
            Assert.False(document.RootElement.GetProperty("wal_checkpoint_succeeded").GetBoolean());
            var policy = document.RootElement.GetProperty("sqlite_connection_policy");
            Assert.Equal("read_only", policy.GetProperty("active_mode").GetString());
            Assert.Equal(SqliteConnectionPolicy.DefaultCommandTimeoutSeconds, policy.GetProperty("command_timeout_seconds").GetInt32());
            Assert.True(policy.GetProperty("long_running_commands_require_cancellation").GetBoolean());
            Assert.False(policy.GetProperty("read_only_fallback").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
