using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public class CommandErrorWriterTests
{
    [Theory]
    [InlineData(5, CommandErrorCodes.DbLocked, "database_locked", CommandExitCodes.TransientDatabaseError)]
    [InlineData(6, CommandErrorCodes.DbLocked, "database_locked", CommandExitCodes.TransientDatabaseError)]
    [InlineData(8, CommandErrorCodes.DbNotWritable, "database_not_writable", CommandExitCodes.DatabaseError)]
    [InlineData(11, CommandErrorCodes.DbIntegrityFailed, "database_corrupt", CommandExitCodes.DatabaseError)]
    [InlineData(26, CommandErrorCodes.DbNotDatabase, "database_not_a_database", CommandExitCodes.DatabaseError)]
    public void MaintenanceClassifier_UsesSqlitePrimaryCodes_Issue4856(
        int sqliteCode,
        string expectedErrorCode,
        string expectedCategory,
        int expectedExitCode)
    {
        var error = MaintenanceDatabaseErrorClassifier.FromException(
            "test maintenance",
            "/Users/alice/private/codeindex.db",
            showPaths: false,
            new SqliteException("message wording must not affect classification", sqliteCode));

        Assert.Equal(expectedErrorCode, error.ErrorCode);
        Assert.Equal(expectedCategory, error.Category);
        Assert.Equal(expectedExitCode, error.ExitCode);
        Assert.Equal(sqliteCode, error.SqliteErrorCode);
        Assert.DoesNotContain("alice", error.Path, StringComparison.Ordinal);
        if (sqliteCode == 11)
            Assert.DoesNotContain("backoff", error.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CommandErrorCodes.SchemaTooNew, CommandErrorCodes.SchemaTooNew, "database_schema_too_new", CommandExitCodes.DatabaseError)]
    [InlineData(CommandErrorCodes.DbLocked, CommandErrorCodes.DbLocked, "database_locked", CommandExitCodes.TransientDatabaseError)]
    public void MaintenanceClassifier_PreservesStructuredDatabaseFailures_Issue4856(
        string structuredCode,
        string expectedErrorCode,
        string expectedCategory,
        int expectedExitCode)
    {
        var sqlite = structuredCode == CommandErrorCodes.DbLocked
            ? new SqliteException("message wording must not affect classification", 5)
            : null;
        var exception = new CodeIndexException(
            structuredCode,
            CodeIndexExceptionCategory.Database,
            "structured database failure",
            path: "/Users/alice/private/codeindex.db",
            innerException: sqlite);

        var error = MaintenanceDatabaseErrorClassifier.FromException(
            "test maintenance",
            "/Users/alice/private/codeindex.db",
            showPaths: false,
            exception);

        Assert.Equal(expectedErrorCode, error.ErrorCode);
        Assert.Equal(expectedCategory, error.Category);
        Assert.Equal(expectedExitCode, error.ExitCode);
        Assert.DoesNotContain("alice", error.Path, StringComparison.Ordinal);
        if (structuredCode == CommandErrorCodes.DbLocked)
            Assert.Equal(5, error.SqliteErrorCode);
    }

    [Theory]
    [InlineData("/Users/alice/private/codeindex.db")]
    [InlineData("/Users/alice/private/review-missing.db")]
    [InlineData(@"C:\Users\alice\private\codeindex.db")]
    [InlineData(@"\\server\share\private\codeindex.db")]
    [InlineData("file:///Users/alice/private/codeindex.db?immutable=1")]
    public void MaintenanceClassifier_RedactsPlatformAbsolutePathsUnlessExplicitlyEnabled_Issue4856(
        string dbPath)
    {
        var redacted = MaintenanceDatabaseErrorClassifier.FormatPathForOutput(dbPath, showPaths: false);
        var diagnostic = MaintenanceDatabaseErrorClassifier.FormatPathForOutput(dbPath, showPaths: true);

        Assert.Equal("<redacted>", redacted);
        Assert.Equal(dbPath, diagnostic);
    }

    [Theory]
    [InlineData(nameof(ExistingCodeIndexDbValidationFailure.Inaccessible))]
    [InlineData(nameof(ExistingCodeIndexDbValidationFailure.InvalidTarget))]
    public void MaintenanceClassifier_UsesInaccessibleClassificationForPreflightFailures_Issue4856(
        string validationFailureName)
    {
        var validationFailure = Enum.Parse<ExistingCodeIndexDbValidationFailure>(validationFailureName);
        var error = MaintenanceDatabaseErrorClassifier.FromValidation(
            "vacuum",
            "/Users/alice/private/codeindex.db",
            showPaths: false,
            validationFailure,
            validationException: null);

        Assert.Equal(CommandErrorCodes.DbError, error.ErrorCode);
        Assert.Equal("database_inaccessible", error.Category);
        Assert.Equal(CommandExitCodes.DatabaseError, error.ExitCode);
        Assert.Equal("<redacted>", error.Path);
        Assert.Contains("readable regular database file", error.Hint);
        Assert.DoesNotContain("--rebuild", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void MaintenanceClassifier_KeepsRelativePathInDefaultDiagnostics_Issue4856()
    {
        const string relativePath = ".cdidx/codeindex.db";

        Assert.Equal(
            relativePath,
            MaintenanceDatabaseErrorClassifier.FormatPathForOutput(relativePath, showPaths: false));
    }

    [Fact]
    public void ResolveMachineContract_NotFoundUsesGenericCodeWithoutDomainContext_Issue4855()
    {
        var (errorCode, category) = CommandErrorWriter.ResolveMachineContract(CommandExitCodes.NotFound);

        Assert.Equal(CommandErrorCodes.CommandFailed, errorCode);
        Assert.Equal("not_found", category);
    }

    [Fact]
    public void Write_DoesNotDuplicateExistingUsagePrefix_Issue4244()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);

                CommandErrorWriter.Write(
                    "unsupported suggestions option.",
                    hint: "retry with a supported option.",
                    usage: "Usage: cdidx suggestions <list|show|export>");
            }
            finally
            {
                Console.SetError(originalError);
            }

            var output = stderr.ToString();
            Assert.Contains("Usage: cdidx suggestions <list|show|export>", output);
            Assert.DoesNotContain("Usage: Usage:", output);
        }
    }
}
