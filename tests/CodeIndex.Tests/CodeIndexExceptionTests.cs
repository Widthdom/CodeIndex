using System.Reflection;
using System.Text.Json;
using CodeIndex;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Coverage for the <see cref="CodeIndexException"/> base type and the CLI / MCP
/// formatters that surface its <c>Code / Category / Path / Hint</c> fields uniformly (#1580).
/// CodeIndexException と、その <c>Code / Category / Path / Hint</c> を一律に出す
/// CLI / MCP フォーマッタのカバレッジ (#1580)。
/// </summary>
[Collection("SQLite pool sensitive")]
public class CodeIndexExceptionTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void Constructor_PathProvided_AppendsPathToMessage()
    {
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbLocked,
            category: CodeIndexExceptionCategory.Database,
            message: "Failed to open SQLite connection after retries.",
            path: "/tmp/cdidx.db",
            hint: "Close other cdidx invocations.");

        Assert.Equal(CommandErrorCodes.DbLocked, ex.Code);
        Assert.Equal(CodeIndexExceptionCategory.Database, ex.Category);
        Assert.Equal("/tmp/cdidx.db", ex.Path);
        Assert.Equal("Close other cdidx invocations.", ex.Hint);
        Assert.Contains("Failed to open SQLite connection", ex.Message);
        Assert.Contains("/tmp/cdidx.db", ex.Message);
    }

    [Fact]
    public void Constructor_NoPath_LeavesMessageUntouched()
    {
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbError,
            category: CodeIndexExceptionCategory.Database,
            message: "Generic failure.");

        Assert.Equal("Generic failure.", ex.Message);
        Assert.Null(ex.Path);
        Assert.Null(ex.Hint);
    }

    [Fact]
    public void Constructor_NullCode_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CodeIndexException(
            code: null!,
            category: CodeIndexExceptionCategory.Database,
            message: "msg"));
    }

    [Fact]
    public void Constructor_NullCategory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CodeIndexException(
            code: CommandErrorCodes.DbError,
            category: null!,
            message: "msg"));
    }

    [Fact]
    public void Constructor_EmptyMessage_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CodeIndexException(
            code: CommandErrorCodes.DbError,
            category: CodeIndexExceptionCategory.Database,
            message: "   "));
    }

    [Fact]
    public void Constructor_InnerException_IsPreserved()
    {
        var inner = new InvalidOperationException("inner");

        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbError,
            category: CodeIndexExceptionCategory.Database,
            message: "outer",
            innerException: inner);

        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void Formatter_NoJsonFlag_WritesHumanLines()
    {
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbLocked,
            category: CodeIndexExceptionCategory.Database,
            message: "Failed to open SQLite connection after retries.",
            path: "/var/cdidx/state.db",
            hint: "Wait for the other indexer to finish.");

        var (stdout, stderr) = CaptureConsole(() =>
            CodeIndexExceptionFormatter.Write(ex, ["status"], _jsonOptions));

        Assert.Empty(stdout);
        Assert.Contains("[E002_DB_LOCKED]", stderr);
        Assert.Contains("Failed to open SQLite connection", stderr);
        Assert.Contains("Path: /var/cdidx/state.db", stderr);
        Assert.Contains("Hint: Wait for the other indexer to finish.", stderr);
    }

    [Fact]
    public void Formatter_NoJsonFlag_OmitsPathAndHintLinesWhenAbsent()
    {
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbError,
            category: CodeIndexExceptionCategory.Database,
            message: "Generic failure.");

        var (_, stderr) = CaptureConsole(() =>
            CodeIndexExceptionFormatter.Write(ex, ["status"], _jsonOptions));

        Assert.Contains("[E008_DB_ERROR]", stderr);
        Assert.DoesNotContain("Path:", stderr);
        Assert.DoesNotContain("Hint:", stderr);
    }

    [Fact]
    public void Formatter_JsonFlag_EmitsStructuredEnvelope()
    {
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbLocked,
            category: CodeIndexExceptionCategory.Database,
            message: "Failed to open SQLite connection after retries.",
            path: "/var/cdidx/state.db",
            hint: "Wait for the other indexer to finish.");

        var (stdout, stderr) = CaptureConsole(() =>
            CodeIndexExceptionFormatter.Write(ex, ["status", "--json"], _jsonOptions));

        Assert.Empty(stderr);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal("E002_DB_LOCKED", root.GetProperty("error_code").GetString());
        Assert.Equal("database", root.GetProperty("category").GetString());
        Assert.Equal("/var/cdidx/state.db", root.GetProperty("path").GetString());
        Assert.Equal("Wait for the other indexer to finish.", root.GetProperty("hint").GetString());
        Assert.Contains("Failed to open SQLite connection", root.GetProperty("message").GetString());
    }

    [Fact]
    public void HasJsonFlag_RespectsDoubleDashBoundary()
    {
        Assert.False(CodeIndexExceptionFormatter.HasJsonFlag(["status"]));
        Assert.True(CodeIndexExceptionFormatter.HasJsonFlag(["status", "--json"]));
        Assert.False(CodeIndexExceptionFormatter.HasJsonFlag(["status", "--", "--json"]));
    }

    [Fact]
    public void OpenSqliteConnectionWithRetry_ZeroAttempts_ThrowsArgumentOutOfRange()
    {
        // Guard the degenerate maxOpenAttempts=0 case so the bottom CodeIndexException
        // is never thrown with a null inner exception (would mask the misuse).
        // maxOpenAttempts=0 で末尾の throw に落ちると inner exception が null になり
        // 呼び出しミスを隠してしまうため、入口で ArgumentOutOfRange を投げる。
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DbContext.OpenSqliteConnectionWithRetry(
                () => new SqliteConnection("Data Source=:memory:"),
                static _ => { },
                sleep: null,
                maxOpenAttempts: 0));
    }

    [Fact]
    public void OpenSqliteConnectionWithRetry_ExhaustedRetries_RaisesCodeIndexExceptionWithPath()
    {
        // #1580: when the busy retry loop exhausts, the caller must get the DB path,
        // a stable error Code, and a recovery Hint — not a bare InvalidOperationException.
        // #1580: retry 枯渇時は path / Code / Hint を含む CodeIndexException を投げる。
        const string dbPath = "/tmp/cdidx_retry_exhaust.db";
        var sleeps = new List<int>();
        var attempts = 0;

        var ex = Assert.Throws<CodeIndexException>(() =>
            DbContext.OpenSqliteConnectionWithRetry(
                () => new SqliteConnection("Data Source=:memory:"),
                _ =>
                {
                    attempts++;
                    throw CreateTransientBusyException();
                },
                sleep: sleeps.Add,
                maxOpenAttempts: 3,
                dbPath: dbPath));

        Assert.Equal(CommandErrorCodes.DbLocked, ex.Code);
        Assert.Equal(CodeIndexExceptionCategory.Database, ex.Category);
        Assert.Equal(dbPath, ex.Path);
        Assert.False(string.IsNullOrEmpty(ex.Hint));
        Assert.Contains(dbPath, ex.Message);
        Assert.IsType<SqliteException>(ex.InnerException);
        Assert.Equal(3, attempts);
    }

    private static SqliteException CreateTransientBusyException()
    {
        var exception = Activator.CreateInstance(
            typeof(SqliteException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["busy", 5],
            culture: null) as SqliteException;
        return exception ?? throw new InvalidOperationException("Failed to create SqliteException for retry test.");
    }

    [Theory]
    [InlineData(CommandErrorCodes.DbNotFound, CommandExitCodes.NotFound)]
    [InlineData(CommandErrorCodes.DirectoryNotFound, CommandExitCodes.NotFound)]
    [InlineData(CommandErrorCodes.DbLocked, CommandExitCodes.TransientDatabaseError)]
    [InlineData(CommandErrorCodes.DbNotWritable, CommandExitCodes.DatabaseError)]
    [InlineData(CommandErrorCodes.DbIntegrityFailed, CommandExitCodes.DatabaseError)]
    [InlineData(CommandErrorCodes.SchemaTooNew, CommandExitCodes.DatabaseError)]
    [InlineData(CommandErrorCodes.TempStoreExhausted, CommandExitCodes.DatabaseError)]
    [InlineData(CommandErrorCodes.DbError, CommandExitCodes.DatabaseError)]
    [InlineData(CommandErrorCodes.FeatureUnavailable, CommandExitCodes.FeatureUnavailable)]
    [InlineData(CommandErrorCodes.UsageError, CommandExitCodes.InvalidArgument)]
    [InlineData(CommandErrorCodes.Interrupted, CommandExitCodes.CancelledBySignal)]
    [InlineData("E999_UNKNOWN", CommandExitCodes.DatabaseError)]
    public void MapCodeIndexExceptionExitCode_MapsKnownCodesToTaxonomy(string code, int expectedExitCode)
    {
        Assert.Equal(expectedExitCode, ProgramRunner.MapCodeIndexExceptionExitCode(code));
    }

    private static (string stdout, string stderr) CaptureConsole(Action body)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            body();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
        return (stdout.ToString(), stderr.ToString());
    }
}
