using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.HookIsolationFixture;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void ReadCommands_OpenQueryOnlyAndLeaveDatabaseArtifactsUnchanged_Issue4557()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_only_issue4557");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(sourceDbPath, "src/App.cs", "csharp", "class App {}\n");
            SqliteConnection.ClearAllPools();
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = sourceDbPath, Pooling = false }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE)";
                _ = command.ExecuteScalar();
            }

            SqliteConnection.ClearAllPools();
            var dbPath = Path.Combine(projectRoot, "checkpointed-wal-copy.db");
            File.Copy(sourceDbPath, dbPath);
            Assert.False(File.Exists(dbPath + "-wal"));
            Assert.False(File.Exists(dbPath + "-shm"));
            var expectedArtifacts = CaptureDatabaseArtifacts(dbPath);
            var expectedUserVersion = ReadPragmaInt64(sourceDbPath, "user_version");
            var expectedApplicationId = ReadPragmaInt64(sourceDbPath, "application_id");

            var commands = new (string Name, Func<int> Run)[]
            {
                ("status", () => QueryCommandRunner.RunStatus(["--db", dbPath, "--json"], _jsonOptions)),
                ("search", () => QueryCommandRunner.RunSearch(["App", "--db", dbPath, "--json"], _jsonOptions)),
                ("files", () => QueryCommandRunner.RunFiles(["--db", dbPath, "--json"], _jsonOptions)),
            };

            foreach (var (name, run) in commands)
            {
                var (exitCode, stdout, _) = CaptureConsole(run);
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.False(string.IsNullOrWhiteSpace(stdout));
                using (var verificationDb = new DbContext(DbOpenIntent.QueryOnly, dbPath))
                {
                    using var userVersion = verificationDb.Connection.CreateCommand();
                    userVersion.CommandText = "PRAGMA user_version";
                    Assert.Equal(expectedUserVersion, (long)userVersion.ExecuteScalar()!);
                    using var applicationId = verificationDb.Connection.CreateCommand();
                    applicationId.CommandText = "PRAGMA application_id";
                    Assert.Equal(expectedApplicationId, (long)applicationId.ExecuteScalar()!);
                }
                Assert.Equal(expectedArtifacts, CaptureDatabaseArtifacts(dbPath));

                if (name == "status")
                {
                    using var document = ParseJsonOutput(stdout);
                    var policy = document.RootElement.GetProperty("sqlite_connection_policy");
                    Assert.Equal(SqliteConnectionPolicy.ImmutableReadOnlyUriModeName, policy.GetProperty("active_mode").GetString());
                    Assert.Equal(SqliteConnectionPolicy.ImmutableReadOnlyUriModeName, policy.GetProperty("open_mode").GetString());
                    Assert.True(policy.GetProperty("immutable_uri").GetBoolean());
                    Assert.False(policy.GetProperty("wal_stale_snapshot_risk").GetBoolean());
                    Assert.Equal("delete", document.RootElement.GetProperty("db_pragma_settings").GetProperty("journal_mode").GetString());
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ReadCommands_SnapshotHotWalWithoutTouchingArtifacts_Issue4557()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_only_hot_wal_issue4557");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SqliteConnection.ClearAllPools();
            using var writer = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ConnectionString);
            writer.Open();
            using (var command = writer.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE)";
                _ = command.ExecuteScalar();
                command.CommandText = "INSERT OR REPLACE INTO codeindex_meta(key, value) VALUES ('issue4557_hot_wal', 'committed')";
                Assert.Equal(1, command.ExecuteNonQuery());
                command.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'issue4557_hot_wal'";
                Assert.Equal("committed", (string?)command.ExecuteScalar());
            }

            Assert.True(new FileInfo(dbPath + "-wal").Length > 0);
            var expectedArtifacts = CaptureDatabaseArtifacts(dbPath);

            string snapshotDirectory;
            using (var queryDb = new DbContext(DbOpenIntent.QueryOnly, dbPath))
            {
                snapshotDirectory = Path.GetDirectoryName(queryDb.Connection.DataSource)!;
                Assert.NotEqual(Path.GetDirectoryName(dbPath), snapshotDirectory);
                using var committedValue = queryDb.Connection.CreateCommand();
                committedValue.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'issue4557_hot_wal'";
                Assert.Equal("committed", (string?)committedValue.ExecuteScalar());
            }
            Assert.False(Directory.Exists(snapshotDirectory));
            Assert.Equal(expectedArtifacts, CaptureDatabaseArtifacts(dbPath));

            var (exitCode, stdout, _) = CaptureConsole(
                () => QueryCommandRunner.RunStatus(["--db", dbPath, "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(string.IsNullOrWhiteSpace(stdout));
            Assert.Equal(expectedArtifacts, CaptureDatabaseArtifacts(dbPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void QueryOnlySnapshot_RetriesEmptyWalToCommittedWalTransition_Issue4557()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_only_wal_transition_issue4557");
        var originalHook = DbConnectionFactory.QueryOnlySnapshotCapturedForTesting;
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SqliteConnection.ClearAllPools();
            using var writer = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ConnectionString);
            writer.Open();
            using (var setup = writer.CreateCommand())
            {
                setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE)";
                _ = setup.ExecuteScalar();
            }

            var transitionExecuted = 0;
            DbConnectionFactory.QueryOnlySnapshotCapturedForTesting = () =>
            {
                if (Interlocked.Exchange(ref transitionExecuted, 1) != 0)
                    return;
                using var commit = writer.CreateCommand();
                commit.CommandText = "INSERT OR REPLACE INTO codeindex_meta(key, value) VALUES ('issue4557_transition', 'visible')";
                Assert.Equal(1, commit.ExecuteNonQuery());
            };

            using var queryDb = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            var expectedArtifacts = CaptureDatabaseArtifacts(dbPath);
            using var value = queryDb.Connection.CreateCommand();
            value.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'issue4557_transition'";
            Assert.Equal("visible", (string?)value.ExecuteScalar());
            Assert.Equal(1, transitionExecuted);
            Assert.Equal(expectedArtifacts, CaptureDatabaseArtifacts(dbPath));
        }
        finally
        {
            DbConnectionFactory.QueryOnlySnapshotCapturedForTesting = originalHook;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void QueryOnlySnapshot_RetriesHotWalCheckpointReset_Issue4557()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_only_wal_reset_issue4557");
        var originalHook = DbConnectionFactory.QueryOnlySnapshotCapturedForTesting;
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SqliteConnection.ClearAllPools();
            using var writer = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ConnectionString);
            writer.Open();
            using (var setup = writer.CreateCommand())
            {
                setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE)";
                _ = setup.ExecuteScalar();
                setup.CommandText = "INSERT OR REPLACE INTO codeindex_meta(key, value) VALUES ('issue4557_reset', 'visible')";
                Assert.Equal(1, setup.ExecuteNonQuery());
            }
            Assert.True(new FileInfo(dbPath + "-wal").Length > 0);

            var resetExecuted = 0;
            DbConnectionFactory.QueryOnlySnapshotCapturedForTesting = () =>
            {
                if (Interlocked.Exchange(ref resetExecuted, 1) != 0)
                    return;
                using var checkpoint = writer.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                using var result = checkpoint.ExecuteReader();
                Assert.True(result.Read());
                Assert.Equal(0L, result.GetInt64(0));
            };

            using var queryDb = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            var expectedArtifacts = CaptureDatabaseArtifacts(dbPath);
            using var value = queryDb.Connection.CreateCommand();
            value.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'issue4557_reset'";
            Assert.Equal("visible", (string?)value.ExecuteScalar());
            Assert.Equal(1, resetExecuted);
            Assert.Equal(expectedArtifacts, CaptureDatabaseArtifacts(dbPath));
        }
        finally
        {
            DbConnectionFactory.QueryOnlySnapshotCapturedForTesting = originalHook;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void QueryOnlySnapshot_HonorsCancellationAfterGenerationCapture_Issue4557()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_only_snapshot_cancel_issue4557");
        var originalHook = DbConnectionFactory.QueryOnlySnapshotCapturedForTesting;
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SqliteConnection.ClearAllPools();
            using (var writer = new SqliteConnection(
                       new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ConnectionString))
            {
                writer.Open();
                using var checkpoint = writer.CreateCommand();
                checkpoint.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE)";
                _ = checkpoint.ExecuteScalar();
            }

            var expectedArtifacts = CaptureDatabaseArtifacts(dbPath);
            using var cancellation = new CancellationTokenSource();
            DbConnectionFactory.QueryOnlySnapshotCapturedForTesting = cancellation.Cancel;

            Assert.Throws<OperationCanceledException>(() =>
                new DbContext(DbOpenIntent.QueryOnly, dbPath, cancellation.Token));
            Assert.Equal(expectedArtifacts, CaptureDatabaseArtifacts(dbPath));
        }
        finally
        {
            DbConnectionFactory.QueryOnlySnapshotCapturedForTesting = originalHook;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueryOnlySnapshot_ReportsPersistentCopyFailureWithoutRetrying_Issue4557(bool permissionDenied)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_only_snapshot_copy_failure_issue4557");
        var originalCopyHook = DbConnectionFactory.QueryOnlySnapshotFileCopyingForTesting;
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            SqliteConnection.ClearAllPools();
            using var writer = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ConnectionString);
            writer.Open();
            using (var setup = writer.CreateCommand())
            {
                setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE)";
                _ = setup.ExecuteScalar();
                setup.CommandText = "INSERT OR REPLACE INTO codeindex_meta(key, value) VALUES ('issue4557_copy_failure', 'visible')";
                Assert.Equal(1, setup.ExecuteNonQuery());
            }
            Assert.True(new FileInfo(dbPath + "-wal").Length > 0);

            var copyAttempts = 0;
            Exception injectedFailure = permissionDenied
                ? new UnauthorizedAccessException("injected destination permission failure")
                : new IOException("injected destination copy failure");
            DbConnectionFactory.QueryOnlySnapshotFileCopyingForTesting = (_, _) =>
            {
                Interlocked.Increment(ref copyAttempts);
                throw injectedFailure;
            };

            var error = Assert.Throws<CodeIndexException>(() =>
                new DbContext(DbOpenIntent.QueryOnly, dbPath));

            Assert.Equal(DbConnectionFactory.QueryOnlySnapshotCopyFailedCode, error.Code);
            Assert.Equal(CodeIndexExceptionCategory.Filesystem, error.Category);
            Assert.Contains("temporary-storage capacity", error.Hint, StringComparison.Ordinal);
            Assert.DoesNotContain(injectedFailure.Message, error.Message, StringComparison.Ordinal);
            Assert.Same(injectedFailure, error.InnerException);
            Assert.Equal(1, copyAttempts);
        }
        finally
        {
            DbConnectionFactory.QueryOnlySnapshotFileCopyingForTesting = originalCopyHook;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static Dictionary<string, (long Length, DateTime LastWriteTimeUtc, string Sha256)> CaptureDatabaseArtifacts(string dbPath)
    {
        var result = new Dictionary<string, (long, DateTime, string)>(StringComparer.Ordinal);
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (!File.Exists(path))
                continue;

            var info = new FileInfo(path);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            result[Path.GetFileName(path)] = (
                info.Length,
                info.LastWriteTimeUtc,
                Convert.ToHexString(SHA256.HashData(stream)));
        }

        return result;
    }

    private static long ReadPragmaInt64(string dbPath, string pragmaName)
    {
        using var connection = new SqliteConnection(
            $"Data Source={DbContext.ToReadOnlyUri(dbPath)};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragmaName}";
        return (long)command.ExecuteScalar()!;
    }

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
    public void RunCodeOwners_FullAndIncrementalIndexingSupportsDiscoverySearchSymbolsOutlineAndStatus_Issue5198()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_codeowners_issue5198");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, ".github"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
            File.WriteAllText(
                Path.Combine(projectRoot, ".github", "CODEOWNERS"),
                "# highest-precedence candidate\r\n/src/** @org/team owner@example.com # inline comment\r\n/src/special/**\r\n/src/** @later\r\n\\#literal @escaped\r\n");
            File.WriteAllText(Path.Combine(projectRoot, "CODEOWNERS"), "* @root\n");
            File.WriteAllText(Path.Combine(projectRoot, "docs", "CODEOWNERS"), "/docs/** @docs\n");

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (filesExitCode, filesStdout, filesStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--lang", "codeowners", "--json=array"],
                _jsonOptions));
            using var filesDocument = ParseJsonOutput(filesStdout);
            Assert.Equal(CommandExitCodes.Success, filesExitCode);
            Assert.Equal(string.Empty, filesStderr);
            Assert.Equal(
                [".github/CODEOWNERS", "CODEOWNERS", "docs/CODEOWNERS"],
                filesDocument.RootElement.EnumerateArray()
                    .Select(file => file.GetProperty("path").GetString())
                    .OrderBy(path => path, StringComparer.Ordinal));

            AssertSearchesPath(dbPath, "@org/team", ".github/CODEOWNERS");
            AssertSearchesPath(dbPath, "owner@example.com", ".github/CODEOWNERS");

            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--lang", "codeowners", "--path", ".github/CODEOWNERS", "--json=array"],
                _jsonOptions));
            using var symbolsDocument = ParseJsonOutput(symbolsStdout);
            var symbols = symbolsDocument.RootElement.EnumerateArray().ToList();
            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal(string.Empty, symbolsStderr);
            Assert.Equal(3, symbols.Count(symbol => symbol.GetProperty("kind").GetString() == "rule"));
            Assert.Contains(
                symbols,
                symbol => symbol.GetProperty("kind").GetString() == "property"
                    && symbol.GetProperty("name").GetString() == "@org/team"
                    && symbol.GetProperty("container_name").GetString() == "/src/**");
            Assert.Contains(
                symbols,
                symbol => symbol.GetProperty("kind").GetString() == "annotation"
                    && symbol.GetProperty("name").GetString() == "codeowners_unsupported_pattern");

            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                [".github/CODEOWNERS", "--db", dbPath, "--json"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Contains("/src/**", outlineStdout, StringComparison.Ordinal);
            Assert.Contains("@org/team", outlineStdout, StringComparison.Ordinal);

            var (statusExitCode, statusStdout, statusStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));
            using var statusDocument = ParseJsonOutput(statusStdout);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Equal(string.Empty, statusStderr);
            Assert.Equal(0, statusDocument.RootElement.GetProperty("unknown_extension_file_count").GetInt64());

            File.WriteAllText(Path.Combine(projectRoot, "CODEOWNERS"), "* @updated-root\n");
            var (updateExitCode, _, updateStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--files", "CODEOWNERS", "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(string.Empty, updateStderr);
            AssertSearchesPath(dbPath, "@updated-root", "CODEOWNERS");

            File.Delete(Path.Combine(projectRoot, "docs", "CODEOWNERS"));
            var (deleteExitCode, _, deleteStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--files", "docs/CODEOWNERS", "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, deleteExitCode);
            Assert.Equal(string.Empty, deleteStderr);

            var (deletedFilesExitCode, deletedFilesStdout, deletedFilesStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--path", "docs/CODEOWNERS", "--json=array"],
                _jsonOptions));
            using var deletedFilesDocument = ParseJsonOutput(deletedFilesStdout);
            Assert.Equal(CommandExitCodes.Success, deletedFilesExitCode);
            Assert.Equal(string.Empty, deletedFilesStderr);
            Assert.Empty(deletedFilesDocument.RootElement.EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCodeOwners_GitSubdirectoryScanDoesNotPromoteNestedCandidate_Issue5198Review()
    {
        using var repository = TestProjectHelper.CreateTempProjectScope("cdidx_codeowners_git_subdirectory_issue5198");
        TestProjectHelper.InitializeGitRepo(repository.Root);
        var projectRoot = Path.Combine(repository.Root, "packages", "app");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "CODEOWNERS"), "* @nested\n");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

        var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
            [projectRoot, "--db", dbPath, "--json", "--quiet"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, indexExitCode);
        Assert.Equal(string.Empty, indexStderr);

        var (filesExitCode, filesStdout, filesStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
            ["--db", dbPath, "--lang", "codeowners", "--json=array"],
            _jsonOptions));
        using var filesDocument = ParseJsonOutput(filesStdout);
        Assert.Equal(CommandExitCodes.Success, filesExitCode);
        Assert.Equal(string.Empty, filesStderr);
        Assert.Empty(filesDocument.RootElement.EnumerateArray());
    }

    [Fact]
    public void IndexAndDryRun_ZshCompletionCompdefIsShellAndQueryable_Issue5165()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_zsh_compdef_issue5165");
        try
        {
            const string completionFileName = "_cdidx";
            var completionPath = Path.Combine(projectRoot, completionFileName);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            File.WriteAllText(completionPath, ConsoleCompletionRenderer.GetCompletionScript("zsh"));

            var (dryRunExitCode, dryRunStdout, dryRunStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--dry-run", "--json", "--quiet"],
                _jsonOptions));
            using var dryRunDocument = ParseJsonOutput(dryRunStdout);
            Assert.Equal(CommandExitCodes.Success, dryRunExitCode);
            Assert.Equal(string.Empty, dryRunStderr);
            Assert.Equal(1, dryRunDocument.RootElement.GetProperty("files_total").GetInt32());
            Assert.Equal(1, dryRunDocument.RootElement.GetProperty("languages").GetProperty("shell").GetInt32());
            Assert.Equal(0, dryRunDocument.RootElement.GetProperty("unknown_extension_file_count").GetInt32());

            var (scopedDryRunExitCode, scopedDryRunStdout, scopedDryRunStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--files", completionFileName, "--dry-run", "--json", "--quiet"],
                _jsonOptions));
            using var scopedDryRunDocument = ParseJsonOutput(scopedDryRunStdout);
            Assert.Equal(CommandExitCodes.Success, scopedDryRunExitCode);
            Assert.Equal(string.Empty, scopedDryRunStderr);
            Assert.Equal(1, scopedDryRunDocument.RootElement.GetProperty("files_total").GetInt32());
            Assert.Equal(1, scopedDryRunDocument.RootElement.GetProperty("languages").GetProperty("shell").GetInt32());
            Assert.Equal(0, scopedDryRunDocument.RootElement.GetProperty("unsupported_total").GetInt32());

            var (indexExitCode, indexStdout, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            using var indexDocument = ParseJsonOutput(indexStdout);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(1, indexDocument.RootElement.GetProperty("summary").GetProperty("files_total").GetInt32());
            Assert.Equal(0, indexDocument.RootElement.GetProperty("unknown_extension_file_count").GetInt32());

            File.AppendAllText(completionPath, "\n# refreshed\n");
            var (updateExitCode, _, updateStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--files", completionFileName, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(string.Empty, updateStderr);

            var (filesExitCode, filesStdout, filesStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--lang", "shell", "--json=array"],
                _jsonOptions));
            using var filesDocument = ParseJsonOutput(filesStdout);
            var file = Assert.Single(filesDocument.RootElement.EnumerateArray().ToArray());
            Assert.Equal(CommandExitCodes.Success, filesExitCode);
            Assert.Equal(string.Empty, filesStderr);
            Assert.Equal(completionFileName, file.GetProperty("path").GetString());
            Assert.Equal("shell", file.GetProperty("lang").GetString());
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

    [Fact]
    public void RunFiles_SummaryOnlyJsonDoesNotReportLimitAsTruncation_Issue4317()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_files_summary_only_issue4317");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "class App { }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "README.md", "markdown", "# App\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--json", "--summary-only", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;

            Assert.Equal(2, root.GetProperty("count").GetInt32());
            Assert.Equal(0, root.GetProperty("emitted_count").GetInt32());
            Assert.Equal(2, root.GetProperty("omitted_count").GetInt32());
            Assert.Equal(2, root.GetProperty("summary_only_omitted_count").GetInt32());
            Assert.True(root.GetProperty("summary_only").GetBoolean());
            Assert.False(root.GetProperty("truncated").GetBoolean());
            Assert.False(root.TryGetProperty("row_limit_reached", out _));
            Assert.False(root.TryGetProperty("limit_omitted_count", out _));
            Assert.False(root.TryGetProperty("files", out _));
            Assert.Equal("summary_only", Assert.Single(root.GetProperty("omitted_by").EnumerateArray().ToList()).GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
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
    public void RunFiles_GeneratedAliasIncludesGeneratedFiles_Issue4342()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_files_generated_alias_4342");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Generated.g.cs", "csharp", "public class Generated { }\n", isGenerated: true);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--generated", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
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
                "--json-dir/Demo.cs",
                "csharp",
                "class Demo {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                [$"--db={dbPath}", "--path=--json-dir", "--count", "--json"],
                _jsonOptions));

            Assert.True(exitCode == CommandExitCodes.Success, stderr);
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
                "--count-dir/Demo.cs",
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
    public void RunStatus_Json_ReportsBoundedHookDiscoveryFailure_Issue4600()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_hook_metadata_3142");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable);
            using var extensionProject = TestProjectHelper.CreateExecutableExtensionTestProjectScope(
                "cdidx_status_hook_metadata_extensions_3142");
            try
            {
                var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
                var hooksDir = Path.Combine(extensionProject.Root, "hooks");
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
                Assert.False(document.RootElement.TryGetProperty("hooks", out _));
                var diagnostic = Assert.Single(
                    document.RootElement.GetProperty("hook_diagnostics").EnumerateArray(),
                    item => item.GetProperty("category").GetString() == "assembly_load_failed");
                Assert.EndsWith("broken.dll", diagnostic.GetProperty("assembly_path").GetString(), StringComparison.Ordinal);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsStableHookIdentityAndWorkerLifecycle_Issue4600()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_hook_identity_4600");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable);
            using var extensionProject = TestProjectHelper.CreateExecutableExtensionTestProjectScope(
                "cdidx_status_hook_identity_extensions_4600");
            try
            {
                var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
                var hooksDir = Path.Combine(extensionProject.Root, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.Copy(typeof(PathSelectivePostExtractionHook).Assembly.Location, Path.Combine(hooksDir, "status-hook.dll"));
                env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);

                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                    ["--db", dbPath, "--json"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                var hook = Assert.Single(
                    document.RootElement.GetProperty("hooks").EnumerateArray(),
                    item => item.GetProperty("type_name").GetString() == typeof(PathSelectivePostExtractionHook).FullName);
                Assert.StartsWith("hook:", hook.GetProperty("id").GetString(), StringComparison.Ordinal);
                Assert.Equal(
                    PostExtractionHookRunner.HookLoadContextLifecycle,
                    hook.GetProperty("load_context_lifecycle").GetString());
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
            using var extensionProject = TestProjectHelper.CreateExecutableExtensionTestProjectScope(
                "cdidx_status_hook_cap_extensions_3456");
            try
            {
                var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
                var hooksDir = Path.Combine(extensionProject.Root, "hooks");
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
                Assert.False(document.RootElement.TryGetProperty("hooks", out _));
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

    [ExternalProcessFact]
    public void RunStatus_Json_ReportsAcceptedExtensionTrustOverrides_3735()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_trust_overrides_3735");
        string? windowsGitDirectory = null;
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(
                PostExtractionHookRunner.HooksDirectoryEnvironmentVariable,
                ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable,
                GitHelper.GitExecutableEnvironmentVariable);
            using var extensionProject = TestProjectHelper.CreateExecutableExtensionTestProjectScope(
                "cdidx_status_trust_override_extensions_3735");
            try
            {
                var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
                var hooksDir = Path.Combine(extensionProject.Root, "hooks");
                windowsGitDirectory = OperatingSystem.IsWindows()
                    ? TestProjectHelper.CreateTrustedWindowsGitDirectory("cdidx_status_git_3735")
                    : null;
                var gitPath = Path.Combine(
                    windowsGitDirectory ?? projectRoot,
                    OperatingSystem.IsWindows() ? "git.exe" : "git");
                Directory.CreateDirectory(hooksDir);
                File.WriteAllText(
                    gitPath,
                    OperatingSystem.IsWindows()
                        ? "not a portable executable"
                        : "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo 'git version 2.0.0'; exit 0; fi\nexit 1\n");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        gitPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "true");
                env.Set(GitHelper.GitExecutableEnvironmentVariable, gitPath);

                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                    ["--db", dbPath, "--json"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                var trustOverrides = document.RootElement.GetProperty("trust_overrides").EnumerateArray().ToArray();
                Assert.Equal(OperatingSystem.IsWindows() ? 2 : 3, trustOverrides.Length);

                var gitExecutable = document.RootElement.GetProperty("git_executable");
                Assert.Equal("environment_override", gitExecutable.GetProperty("source").GetString());
                if (OperatingSystem.IsWindows())
                {
                    Assert.False(gitExecutable.GetProperty("accepted").GetBoolean());
                    Assert.Equal("invalid_executable_format", gitExecutable.GetProperty("reason").GetString());
                    Assert.False(gitExecutable.GetProperty("executable").GetBoolean());
                    Assert.DoesNotContain(
                        trustOverrides,
                        item => item.GetProperty("kind").GetString() == "git_executable");
                }
                else
                {
                    Assert.True(gitExecutable.GetProperty("accepted").GetBoolean());
                    Assert.Equal("accepted", gitExecutable.GetProperty("reason").GetString());
                    Assert.True(gitExecutable.GetProperty("executable").GetBoolean());
                    Assert.True(gitExecutable.GetProperty("owner_only_writable").GetBoolean());
                    Assert.Equal("0700", gitExecutable.GetProperty("unix_mode").GetString());
                    Assert.Equal("current_user", gitExecutable.GetProperty("owner").GetString());
                    Assert.True(gitExecutable.GetProperty("owner_trusted").GetBoolean());
                    Assert.True(gitExecutable.GetProperty("ancestor_directories_trusted").GetBoolean());

                    var gitOverride = Assert.Single(
                        trustOverrides,
                        item => item.GetProperty("kind").GetString() == "git_executable");
                    Assert.Equal(GitHelper.GitExecutableEnvironmentVariable, gitOverride.GetProperty("environment_variable").GetString());
                    Assert.Equal(Path.GetFileName(gitPath), gitOverride.GetProperty("path").GetString());
                    Assert.DoesNotContain(projectRoot, gitOverride.GetProperty("path").GetString(), StringComparison.Ordinal);
                }

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
                if (windowsGitDirectory != null)
                    TestProjectHelper.DeleteDirectory(windowsGitDirectory);
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsRejectedGitHubCliEnvironmentOverride_Issue5184()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_gh_5184");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(
                GitHubCliExecutableResolver.ExecutableEnvironmentVariable);
            try
            {
                var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
                env.Set(GitHubCliExecutableResolver.ExecutableEnvironmentVariable, "gh");

                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                    ["--db", dbPath, "--json"],
                    _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Empty(stderr);
                using var document = JsonDocument.Parse(stdout);
                var githubCliExecutable = document.RootElement.GetProperty("github_cli_executable");
                Assert.Equal(
                    "environment_override",
                    githubCliExecutable.GetProperty("source").GetString());
                Assert.False(githubCliExecutable.GetProperty("accepted").GetBoolean());
                Assert.Equal("path_not_absolute", githubCliExecutable.GetProperty("reason").GetString());
                if (document.RootElement.TryGetProperty("trust_overrides", out var trustOverrides))
                {
                    Assert.DoesNotContain(
                        trustOverrides.EnumerateArray(),
                        item => item.GetProperty("kind").GetString() == "github_cli_executable");
                }
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
    public void RunStatus_ReportsVerifiedHeadDriftWithoutMixingLegacyOrLatestProvenance_Issue5054()
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
            var legacyHead = new string('a', 40);
            var staleHead = new string('b', 40);
            var latestHead = new string('c', 40);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, legacyHead);
                writer.SetMeta(DbContext.WorkspaceVerifiedHeadShaMetaKey, staleHead);
                writer.SetMeta(DbContext.IndexedHeadShaMetaKey, latestHead);
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(legacyHead, json.GetProperty("indexed_head_commit").GetString());
            Assert.Equal(staleHead, json.GetProperty("workspace_verified_head_sha").GetString());
            Assert.Equal(latestHead, json.GetProperty("indexed_head_sha").GetString());
            Assert.True(json.GetProperty("worktree_head_changed").GetBoolean());
            var headFreshness = json.GetProperty("head_freshness");
            Assert.Equal("head_changed", headFreshness.GetProperty("state").GetString());
            Assert.Equal("worktree_head_changed", headFreshness.GetProperty("state_reason").GetString());
            Assert.Equal(staleHead, headFreshness.GetProperty("indexed_head").GetString());
            Assert.Equal("workspace_verified", headFreshness.GetProperty("indexed_head_source").GetString());
            Assert.Equal(legacyHead, headFreshness.GetProperty("legacy_full_scan_head").GetString());
            Assert.Equal(latestHead, headFreshness.GetProperty("latest_index_head").GetString());
            Assert.False(headFreshness.TryGetProperty("indexed_head_branch", out _));
            Assert.False(headFreshness.TryGetProperty("indexed_head_timestamp", out _));
            Assert.False(headFreshness.TryGetProperty("commits_ahead_of_indexed_head", out _));
            Assert.True(headFreshness.GetProperty("worktree_head_changed").GetBoolean());

            var (humanExitCode, humanStdout, humanStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Equal(string.Empty, humanStderr);
            Assert.Contains($"Verified : {staleHead}", humanStdout, StringComparison.Ordinal);
            Assert.Contains($"({staleHead[..12]} ->", humanStdout, StringComparison.Ordinal);
            Assert.DoesNotContain($"({legacyHead[..12]} ->", humanStdout, StringComparison.Ordinal);
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

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, staleHead);
                writer.SetMeta(DbContext.WorkspaceVerifiedHeadShaMetaKey, staleHead);
                writer.SetMeta(DbContext.IndexedHeadShaMetaKey, staleHead);
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("WARN     : worktree HEAD changed since the workspace was verified", stdout);
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
    public void RunStatus_HumanOutput_UsesDynamicGraphContractRepairHint_Issue4746()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_dynamic_graph_contract_4746");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.groovy"),
                "def helper() { }\ndef run() { helper() }\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(
                    DbContext.GetDynamicReferenceGraphContractVersionMetaKey("groovy"),
                    "0");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains(
                "Refresh indexing to rewrite stale dynamic-language graph rows and extractor-version stamps.",
                stdout,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "reduce or exclude the cap-hitting generated/pathological source",
                stdout,
                StringComparison.OrdinalIgnoreCase);
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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

    [ProductionRuntimeFact]
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
                ["status", "--db", "file:codeindex.db?immutable=1", "--check=fold", "--json"],
                dbDirectory);

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("stale_fold_key_version", json.GetProperty("fold_ready_reason").GetString());
            Assert.Contains(dbPath, json.GetProperty("recommended_action").GetString());
            Assert.Contains(dbPath, json.GetProperty("alternative_action").GetString());
            Assert.DoesNotContain("<writable-db-path>", json.GetProperty("recommended_action").GetString());
            Assert.DoesNotContain("<writable-db-path>", json.GetProperty("alternative_action").GetString());
            Assert.DoesNotContain("file:", json.GetProperty("recommended_action").GetString());
            Assert.DoesNotContain("file:", json.GetProperty("alternative_action").GetString());
            var repairCommand = Assert.Single(json.GetProperty("repair_commands").EnumerateArray());
            Assert.Equal("backfill_fold", repairCommand.GetProperty("action").GetString());
            Assert.Equal("fold_ready", repairCommand.GetProperty("reason").GetString());
            Assert.Equal(
                ["fold_ready"],
                repairCommand.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal("database_write", repairCommand.GetProperty("mutation_class").GetString());
            Assert.Equal("fold_backfill", repairCommand.GetProperty("safety_class").GetString());
            var repairArgs = repairCommand.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Equal("backfill-fold", repairArgs[0]);
            Assert.Equal("--db", repairArgs[1]);
            Assert.EndsWith(Path.Combine(".cdidx", "codeindex.db"), repairArgs[2], StringComparison.Ordinal);
            Assert.DoesNotContain(repairArgs, value => value?.Contains("file:", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(repairArgs, value => value?.Contains("immutable=1", StringComparison.Ordinal) == true);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [ProductionRuntimeFact]
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
                ["status", "--db", "file:codeindex.db?mode=ro", "--check=fold"],
                dbDirectory);

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("[repair] cdidx backfill-fold --db ", stderr, StringComparison.Ordinal);
            Assert.Contains("codeindex.db", stderr, StringComparison.Ordinal);
            Assert.Contains("reasons=fold_ready", stderr, StringComparison.Ordinal);
            Assert.Contains("action=backfill_fold", stderr, StringComparison.Ordinal);
            Assert.Contains("mutation=database_write", stderr, StringComparison.Ordinal);
            Assert.Contains("safety=fold_backfill", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("<writable-db-path>", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("file:codeindex.db", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("mode=ro", stderr, StringComparison.Ordinal);
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
    public void RunFiles_ZeroCompactJson_OnEmptyIndex_EmitsNullIndexedAt()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_zero_json_empty_index");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["definitely-missing-path", "--db", dbPath, "--format", "compact"],
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
    public void RunFiles_ZeroCompactJson_OnLegacyReadOnlyDb_EmitsFreshnessDegradedSignal()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_zero_json_legacy_freshness");
        try
        {
            var dbPath = CreateLegacyDbWithoutIndexedAt(projectRoot);
            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["definitely-missing-path", "--db", readOnlyUri, "--format", "compact"],
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
    public void RunFiles_ZeroResultCompactJson_EmitsStructuredPayloadWithFreshnessHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_zero_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing-file-fragment", "--db", dbPath, "--format", "compact"],
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
    public void RunFiles_ZeroResultCompactJson_EmptyIndexEmitsNullIndexedAt()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_zero_json_empty");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing-file-fragment", "--db", dbPath, "--format", "compact"],
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
    public void RunStatus_Explain_ReferenceGraphCompleteCoversUnavailableAndCapHitStates_Issue4620()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "reference_graph_complete"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("state is unavailable", stdout, StringComparison.Ordinal);
        Assert.Contains("one or more files hit a hard cap", stdout, StringComparison.Ordinal);
        Assert.Contains("reference_extraction_cap_hits.state_available", stdout, StringComparison.Ordinal);
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
    public void RunStatus_Explain_MaintenanceGuidanceDescribesSharedOptimizationDecision_Issue4887()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "maintenance_guidance"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Database maintenance guidance (maintenance_guidance)", stdout);
        Assert.Contains("fts_optimization", stdout);
        Assert.Contains("threshold_writes", stdout);
        Assert.Contains("observed_writes", stdout);
        Assert.Contains("state=stale", stdout);
        Assert.Contains("optimize --dry-run", stdout);
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
        Assert.Contains("workspace_verified_head_sha", stdout);
    }

    [Fact]
    public void RunStatus_Explain_PrintsHeadFreshnessSummaryDescriptionWithoutDatabase_Issue4152()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "head_freshness"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Compact HEAD freshness summary (head_freshness)", stdout);
        Assert.Contains("state=fresh", stdout);
        Assert.Contains("state=head_current", stdout);
        Assert.Contains("indexed_head_source", stdout);
    }

    [Fact]
    public void RunStatus_Explain_PrintsGitExecutableDescriptionWithoutDatabase_Issue4599()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "git_executable"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Trusted Git executable selection (git_executable)", stdout);
        Assert.Contains("git --version", stdout);
        Assert.Contains(GitHelper.GitExecutableEnvironmentVariable, stdout);
    }

    [Fact]
    public void RunStatus_Explain_PrintsGitHubCliExecutableDescriptionWithoutDatabase_Issue5184()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "github_cli_executable"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(
            "Trusted GitHub CLI executable selection (github_cli_executable)",
            stdout);
        Assert.Contains("gh --version", stdout);
        Assert.Contains(GitHubCliExecutableResolver.ExecutableEnvironmentVariable, stdout);
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
        Assert.Contains("head_freshness", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("indexed_head_sha", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("indexed_head_commit", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("index_matches_workspace", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("data_dir_mode", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("unknown_extension_file_count", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("path_case_sensitive", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("sqlite_connection_policy", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("git_executable", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("maintenance_guidance", json.GetProperty("known_fields").EnumerateArray().Select(item => item.GetString()));

        var (policyExitCode, policyStdout, policyStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "sqlite_connection_policy", "--json"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, policyExitCode);
        Assert.Equal(string.Empty, policyStderr);
        using var policyDocument = ParseJsonOutput(policyStdout);
        Assert.Equal("sqlite_connection_policy", policyDocument.RootElement.GetProperty("field").GetString());
        Assert.Contains("immutable-URI", policyDocument.RootElement.GetProperty("ready").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trust_overrides", "Accepted trust overrides", "environment overrides", "extractors")]
    [InlineData("extractors", "Extractor registry diagnostics", "extractor plugins", "trust_overrides")]
    [InlineData("hooks", "Post-extraction hook manifests", "hook manifests", "hook_diagnostics")]
    [InlineData("maintenance_guidance", "Database maintenance guidance", "WAL and freelist", "db_pragma_settings")]
    [InlineData("reference_extraction_cap_hits", "Reference extraction cap hits", "safety caps", "reference_extraction_limits")]
    public void RunStatus_ExplainJson_PrintsStructuredMajorSectionMetadata_Issue4891(
        string field,
        string label,
        string meaningText,
        string dependency)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", field, "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        Assert.Equal(field, json.GetProperty("field").GetString());
        Assert.Equal(label, json.GetProperty("label").GetString());
        Assert.Equal("top_level", json.GetProperty("scope").GetString());
        Assert.Contains(meaningText, json.GetProperty("meaning").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("source").GetString()));
        Assert.Contains(
            dependency,
            json.GetProperty("dependencies").EnumerateArray().Select(item => item.GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("interpretation").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("repair_guidance").GetString()));
        Assert.False(json.GetProperty("redaction").GetProperty("runtime_values_included").GetBoolean());
        Assert.False(json.GetProperty("redaction").GetProperty("paths_included").GetBoolean());
    }

    [Theory]
    [InlineData("maintenance_guidance.recommended_command", "Recommended maintenance command", "single maintenance action", "maintenance_guidance.wal_state")]
    [InlineData("maintenance_guidance.fts_optimization", "FTS optimization guidance", "optimizing FTS indexes", "maintenance_guidance.fts_optimization.state")]
    [InlineData("reference_extraction_cap_hits.files", "Reference cap-hit file sample", "bounded", "reference_extraction_cap_hits.file_limit")]
    [InlineData("extractors.diagnostics", "Extractor diagnostics", "sanitized diagnostics", "extractors.diagnostic_count")]
    [InlineData("hooks.callback_budget_ms", "Hook callback budget", "maximum elapsed time", "hook_diagnostics")]
    [InlineData("trust_overrides.path", "Trust override path", "redacted path context", "trust_overrides.kind")]
    public void RunStatus_ExplainJson_PrintsNestedMemberMetadataWithoutRuntimeValues_Issue4891(
        string field,
        string label,
        string meaningText,
        string dependency)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", field, "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        Assert.Equal(field, json.GetProperty("field").GetString());
        Assert.Equal(label, json.GetProperty("label").GetString());
        Assert.Equal("member", json.GetProperty("scope").GetString());
        Assert.Contains(meaningText, json.GetProperty("meaning").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            dependency,
            json.GetProperty("dependencies").EnumerateArray().Select(item => item.GetString()));
        Assert.False(json.GetProperty("redaction").GetProperty("runtime_values_included").GetBoolean());
        Assert.False(json.GetProperty("redaction").GetProperty("paths_included").GetBoolean());
        Assert.DoesNotContain(Directory.GetCurrentDirectory(), stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void RunStatus_Explain_CoversEverySerializedTopLevelFieldFromSerializerRegistry_Issue4891()
    {
        var serializedFields = QueryCommandRunner.GetStatusSerializableFieldNames(_jsonOptions);

        Assert.NotEmpty(serializedFields);
        Assert.DoesNotContain("indexed_follow_symlinks_policy", serializedFields);
        foreach (var field in serializedFields)
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--explain", field, "--json"],
                _jsonOptions));

            Assert.True(
                exitCode == CommandExitCodes.Success,
                $"Expected serialized status field `{field}` to be explainable, but got exit {exitCode}: {stderr}{stdout}");
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            Assert.Equal(field, document.RootElement.GetProperty("field").GetString());
        }

        var (_, knownFieldsStdout, _) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "files", "--json"],
            _jsonOptions));
        using var knownFieldsDocument = ParseJsonOutput(knownFieldsStdout);
        var knownFields = knownFieldsDocument.RootElement
            .GetProperty("known_fields")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(serializedFields, field => Assert.Contains(field, knownFields));
        Assert.True(knownFieldsDocument.RootElement.GetProperty("known_fields_truncated").GetBoolean());
        Assert.Equal(
            knownFieldsDocument.RootElement.GetProperty("known_field_limit").GetInt32(),
            knownFields.Count);
    }

    [Fact]
    public void RunStatus_ExplainUnknownNestedJsonReturnsBoundedValidCandidatesAndRedactsInput_Issue4891()
    {
        var (nestedExitCode, nestedStdout, nestedStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "maintenance_guidance.nope", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, nestedExitCode);
        Assert.Equal(string.Empty, nestedStderr);
        using var nestedDocument = ParseJsonOutput(nestedStdout);
        Assert.Contains(
            "maintenance_guidance.recommended_command",
            nestedDocument.RootElement.GetProperty("hint").GetString(),
            StringComparison.Ordinal);

        var (typoExitCode, typoStdout, typoStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "db_pragma_settings.journal_mdoe", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, typoExitCode);
        Assert.Equal(string.Empty, typoStderr);
        using var typoDocument = ParseJsonOutput(typoStdout);
        Assert.Contains(
            "db_pragma_settings.journal_mode",
            typoDocument.RootElement.GetProperty("hint").GetString(),
            StringComparison.Ordinal);

        var sensitiveInput = "/Users/example/.ssh/id_rsa-" + new string('x', 400);
        var (redactedExitCode, redactedStdout, redactedStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", sensitiveInput, "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, redactedExitCode);
        Assert.Equal(string.Empty, redactedStderr);
        Assert.DoesNotContain("/Users/example", redactedStdout, StringComparison.Ordinal);
        Assert.True(redactedStdout.Length < 5000, $"Expected bounded error output, got {redactedStdout.Length} characters.");
    }

    [Fact]
    public void RunStatus_ExplainJson_PrintsIndexMatchesWorkspaceDescription_Issue4317()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "index_matches_workspace", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        Assert.Equal("index_matches_workspace", json.GetProperty("field").GetString());
        Assert.Equal("Workspace freshness check", json.GetProperty("label").GetString());
        Assert.Contains("status --check", json.GetProperty("ready").GetString());
        Assert.Contains("missing, changed, deleted, or stale", json.GetProperty("ready").GetString());
        Assert.Contains("cdidx index <projectPath>", json.GetProperty("remediation").GetString());
    }

    [Theory]
    [InlineData("data_dir_mode", "Data directory mode", "Unix permission mode")]
    [InlineData("unknown_extension_file_count", "Unknown extension inventory", "unknown extensions")]
    public void RunStatus_ExplainJson_PrintsSupportSafeFieldDescriptions_Issue4313(
        string field,
        string label,
        string readyText)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", field, "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        Assert.Equal(field, json.GetProperty("field").GetString());
        Assert.Equal(label, json.GetProperty("label").GetString());
        Assert.Contains(readyText, json.GetProperty("ready").GetString());
    }

    [Fact]
    public void RunStatus_ExplainJson_PrintsHeadFreshnessSummaryDescription_Issue4152()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "head_freshness", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        Assert.Equal("head_freshness", json.GetProperty("field").GetString());
        Assert.Equal("Compact HEAD freshness summary", json.GetProperty("label").GetString());
        Assert.Contains("status --check", json.GetProperty("ready").GetString());
        Assert.Contains("state=stale", json.GetProperty("degraded").GetString());
        Assert.Contains("indexed_head_source=workspace_verified", json.GetProperty("remediation").GetString());
        Assert.Contains("latest_index_head", json.GetProperty("remediation").GetString());
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
        Assert.Contains("workspace_verified_head_sha", json.GetProperty("remediation").GetString());
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
        var logDir = TestProjectHelper.CreateTempProject("cdidx_status_log_path");
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void RunStatus_LogPath_JsonHonorsXdgCacheHome()
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            "XDG_STATE_HOME",
            "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR");
        var tempRoot = TestProjectHelper.CreateTempProject("cdidx_status_log_path_xdg");
        var cacheHome = Path.Combine(tempRoot, "cache");
        try
        {
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", null);
            env.Set("XDG_STATE_HOME", null);
            env.Set("XDG_CACHE_HOME", cacheHome);
            env.Set("XDG_RUNTIME_DIR", Path.Combine(tempRoot, "ignored-runtime"));

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--log-path", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("1", document.RootElement.GetProperty("api_version").GetString());
            Assert.Equal(Path.Combine(cacheHome, "cdidx", "logs"), document.RootElement.GetProperty("log_path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempRoot);
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            Assert.Equal("FULL", pragmas.GetProperty("synchronous").GetString());
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            var headFreshness = json.GetProperty("head_freshness");
            Assert.Equal("fresh", headFreshness.GetProperty("state").GetString());
            Assert.Equal("matched", headFreshness.GetProperty("state_reason").GetString());
            Assert.Equal("unavailable", headFreshness.GetProperty("indexed_head_source").GetString());
            Assert.True(headFreshness.GetProperty("workspace_matches_index").GetBoolean());
            Assert.Equal(1, check.GetProperty("matched_file_count").GetInt32());
            Assert.Contains("index fresh", json.GetProperty("summary").GetString());
            Assert.False(json.TryGetProperty("repair_commands", out _));
            var queryContext = json.GetProperty("query_context");
            Assert.Equal(QueryCommandRunner.StatusCheckModeExplicit, queryContext.GetProperty("check_mode").GetString());
            Assert.Equal(json.GetProperty("stale_after_seconds").GetInt64(), queryContext.GetProperty("stale_after_seconds").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_StaleAfterJson_ImpliesCheckAndReportsEffectiveContext_Issue4576()
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
                ["--db", dbPath, "--stale-after", "30m", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(30 * 60, json.GetProperty("stale_after_seconds").GetInt64());
            Assert.True(json.GetProperty("index_age_seconds").GetInt64() >= 0);
            Assert.True(json.GetProperty("index_matches_workspace").GetBoolean());
            var queryContext = json.GetProperty("query_context");
            Assert.Equal(QueryCommandRunner.StatusCheckModeImpliedByStaleAfter, queryContext.GetProperty("check_mode").GetString());
            Assert.Equal(30 * 60, queryContext.GetProperty("stale_after_seconds").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false, false, QueryCommandRunner.StatusCheckModeImpliedByStaleAfter)]
    [InlineData(true, false, QueryCommandRunner.StatusCheckModeExplicit)]
    [InlineData(true, true, QueryCommandRunner.StatusCheckModeExplicit)]
    public void RunStatus_StaleAfterJson_ReturnsStaleExitWhenWorkspaceDiffers_Issue4576(
        bool useScopedCheck,
        bool staleAfterBeforeCheck,
        string expectedCheckMode)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_stale_after_changed_4576");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            MarkStatusReadinessReady(dbPath);
            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            var args = new List<string> { "--db", dbPath };
            if (useScopedCheck && staleAfterBeforeCheck)
                args.AddRange(["--stale-after", "1m", "--check=fold"]);
            else if (useScopedCheck)
                args.Add("--check=fold");
            if (!staleAfterBeforeCheck)
                args.AddRange(["--stale-after", "1m"]);
            args.Add("--json");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(args.ToArray(), _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("index_matches_workspace").GetBoolean());
            Assert.Equal("workspace_stale", json.GetProperty("failed_checks")[0].GetString());
            var queryContext = json.GetProperty("query_context");
            Assert.Equal(expectedCheckMode, queryContext.GetProperty("check_mode").GetString());
            Assert.Equal(60, queryContext.GetProperty("stale_after_seconds").GetInt64());
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
            Assert.False(json.TryGetProperty("query_context", out _));
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
            Assert.Equal("index", repairCommand.GetProperty("action").GetString());
            Assert.Equal("workspace_stale", repairCommand.GetProperty("reason").GetString());
            Assert.Equal(
                ["workspace_stale"],
                repairCommand.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal("index_write", repairCommand.GetProperty("mutation_class").GetString());
            Assert.Equal("workspace_refresh", repairCommand.GetProperty("safety_class").GetString());
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
    public void DeduplicateStatusRepairCommands_UsesStructuredIdentityAndStableReasonOrder_Issue4915()
    {
        static StatusRepairCommand Command(
            string reason,
            string target = "workspace-a",
            string action = "index",
            string mutationClass = "index_write",
            string safetyClass = "metadata_refresh",
            string safetyNote = "Refresh metadata.",
            bool rebuild = false)
            => new()
            {
                Name = "cdidx",
                Action = action,
                Args = rebuild ? ["index", target, "--rebuild"] : ["index", target],
                Reason = reason,
                Reasons = [reason],
                MutationClass = mutationClass,
                SafetyClass = safetyClass,
                SafetyNotes = [safetyNote],
            };

        var commands = QueryCommandRunner.DeduplicateStatusRepairCommands(
        [
            Command("graph_table_available"),
            Command("issues_table_available"),
            Command("other_target", target: "workspace-b"),
            Command("other_options", rebuild: true),
            Command("other_safety_class", safetyClass: "reference_graph_refresh"),
            Command("other_safety_note", safetyNote: "Inspect reference caps."),
            Command("other_mutation", mutationClass: "database_write"),
            Command("other_action", action: "backfill_fold"),
        ]);

        Assert.Equal(7, commands.Count);
        Assert.Equal("graph_table_available", commands[0].Reason);
        Assert.Equal(
            ["graph_table_available", "issues_table_available"],
            commands[0].Reasons);
        Assert.Equal("workspace-b", commands[1].Args[1]);
        Assert.Equal("--rebuild", commands[2].Args[2]);
        Assert.Equal("reference_graph_refresh", commands[3].SafetyClass);
        Assert.Equal("Inspect reference caps.", commands[4].SafetyNotes[0]);
        Assert.Equal("database_write", commands[5].MutationClass);
        Assert.Equal("backfill_fold", commands[6].Action);
        Assert.Empty(QueryCommandRunner.DeduplicateStatusRepairCommands([]));
    }

    [Fact]
    public void RenderStatusRepairCommand_EscapesControlCharactersOnOneLine_Issue4915()
    {
        var command = new StatusRepairCommand
        {
            Name = "cdidx",
            Action = "index",
            Args = ["index", "workspace\n[repair] forged\u001b"],
            Reason = "workspace_stale",
            Reasons = ["workspace_stale"],
            MutationClass = "index_write",
            SafetyClass = "workspace_refresh",
        };

        var rendered = QueryCommandRunner.RenderStatusRepairCommand(command);

        Assert.Equal("cdidx index 'workspace\\n[repair] forged\\u001B'", rendered);
        Assert.DoesNotContain('\n', rendered);
        Assert.DoesNotContain('\r', rendered);
        Assert.DoesNotContain('\u001b', rendered);
        Assert.Equal("workspace\n[repair] forged\u001b", command.Args[1]);
    }

    [Fact]
    public void RunStatus_Check_DeduplicatesRepairCommandsForJsonAndHumanOutput_Issue4915()
    {
        var containerRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_repair_dedup");
        var projectRoot = Path.Combine(containerRoot, "workspace;printf_PWNED'$value");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            const string content = "class App {}\n";
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), content);
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", content);

            var (jsonExitCode, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(jsonStdout);
            var repairCommands = document.RootElement.GetProperty("repair_commands").EnumerateArray().ToArray();
            var metadataRefresh = repairCommands[0];

            Assert.Equal(2, jsonExitCode);
            Assert.Equal(string.Empty, jsonStderr);
            Assert.Equal(4, repairCommands.Length);
            Assert.Equal(
                ["metadata_refresh", "reference_graph_refresh", "full_rebuild", "fold_backfill"],
                repairCommands.Select(command => command.GetProperty("safety_class").GetString()).ToArray());
            Assert.Equal("graph_table_available", metadataRefresh.GetProperty("reason").GetString());
            Assert.Equal(
                [
                    "graph_table_available",
                    "file_issues_data_current",
                    "csharp_symbol_name_ready",
                    "csharp_metadata_target_ready",
                ],
                metadataRefresh.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal("index", metadataRefresh.GetProperty("action").GetString());
            Assert.Equal("index_write", metadataRefresh.GetProperty("mutation_class").GetString());

            var (humanExitCode, humanStdout, humanStderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check"],
                _jsonOptions));
            var repairLines = humanStderr
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("[repair] ", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(2, humanExitCode);
            Assert.Equal(string.Empty, humanStdout);
            Assert.Equal(4, repairLines.Length);
            var quotedProjectRoot = OperatingSystem.IsWindows()
                ? $"'{projectRoot.Replace("'", "''", StringComparison.Ordinal)}'"
                : $"'{projectRoot.Replace("'", "'\\''", StringComparison.Ordinal)}'";
            var quotedDbPath = OperatingSystem.IsWindows()
                ? $"'{dbPath.Replace("'", "''", StringComparison.Ordinal)}'"
                : $"'{dbPath.Replace("'", "'\\''", StringComparison.Ordinal)}'";
            Assert.Contains($"cdidx index {quotedProjectRoot} --db {quotedDbPath}", repairLines[0], StringComparison.Ordinal);
            Assert.DoesNotContain($"index {projectRoot}", repairLines[0], StringComparison.Ordinal);
            Assert.Contains(
                "reasons=graph_table_available,file_issues_data_current,csharp_symbol_name_ready,csharp_metadata_target_ready",
                repairLines[0],
                StringComparison.Ordinal);
            Assert.Equal(1, repairLines.Count(line => line.Contains("safety=metadata_refresh", StringComparison.Ordinal)));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(containerRoot);
        }
    }

    [Fact]
    public void RunStatus_CheckJson_ReferenceGraphUnavailableUsesRefreshGuidance_Issue4620()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_reference_cap_state_unavailable");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            const string content = "class App {}\n";
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), content);
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", content);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                new DbWriter(db.Connection).MarkGraphReady();

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check=graph", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(
                ["reference_graph_complete"],
                json.GetProperty("failed_checks").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal(
                DegradationReasonCodes.ReferenceExtractionCapStateUnavailable,
                json.GetProperty("degraded_root_cause").GetString());
            var repairCommand = Assert.Single(json.GetProperty("repair_commands").EnumerateArray());
            var safetyNote = repairCommand.GetProperty("safety_notes")[0].GetString();
            Assert.Contains("populate current per-file issue state", safetyNote, StringComparison.Ordinal);
            Assert.DoesNotContain("cap-hitting", safetyNote, StringComparison.Ordinal);
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            Assert.Equal("backfill_fold", repairCommand.GetProperty("action").GetString());
            Assert.Equal("fold_ready", repairCommand.GetProperty("reason").GetString());
            Assert.Equal("database_write", repairCommand.GetProperty("mutation_class").GetString());
            Assert.Equal("fold_backfill", repairCommand.GetProperty("safety_class").GetString());
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            Assert.Equal(
                scope == "graph"
                    ? ["graph_table_available", "reference_graph_complete"]
                    : [expectedFailure],
                failedChecks);
            if (scope == "graph")
            {
                var referenceDegradation = document.RootElement
                    .GetProperty("readiness_degradations")
                    .EnumerateArray()
                    .Single(degradation =>
                        degradation.GetProperty("field").GetString()
                        == "reference_graph_complete");
                Assert.Equal(
                    DegradationReasonCodes.GraphTableMissing,
                    referenceDegradation.GetProperty("root_cause").GetString());
                Assert.DoesNotContain(
                    "safety cap",
                    referenceDegradation.GetProperty("degraded_reason").GetString(),
                    StringComparison.OrdinalIgnoreCase);
            }
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
    public void RunStatus_CheckJson_IgnoresSuggestionSidecarInInternalDataDirectory()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_check_suggestion_sidecar");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App {}\n");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            File.WriteAllText(
                Path.Combine(projectRoot, ".cdidx", "suggestions-codeindex.json"),
                "[]\n");

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
            Assert.Equal("matched", check.GetProperty("reason").GetString());
            Assert.Equal(1, check.GetProperty("workspace_file_count").GetInt32());
            Assert.Equal(0, check.GetProperty("unindexed_file_count").GetInt32());
            Assert.Empty(check.GetProperty("unindexed_files").EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_ReadOnlyUriForExplicitDb_UsesPersistedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_uri");
        var dbRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_db");
        var dbPath = Path.Combine(dbRoot, "codeindex.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            TestProjectHelper.DeleteDirectory(dbRoot);
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", indexedContent);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
        var dbRoot = TestProjectHelper.CreateTempProject("cdidx_missing_db");
        var missingDbPath = Path.Combine(dbRoot, "missing.db");
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(dbRoot);
        }
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
    public void RunFilesAndSymbols_RawJsonPreservesSelectedShapeForZeroAndByteCappedResults_Issue4563()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_discovery_json_shape_4563");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (emptyFilesExitCode, emptyFilesStdout, emptyFilesStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing-file-fragment", "--db", dbPath, "--json"],
                _jsonOptions));
            var (emptySymbolsExitCode, emptySymbolsStdout, emptySymbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["MissingSymbol", "--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, emptyFilesExitCode);
            Assert.Equal(CommandExitCodes.Success, emptySymbolsExitCode);
            Assert.Equal(string.Empty, emptyFilesStdout);
            Assert.Equal(string.Empty, emptySymbolsStdout);
            Assert.Equal(string.Empty, emptyFilesStderr);
            Assert.Equal(string.Empty, emptySymbolsStderr);

            var exactEmptyArrayBytes = Encoding.UTF8.GetByteCount("[]" + Environment.NewLine);
            var (emptyArrayExitCode, emptyArrayStdout, emptyArrayStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing-file-fragment", "--db", dbPath, "--json=array", "--max-json-bytes", exactEmptyArrayBytes.ToString()],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, emptyArrayExitCode);
            Assert.Equal("[]" + Environment.NewLine, emptyArrayStdout);
            Assert.Equal(string.Empty, emptyArrayStderr);

            var sourceDirectory = Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            for (var i = 0; i < 4; i++)
            {
                var suffix = new string((char)('a' + i), 120);
                File.WriteAllText(
                    Path.Combine(sourceDirectory.FullName, $"SchemaFixture{suffix}.cs"),
                    $"public class SchemaFixture{i} {{ public void Execute{i}() {{ }} }}\n");
            }

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertByteCappedDiscoveryArray(
                args => CaptureConsole(() => QueryCommandRunner.RunFiles(args, _jsonOptions)),
                ["--db", dbPath, "--json=array", "--limit", "100"]);
            AssertByteCappedDiscoveryArray(
                args => CaptureConsole(() => QueryCommandRunner.RunSymbols(args, _jsonOptions)),
                ["--db", dbPath, "--json=array", "--limit", "100"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFilesCountAndMap_ReportGeneratedFileExclusionMetadata_Issue4563()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_generated_filter_metadata_4563");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.g.cs", "csharp", "class GeneratedApp {}\n", isGenerated: true);

            var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--json", "--count"],
                _jsonOptions));
            using var countDocument = ParseJsonOutput(countStdout);
            var countJson = countDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            Assert.Equal(1, countJson.GetProperty("count").GetInt32());
            Assert.Equal("exclude", countJson.GetProperty("generated_code_policy").GetString());
            Assert.Equal(1, countJson.GetProperty("generated_file_count_excluded").GetInt32());
            Assert.True(countJson.GetProperty("generated_file_count_excluded_authoritative").GetBoolean());
            Assert.True(countJson.GetProperty("generated_file_filter_available").GetBoolean());
            Assert.False(countJson.GetProperty("query_context").GetProperty("include_generated").GetBoolean());

            var (mapExitCode, mapStdout, mapStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json", "--summary-only"],
                _jsonOptions));
            using var mapDocument = ParseJsonOutput(mapStdout);
            var mapJson = mapDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, mapExitCode);
            Assert.Equal(string.Empty, mapStderr);
            Assert.Equal(1, mapJson.GetProperty("file_count").GetInt32());
            Assert.Equal("exclude", mapJson.GetProperty("generated_code_policy").GetString());
            Assert.Equal(1, mapJson.GetProperty("generated_file_count_excluded").GetInt32());
            Assert.True(mapJson.GetProperty("generated_file_count_excluded_authoritative").GetBoolean());

            var (issueDraftExitCode, issueDraftStdout, issueDraftStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--format", "issue-drafts", "--summary-only"],
                _jsonOptions));
            using var issueDraftDocument = ParseJsonOutput(issueDraftStdout);
            var issueDraftJson = issueDraftDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, issueDraftExitCode);
            Assert.Equal(string.Empty, issueDraftStderr);
            Assert.Equal("exclude", issueDraftJson.GetProperty("generated_code_policy").GetString());
            Assert.Equal(1, issueDraftJson.GetProperty("generated_file_count_excluded").GetInt32());
            Assert.True(issueDraftJson.GetProperty("generated_file_count_excluded_authoritative").GetBoolean());

            var (includedExitCode, includedStdout, includedStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json", "--summary-only", "--include-generated"],
                _jsonOptions));
            using var includedDocument = ParseJsonOutput(includedStdout);
            var includedJson = includedDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, includedExitCode);
            Assert.Equal(string.Empty, includedStderr);
            Assert.Equal(2, includedJson.GetProperty("file_count").GetInt32());
            Assert.Equal("include", includedJson.GetProperty("generated_code_policy").GetString());
            Assert.Equal(0, includedJson.GetProperty("generated_file_count_excluded").GetInt32());

            var (includedIssueDraftExitCode, includedIssueDraftStdout, includedIssueDraftStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--format", "issue-drafts", "--summary-only", "--include-generated"],
                _jsonOptions));
            using var includedIssueDraftDocument = ParseJsonOutput(includedIssueDraftStdout);
            var includedIssueDraftJson = includedIssueDraftDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, includedIssueDraftExitCode);
            Assert.Equal(string.Empty, includedIssueDraftStderr);
            Assert.Equal("include", includedIssueDraftJson.GetProperty("generated_code_policy").GetString());
            Assert.Equal(0, includedIssueDraftJson.GetProperty("generated_file_count_excluded").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFilesAndSymbols_ResultsOnlyEmitsNdjsonRowsWithoutTerminalRecord_Issue4688()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_discovery_results_only_4688");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.py",
                "python",
                "import os\n\ndef main():\n    return os.getcwd()\n");

            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--results-only", "--lang", "python", "--kind", "import", "--limit", "1"],
                _jsonOptions));
            var (filesExitCode, filesStdout, filesStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--json", "--results-only", "--lang", "python", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal(CommandExitCodes.Success, filesExitCode);
            Assert.Equal(string.Empty, symbolsStderr);
            Assert.Equal(string.Empty, filesStderr);

            using var symbolRow = JsonDocument.Parse(Assert.Single(symbolsStdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)));
            using var fileRow = JsonDocument.Parse(Assert.Single(filesStdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)));
            Assert.Equal("import", symbolRow.RootElement.GetProperty("kind").GetString());
            Assert.Equal("src/app.py", symbolRow.RootElement.GetProperty("path").GetString());
            Assert.False(symbolRow.RootElement.TryGetProperty("terminal_record", out _));
            Assert.Equal("src/app.py", fileRow.RootElement.GetProperty("path").GetString());
            Assert.False(fileRow.RootElement.TryGetProperty("terminal_record", out _));

            var (symbolsArrayExitCode, symbolsArrayStdout, symbolsArrayStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--results-only", "--json=array"],
                _jsonOptions));
            var (filesArrayExitCode, filesArrayStdout, filesArrayStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--results-only", "--json=array"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, symbolsArrayExitCode);
            Assert.Equal(CommandExitCodes.UsageError, filesArrayExitCode);
            Assert.Equal(string.Empty, symbolsArrayStdout);
            Assert.Equal(string.Empty, filesArrayStdout);
            Assert.Contains("--results-only is only supported with symbols NDJSON row output", symbolsArrayStderr);
            Assert.Contains("--results-only is only supported with files NDJSON row output", filesArrayStderr);

            var (symbolsArrayFirstExitCode, symbolsArrayFirstStdout, symbolsArrayFirstStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json=array", "--results-only"],
                _jsonOptions));
            var (filesArrayFirstExitCode, filesArrayFirstStdout, filesArrayFirstStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--json=array", "--results-only"],
                _jsonOptions));
            var (filesCompactFirstExitCode, filesCompactFirstStdout, filesCompactFirstStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--format", "compact", "--results-only"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, symbolsArrayFirstExitCode);
            Assert.Equal(CommandExitCodes.UsageError, filesArrayFirstExitCode);
            Assert.Equal(CommandExitCodes.UsageError, filesCompactFirstExitCode);
            Assert.Equal(string.Empty, symbolsArrayFirstStdout);
            Assert.Equal(string.Empty, filesArrayFirstStdout);
            Assert.Equal(string.Empty, filesCompactFirstStdout);
            Assert.Contains("--results-only is only supported with symbols NDJSON row output", symbolsArrayFirstStderr);
            Assert.Contains("--results-only is only supported with files NDJSON row output", filesArrayFirstStderr);
            Assert.Contains("--results-only is only supported with files NDJSON row output", filesCompactFirstStderr);

            var (filesCompatibleOverrideExitCode, filesCompatibleOverrideStdout, _) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--format", "compact", "--format", "json", "--results-only"],
                _jsonOptions));
            var (filesEscapedQueryExitCode, filesEscapedQueryStdout, filesEscapedQueryStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--query", "--json=array", "--results-only"],
                _jsonOptions));
            var (filesSentinelQueryExitCode, filesSentinelQueryStdout, filesSentinelQueryStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", dbPath, "--", "--format", "--results-only"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, filesCompatibleOverrideExitCode);
            Assert.Single(filesCompatibleOverrideStdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal(CommandExitCodes.Success, filesEscapedQueryExitCode);
            Assert.Equal(string.Empty, filesEscapedQueryStdout);
            Assert.Equal(string.Empty, filesEscapedQueryStderr);
            Assert.Equal(CommandExitCodes.Success, filesSentinelQueryExitCode);
            Assert.Equal(string.Empty, filesSentinelQueryStdout);
            Assert.Equal(string.Empty, filesSentinelQueryStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDiscoveryCountsCompactAndMap_LegacySchemaReportsGeneratedFilterUnavailable_Issue4563()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_generated_filter_legacy_4563");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/legacy.cs", "csharp", "class Legacy {}\n");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var removeGeneratedMetadata = connection.CreateCommand();
                removeGeneratedMetadata.CommandText = """
                    DROP INDEX idx_files_generated;
                    ALTER TABLE files DROP COLUMN generated;
                    PRAGMA wal_checkpoint(TRUNCATE);
                    """;
                removeGeneratedMetadata.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";

            AssertUnavailableGeneratedFilter(CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", readOnlyUri, "--json", "--count"],
                _jsonOptions)));
            AssertUnavailableGeneratedFilter(CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", readOnlyUri, "--format", "compact"],
                _jsonOptions)));
            AssertUnavailableGeneratedFilter(CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", readOnlyUri, "--json", "--summary-only"],
                _jsonOptions)));
            AssertUnavailableGeneratedQueryContext(CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["MissingLegacySymbol", "--db", readOnlyUri, "--json", "--count"],
                _jsonOptions)));
            AssertUnavailableGeneratedQueryContext(CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["missing-legacy-text", "--db", readOnlyUri, "--json", "--count"],
                _jsonOptions)));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }

        void AssertUnavailableGeneratedFilter((int ExitCode, string StdOut, string StdErr) result)
        {
            using var document = ParseJsonOutput(result.StdOut);
            var json = document.RootElement;
            Assert.Equal(CommandExitCodes.Success, result.ExitCode);
            Assert.Equal(string.Empty, result.StdErr);
            Assert.Equal("unavailable", json.GetProperty("generated_code_policy").GetString());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("generated_file_count_excluded").ValueKind);
            Assert.False(json.GetProperty("generated_file_count_excluded_authoritative").GetBoolean());
            Assert.False(json.GetProperty("generated_file_filter_available").GetBoolean());
        }

        void AssertUnavailableGeneratedQueryContext((int ExitCode, string StdOut, string StdErr) result)
        {
            using var document = ParseJsonOutput(result.StdOut);
            var queryContext = document.RootElement.GetProperty("query_context");
            Assert.Equal(CommandExitCodes.Success, result.ExitCode);
            Assert.Equal(string.Empty, result.StdErr);
            Assert.Equal("unavailable", queryContext.GetProperty("generated_code_policy").GetString());
            Assert.False(queryContext.GetProperty("generated_file_filter_available").GetBoolean());
        }
    }

    [Fact]
    public void RunFilesAndSymbols_RawOutputPreservesImmutableSqliteTrustDiagnostics_Issue4563()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_discovery_cap_trust_4563");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", readOnlyUri, "--json", "--limit", "1", "--max-json-bytes", "4096"],
                _jsonOptions));
            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(document.RootElement.GetProperty("wal_stale_snapshot_risk").GetBoolean());
            Assert.Equal("explicit_immutable_read_only", document.RootElement.GetProperty("wal_stale_snapshot_reason").GetString());

            AssertDiagnosticOnlyArray(CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing-file", "--db", readOnlyUri, "--json=array"],
                _jsonOptions)));
            AssertDiagnosticOnlyArray(CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["missing-file", "--db", readOnlyUri, "--json=array", "--max-json-bytes", "4096"],
                _jsonOptions)));
            AssertDiagnosticOnlyArray(CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["MissingSymbol", "--db", readOnlyUri, "--json=array"],
                _jsonOptions)));
            AssertDiagnosticOnlyArray(CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["MissingSymbol", "--db", readOnlyUri, "--json=array", "--max-json-bytes", "4096"],
                _jsonOptions)));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }

        void AssertDiagnosticOnlyArray((int ExitCode, string StdOut, string StdErr) result)
        {
            using var diagnosticDocument = ParseJsonOutput(result.StdOut);
            var diagnostic = Assert.Single(diagnosticDocument.RootElement.EnumerateArray());
            Assert.Equal(CommandExitCodes.Success, result.ExitCode);
            Assert.Equal(string.Empty, result.StdErr);
            Assert.True(diagnostic.GetProperty("diagnostic_only").GetBoolean());
            Assert.True(diagnostic.GetProperty("wal_stale_snapshot_risk").GetBoolean());
            Assert.Equal("explicit_immutable_read_only", diagnostic.GetProperty("wal_stale_snapshot_reason").GetString());
        }
    }

    private static void AssertByteCappedDiscoveryArray(
        Func<string[], (int ExitCode, string StdOut, string StdErr)> run,
        string[] baseArgs)
    {
        var (fullExitCode, fullStdout, fullStderr) = run(baseArgs);
        using var fullDocument = ParseJsonOutput(fullStdout);
        var fullCount = fullDocument.RootElement.GetArrayLength();
        var cap = Encoding.UTF8.GetByteCount(fullStdout) / 2;

        Assert.Equal(CommandExitCodes.Success, fullExitCode);
        Assert.Equal(string.Empty, fullStderr);
        Assert.True(fullCount >= 4);
        Assert.True(cap > Encoding.UTF8.GetByteCount("[]" + Environment.NewLine));

        var (cappedExitCode, cappedStdout, cappedStderr) = run([.. baseArgs, "--max-json-bytes", cap.ToString()]);
        using var cappedDocument = ParseJsonOutput(cappedStdout);
        var cappedCount = cappedDocument.RootElement.GetArrayLength();

        Assert.Equal(CommandExitCodes.Success, cappedExitCode);
        Assert.Equal(string.Empty, cappedStderr);
        Assert.Equal(JsonValueKind.Array, cappedDocument.RootElement.ValueKind);
        Assert.InRange(cappedCount, 1, fullCount - 1);
        Assert.True(Encoding.UTF8.GetByteCount(cappedStdout) <= cap);

        var exactFullBytes = Encoding.UTF8.GetByteCount(fullStdout);
        var (exactExitCode, exactStdout, exactStderr) = run([.. baseArgs, "--max-json-bytes", exactFullBytes.ToString()]);
        Assert.Equal(CommandExitCodes.Success, exactExitCode);
        Assert.Equal(fullStdout, exactStdout);
        Assert.Equal(string.Empty, exactStderr);
    }

    [Fact]
    public void RunFiles_FormatCompactMaxJsonBytesTruncatesRowsWithMetadata_Issue4165()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_compact_cap_4165");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < 3; i++)
            {
                var suffix = new string((char)('a' + i), 160);
                TestProjectHelper.InsertIndexedFile(dbPath, $"src/{suffix}.cs", "csharp", "class App {}\n");
            }

            string[] baseArgs = ["--db", dbPath, "--format", "compact", "--limit", "3"];
            var (fullExitCode, fullStdout, fullStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(baseArgs, _jsonOptions));
            var cap = Encoding.UTF8.GetByteCount(fullStdout) - 80;

            Assert.Equal(CommandExitCodes.Success, fullExitCode);
            Assert.Equal(string.Empty, fullStderr);
            Assert.True(cap > 0);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                [.. baseArgs, "--max-json-bytes", cap.ToString()],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            var files = root.GetProperty("files").EnumerateArray().ToArray();
            var omittedBy = root.GetProperty("omitted_by").EnumerateArray().Select(item => item.GetString()).ToArray();

            Assert.True(Encoding.UTF8.GetByteCount(stdout) <= cap);
            Assert.Equal("compact", root.GetProperty("format").GetString());
            Assert.Equal(3, root.GetProperty("count").GetInt32());
            Assert.True(root.GetProperty("emitted_count").GetInt32() < 3);
            Assert.Equal(root.GetProperty("emitted_count").GetInt32(), files.Length);
            Assert.True(root.GetProperty("omitted_count").GetInt32() > 0);
            Assert.True(root.GetProperty("truncated").GetBoolean());
            Assert.True(root.GetProperty("byte_limit_reached").GetBoolean());
            Assert.Contains("max_json_bytes", omittedBy);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFilesAndMap_ExcludeTestsKeepExplicitPathsInsideSourceBaseline_Issues3918_4754_5229()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_files_exclude_tests_source_3918");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, ".agent_harness/command_guard_core.py", "python", "def guard_harness():\n    pass\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "class App {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Feature/Other.cs", "csharp", "class Other {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Nested/Worker.cs", "csharp", "class Worker {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Generated.g.cs", "csharp", "class Generated {}\n", isGenerated: true);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/CodeIndex/Cli/SearchAuditRecipes.cs", "csharp", "class AuditRecipes {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "tests/AppTests.cs", "csharp", "class AppTests {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "tools/Tool.cs", "csharp", "class Tool {}\n");

            HashSet<string> RunFiles(params string[] additionalArgs)
            {
                var filesArgs = new[] { "--db", dbPath, "--json=array", "--exclude-tests", "--limit", "10" }
                    .Concat(additionalArgs)
                    .ToArray();
                var (filesExitCode, filesStdout, filesStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                    filesArgs,
                    _jsonOptions));

                using var filesDocument = ParseJsonOutput(filesStdout);
                Assert.Equal(CommandExitCodes.Success, filesExitCode);
                Assert.Equal(string.Empty, filesStderr);
                return filesDocument.RootElement
                    .EnumerateArray()
                    .Select(file => file.GetProperty("path").GetString()!)
                    .ToHashSet(StringComparer.Ordinal);
            }

            JsonElement RunFilesCount(params string[] additionalArgs)
            {
                var countArgs = new[] { "--db", dbPath, "--json", "--count", "--exclude-tests" }
                    .Concat(additionalArgs)
                    .ToArray();
                var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                    countArgs,
                    _jsonOptions));
                using var countDocument = ParseJsonOutput(countStdout);

                Assert.Equal(CommandExitCodes.Success, countExitCode);
                Assert.Equal(string.Empty, countStderr);
                return countDocument.RootElement.Clone();
            }

            var baseline = RunFiles();
            var broad = RunFiles("--path", "**");
            var sourceBroad = RunFiles("--path", "src/**");
            var narrow = RunFiles("--path", "src/Nested/**");
            var multiple = RunFiles("--path", "src/App.cs", "--path", "src/Feature/**");
            var explicitlyExcluded = RunFiles("--path", "src/**", "--exclude-path", "src/Feature/**");
            var caseVariant = RunFiles("--path", "SRC/NESTED/**");
            var generatedIncluded = RunFiles("--include-generated");

            Assert.Equal(
                new[] { "src/App.cs", "src/Feature/Other.cs", "src/Nested/Worker.cs" },
                baseline.OrderBy(path => path, StringComparer.Ordinal));
            Assert.True(broad.IsSubsetOf(baseline));
            Assert.True(sourceBroad.IsSubsetOf(baseline));
            Assert.Equal(baseline, broad);
            Assert.Equal(baseline, sourceBroad);
            Assert.Equal(["src/Nested/Worker.cs"], narrow);
            Assert.Equal(["src/App.cs", "src/Feature/Other.cs"], multiple);
            Assert.Equal(["src/App.cs", "src/Nested/Worker.cs"], explicitlyExcluded);
            Assert.Equal(narrow, caseVariant);
            Assert.Contains("src/Generated.g.cs", generatedIncluded);
            Assert.DoesNotContain("src/CodeIndex/Cli/SearchAuditRecipes.cs", generatedIncluded);
            Assert.DoesNotContain("tests/AppTests.cs", generatedIncluded);
            Assert.DoesNotContain("tools/Tool.cs", generatedIncluded);
            Assert.DoesNotContain(".agent_harness/command_guard_core.py", generatedIncluded);

            foreach (var scope in new[]
                     {
                         (Paths: baseline, Args: Array.Empty<string>()),
                         (Paths: broad, Args: new[] { "--path", "**" }),
                         (Paths: narrow, Args: new[] { "--path", "src/Nested/**" }),
                         (Paths: multiple, Args: new[] { "--path", "src/App.cs", "--path", "src/Feature/**" }),
                         (Paths: explicitlyExcluded, Args: new[] { "--path", "src/**", "--exclude-path", "src/Feature/**" }),
                     })
            {
                Assert.Equal(scope.Paths.Count, RunFilesCount(scope.Args).GetProperty("count").GetInt32());
            }

            var explicitExcludeCount = RunFilesCount("--path", "src/**", "--exclude-path", "src/Feature/**");
            var effectiveScope = explicitExcludeCount
                .GetProperty("query_context")
                .GetProperty("effective_path_scope");
            Assert.Equal("and", effectiveScope.GetProperty("include_group_operator").GetString());
            Assert.Equal("or", effectiveScope.GetProperty("patterns_within_include_group_operator").GetString());
            Assert.Equal("or", effectiveScope.GetProperty("exclude_group_operator").GetString());
            var includeGroups = effectiveScope.GetProperty("include_groups").EnumerateArray().ToArray();
            var excludeGroups = effectiveScope.GetProperty("exclude_groups").EnumerateArray().ToArray();
            Assert.Equal(["implicit_source_baseline", "explicit_cli"], includeGroups.Select(group => group.GetProperty("origin").GetString()));
            Assert.Equal(["src/**"], includeGroups[0].GetProperty("patterns").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(["src/**"], includeGroups[1].GetProperty("patterns").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(["implicit_source_baseline", "explicit_cli"], excludeGroups.Select(group => group.GetProperty("origin").GetString()));
            Assert.Contains("src/CodeIndex/Cli/SearchAuditRecipes.cs", excludeGroups[0].GetProperty("patterns").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(["src/Feature/**"], excludeGroups[1].GetProperty("patterns").EnumerateArray().Select(item => item.GetString()));

            var (mapExitCode, mapStdout, mapStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json", "--sections", "summary", "--exclude-tests", "--path", "src/Nested/**"],
                _jsonOptions));
            using var mapDocument = ParseJsonOutput(mapStdout);
            Assert.Equal(CommandExitCodes.Success, mapExitCode);
            Assert.Equal(string.Empty, mapStderr);
            Assert.Equal(narrow.Count, mapDocument.RootElement.GetProperty("file_count").GetInt32());
            Assert.Equal(
                "and",
                mapDocument.RootElement
                    .GetProperty("query_context")
                    .GetProperty("effective_path_scope")
                    .GetProperty("include_group_operator")
                    .GetString());
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
    public void RunStatus_ReadOnlyFlagReportsImmutableRiskAcrossJsonCommands_Issue4555()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status_readonly");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                new DbWriter(db).SetMeta(
                    DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey,
                    DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
            }
            SqliteConnection.ClearAllPools();

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
            Assert.Equal(SqliteConnectionPolicy.ImmutableReadOnlyUriModeName, policy.GetProperty("active_mode").GetString());
            Assert.True(policy.GetProperty("immutable_uri").GetBoolean());
            Assert.True(document.RootElement.GetProperty("wal_stale_snapshot_risk").GetBoolean());
            Assert.Equal("explicit_immutable_read_only", document.RootElement.GetProperty("wal_stale_snapshot_reason").GetString());
            var ftsOptimization = document.RootElement
                .GetProperty("maintenance_guidance")
                .GetProperty("fts_optimization");
            Assert.False(ftsOptimization.GetProperty("recommended").GetBoolean());
            Assert.Equal("none", ftsOptimization.GetProperty("action").GetString());
            Assert.Equal("maintenance_snapshot_stale", ftsOptimization.GetProperty("reason").GetString());
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                ftsOptimization.GetProperty("observed_writes").GetInt64());
            Assert.Equal("stale", ftsOptimization.GetProperty("state").GetString());
            Assert.Equal(SqliteConnectionPolicy.DefaultCommandTimeoutSeconds, policy.GetProperty("command_timeout_seconds").GetInt32());
            Assert.True(policy.GetProperty("long_running_commands_require_cancellation").GetBoolean());
            Assert.False(policy.GetProperty("read_only_fallback").GetBoolean());

            var (filesExit, filesStdout, filesStderr) = CaptureConsole(() => QueryCommandRunner.RunFiles(
                ["--db", options.DbPath, "--json", "--limit", "1"],
                _jsonOptions));
            var filesLines = filesStdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, filesLines.Length);
            using var filesDone = JsonDocument.Parse(filesLines[1]);
            Assert.Equal(CommandExitCodes.Success, filesExit);
            Assert.Equal(string.Empty, filesStderr);
            Assert.True(filesDone.RootElement.GetProperty("terminal_record").GetBoolean());
            Assert.True(filesDone.RootElement.GetProperty("wal_stale_snapshot_risk").GetBoolean());

            var (outlineExit, outlineStdout, outlineStderr) = CaptureConsole(() => QueryCommandRunner.RunOutline(
                ["src/app.cs", "--db", options.DbPath, "--json"],
                _jsonOptions));
            using var outlineDocument = ParseJsonOutput(outlineStdout);
            Assert.Equal(CommandExitCodes.Success, outlineExit);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.True(outlineDocument.RootElement.GetProperty("wal_stale_snapshot_risk").GetBoolean());

            var (sarifExit, sarifStdout, sarifStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["class", "--db", options.DbPath, "--format", "sarif", "--limit", "1"],
                _jsonOptions));
            using var sarifDocument = ParseJsonOutput(sarifStdout);
            Assert.Equal(CommandExitCodes.Success, sarifExit);
            Assert.Equal(string.Empty, sarifStderr);
            Assert.True(sarifDocument.RootElement.GetProperty("wal_stale_snapshot_risk").GetBoolean());
            Assert.Equal("explicit_immutable_read_only", sarifDocument.RootElement.GetProperty("wal_stale_snapshot_reason").GetString());

            var (lspExit, lspStdout, lspStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["class", "--db", options.DbPath, "--format", "lsp", "--limit", "1"],
                _jsonOptions));
            using var lspDocument = ParseJsonOutput(lspStdout);
            Assert.Equal(CommandExitCodes.Success, lspExit);
            Assert.Equal(string.Empty, lspStderr);
            Assert.True(lspDocument.RootElement[0].GetProperty("wal_stale_snapshot_risk").GetBoolean());

            var (ndjsonExit, ndjsonStdout, ndjsonStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["class", "--db", options.DbPath, "--json=ndjson", "--limit", "1"],
                _jsonOptions));
            var ndjsonLines = ndjsonStdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            using var ndjsonResult = JsonDocument.Parse(ndjsonLines[0]);
            using var ndjsonDone = JsonDocument.Parse(ndjsonLines[1]);
            Assert.Equal(CommandExitCodes.Success, ndjsonExit);
            Assert.Equal(string.Empty, ndjsonStderr);
            Assert.True(ndjsonResult.RootElement.GetProperty("wal_stale_snapshot_risk").GetBoolean());
            Assert.True(ndjsonDone.RootElement.GetProperty("wal_stale_snapshot_risk").GetBoolean());

            var (emptyArrayExit, emptyArrayStdout, emptyArrayStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["__missing_issue4555__", "--db", options.DbPath, "--json=array"],
                _jsonOptions));
            using var emptyArrayDocument = ParseJsonOutput(emptyArrayStdout);
            var diagnosticsOnly = Assert.Single(emptyArrayDocument.RootElement.EnumerateArray());
            Assert.Equal(CommandExitCodes.Success, emptyArrayExit);
            Assert.Equal(string.Empty, emptyArrayStderr);
            Assert.True(diagnosticsOnly.GetProperty("diagnostic_only").GetBoolean());
            Assert.Equal("sqlite_stale_snapshot_risk", diagnosticsOnly.GetProperty("diagnostic_type").GetString());
            Assert.True(diagnosticsOnly.GetProperty("wal_stale_snapshot_risk").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
