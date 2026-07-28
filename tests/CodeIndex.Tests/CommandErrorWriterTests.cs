using CodeIndex.Cli;
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
    [InlineData("/Users/alice/private/codeindex.db")]
    [InlineData(@"C:\Users\alice\private\codeindex.db")]
    [InlineData("file:///Users/alice/private/codeindex.db?immutable=1")]
    public void MaintenanceClassifier_RedactsPlatformAbsolutePathsUnlessExplicitlyEnabled_Issue4856(
        string dbPath)
    {
        var redacted = MaintenanceDatabaseErrorClassifier.FormatPathForOutput(dbPath, showPaths: false);
        var diagnostic = MaintenanceDatabaseErrorClassifier.FormatPathForOutput(dbPath, showPaths: true);

        Assert.NotEqual(dbPath, redacted);
        Assert.DoesNotContain("alice", redacted, StringComparison.Ordinal);
        Assert.Equal(dbPath, diagnostic);
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
