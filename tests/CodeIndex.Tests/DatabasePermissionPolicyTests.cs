using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class DatabasePermissionPolicyTests
{
    [Fact]
    public void DbContext_BestEffortUnsupportedModeProvider_ContinuesWithStatusDiagnostic_Issue4559()
    {
        var directory = TestProjectHelper.CreateTempProject("db_permission_best_effort");
        var dbPath = Path.Combine(directory, "codeindex.db");
        var provider = new TestFileModeProvider(
            setException: static () => new NotSupportedException("mode changes are unsupported"));

        try
        {
            using var db = new DbContext(
                DbOpenIntent.WriteIndex,
                dbPath,
                DatabasePermissionPolicyMode.BestEffort,
                provider);
            db.InitializeSchema();

            var status = new DbReader(db, CancellationToken.None).GetStatus();
            Assert.Equal(DatabasePermissionPolicy.BestEffortName, status.DatabasePermissionPolicy);
            var diagnostic = status.DatabasePermissionDiagnostics!.Single(
                item => item is
                {
                    Operation: "set",
                    Target: "database",
                    Reason: "not_supported",
                });
            Assert.Contains("filesystem", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("Unix file modes", diagnostic.RecommendedAction, StringComparison.Ordinal);

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(status));
            Assert.Equal(
                DatabasePermissionPolicy.BestEffortName,
                json.RootElement.GetProperty("database_permission_policy").GetString());
            Assert.Contains(
                json.RootElement.GetProperty("database_permission_diagnostics").EnumerateArray(),
                item => item.GetProperty("reason").GetString() == "not_supported");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void DbContext_StrictPolicy_CancellableReaderPreservesEffectivePolicy_Issue4559()
    {
        var directory = TestProjectHelper.CreateTempProject("db_permission_strict_status");
        var dbPath = Path.Combine(directory, "codeindex.db");

        try
        {
            using var db = new DbContext(
                DbOpenIntent.WriteIndex,
                dbPath,
                DatabasePermissionPolicyMode.Strict,
                new TestFileModeProvider());
            db.InitializeSchema();

            var status = new DbReader(db, CancellationToken.None).GetStatus();

            Assert.Equal(DatabasePermissionPolicy.StrictName, status.DatabasePermissionPolicy);
            Assert.Null(status.DatabasePermissionDiagnostics);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void DbContext_StrictAccessDeniedModeProvider_FailsWithRemediation_Issue4559()
    {
        var directory = TestProjectHelper.CreateTempProject("db_permission_strict");
        var dbPath = Path.Combine(directory, "codeindex.db");
        var provider = new TestFileModeProvider(
            setException: static () => new UnauthorizedAccessException("chmod denied"));

        try
        {
            var exception = Assert.Throws<CodeIndexException>(() => new DbContext(
                DbOpenIntent.WriteIndex,
                dbPath,
                DatabasePermissionPolicyMode.Strict,
                provider));

            Assert.Equal(DatabasePermissionPolicy.FailureCode, exception.Code);
            Assert.Equal(CodeIndexExceptionCategory.Filesystem, exception.Category);
            Assert.Contains("strict mode", exception.Message, StringComparison.Ordinal);
            Assert.Contains("permission_denied", exception.Message, StringComparison.Ordinal);
            Assert.Contains(DatabasePermissionPolicy.EnvironmentVariable, exception.Hint, StringComparison.Ordinal);
            Assert.Contains(DatabasePermissionPolicy.BestEffortName, exception.Hint, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RunStatus_InvalidPermissionPolicy_PreservesStructuredErrorAndHint_Issue4559()
    {
        var directory = TestProjectHelper.CreateTempProject("db_permission_invalid_policy");
        var dbPath = Path.Combine(directory, "codeindex.db");

        try
        {
            using (var db = new DbContext(
                DbOpenIntent.WriteIndex,
                dbPath,
                DatabasePermissionPolicyMode.BestEffort,
                new TestFileModeProvider()))
            {
                db.InitializeSchema();
            }

            using var environment = EnvironmentVariableScope.Capture(DatabasePermissionPolicy.EnvironmentVariable);
            environment.Set(DatabasePermissionPolicy.EnvironmentVariable, "invalid");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                ProgramRunner.CreateDefaultJsonOptions()));

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
            using var json = JsonDocument.Parse(stdout);
            Assert.Equal(
                DatabasePermissionPolicy.InvalidPolicyCode,
                json.RootElement.GetProperty("error_code").GetString());
            Assert.Contains(
                DatabasePermissionPolicy.EnvironmentVariable,
                json.RootElement.GetProperty("hint").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void GetUnixFileModeString_InjectedFailures_FollowEffectivePolicy_Issue4559()
    {
        var unsupportedProvider = new TestFileModeProvider(
            getException: static () => new NotSupportedException("mode reads are unsupported"),
            alwaysExists: true);

        var mode = DbContext.GetUnixFileModeString(
            "virtual.db",
            DatabasePermissionPolicyMode.BestEffort,
            unsupportedProvider,
            out var bestEffortDiagnostic);

        Assert.Null(mode);
        Assert.NotNull(bestEffortDiagnostic);
        Assert.Equal("read", bestEffortDiagnostic.Operation);
        Assert.Equal("not_supported", bestEffortDiagnostic.Reason);

        var deniedProvider = new TestFileModeProvider(
            getException: static () => new UnauthorizedAccessException("mode read denied"),
            alwaysExists: true);
        StatusDatabasePermissionDiagnostic? strictDiagnostic = null;

        var exception = Assert.Throws<CodeIndexException>(() => DbContext.GetUnixFileModeString(
            "virtual.db",
            DatabasePermissionPolicyMode.Strict,
            deniedProvider,
            out strictDiagnostic));

        Assert.Equal(DatabasePermissionPolicy.FailureCode, exception.Code);
        Assert.NotNull(strictDiagnostic);
        Assert.Equal("permission_denied", strictDiagnostic.Reason);
        Assert.Contains("move the database", exception.Hint, StringComparison.OrdinalIgnoreCase);
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                return (action(), stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }
    }

    private sealed class TestFileModeProvider(
        Func<Exception>? setException = null,
        Func<Exception>? getException = null,
        bool alwaysExists = false) : IDatabaseFileModeProvider
    {
        public bool SupportsUnixFileModes => true;

        public bool FileExists(string path)
            => alwaysExists || File.Exists(path);

        public void SetUnixFileMode(string path, UnixFileMode mode)
        {
            if (setException != null)
                throw setException();
        }

        public UnixFileMode GetUnixFileMode(string path)
        {
            if (getException != null)
                throw getException();

            return UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
    }
}
