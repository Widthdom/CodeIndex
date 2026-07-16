using CodeIndex.Cli;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal static class DbConnectionFactory
{
    private static readonly AsyncLocal<Func<string, SqliteConnection>?> ScopedOpenReadOnlyForTesting = new();

    internal static Func<string, SqliteConnection>? OpenReadOnlyForTesting
    {
        get => ScopedOpenReadOnlyForTesting.Value;
        set => ScopedOpenReadOnlyForTesting.Value = value;
    }

    private const int SqliteCantOpen = 14;
    private const int SqliteCantOpenDirtyWal = SqliteCantOpen | (5 << 8);

    // SQLITE_READONLY is direct evidence that a read-only retry is appropriate. Generic
    // SQLITE_IOERR is deliberately excluded: media, transport, and corruption-adjacent I/O
    // failures must not be converted into a seemingly successful snapshot. CANTOPEN is only
    // eligible when its extended code is compatible with a DB/sidecar open failure and a
    // bounded filesystem probe confirms that the requested DB is an existing regular file.
    // READONLY は read-only retry の直接的な根拠。汎用 IOERR は成功に見える snapshot へ
    // 変換しない。CANTOPEN は extended code と実在する通常 DB file の確認後だけ対象にする。
    internal static bool IsReadOnlyOpenError(SqliteException ex, string? dbPath = null)
    {
        if (ex.SqliteErrorCode == 8)
            return true;
        if (ex.SqliteErrorCode != SqliteCantOpen
            || ex.SqliteExtendedErrorCode is not (SqliteCantOpen or SqliteCantOpenDirtyWal)
            || string.IsNullOrWhiteSpace(dbPath))
        {
            return false;
        }

        var localPath = dbPath;
        if (SqliteFileUri.StartsWithFileScheme(dbPath)
            && (!TryGetLocalPath(dbPath, out localPath, out _) || localPath == null))
        {
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(localPath);
            return (attributes & FileAttributes.Directory) == 0;
        }
        catch (Exception probeError) when (probeError is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static SqliteConnection OpenWithRetry(
        Func<SqliteConnection> createConnection,
        Action<SqliteConnection> openConnection,
        Action<int>? sleep = null,
        int maxOpenAttempts = 5,
        string? dbPath = null,
        CancellationToken cancellationToken = default)
    {
        if (maxOpenAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOpenAttempts), maxOpenAttempts, "Must be at least 1.");

        cancellationToken.ThrowIfCancellationRequested();
        SqliteConnection? connection = null;
        SqliteException? lastBusyError = null;
        for (var attempt = 1; attempt <= maxOpenAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            connection?.Dispose();
            connection = createConnection();
            try
            {
                openConnection(connection);
                return connection;
            }
            catch (SqliteException ex) when (IsTransientBusyError(ex))
            {
                // #1580: capture the busy error on every attempt — including the
                // last — so the end-of-loop throw can wrap it in a structured
                // CodeIndexException instead of leaking SqliteException to callers
                // (which previously made the bottom `throw` unreachable).
                // #1580: 末尾の throw を必ず通すために busy エラーを全試行で捕捉する。
                lastBusyError = ex;
                if (attempt < maxOpenAttempts)
                {
                    try
                    {
                        SleepBeforeRetry(50 * attempt, sleep, cancellationToken);
                    }
                    catch
                    {
                        connection.Dispose();
                        throw;
                    }
                }
            }
            catch (Exception)
            {
                connection.Dispose();
                throw;
            }
        }
        connection?.Dispose();

        // Issue #1580: surface the DB path and a recovery hint instead of a bare
        // `InvalidOperationException("Failed to ...")` so the caller (CLI / MCP) can
        // tell which database failed and which retry knob to suggest.
        // #1580: 失敗した DB のパスとリカバリ手順を構造化して投げる。
        throw new CodeIndexException(
            code: CommandErrorCodes.DbLocked,
            category: CodeIndexExceptionCategory.Database,
            message: "Failed to open SQLite connection after retries.",
            path: dbPath,
            hint: "Another process holds a write lock on the database. If another cdidx index is running, wait for it to finish; otherwise check for other SQLite clients (e.g. backup tools, DB browsers) accessing the file, then retry.",
            innerException: lastBusyError);
    }

    internal static string ToReadOnlyUri(string dbPath)
        => SqliteConnectionPolicy.ToReadOnlyUri(dbPath);

    internal const string FileUriPathParseFailedReason = "file_uri_path_parse_failed";
    internal const string FileUriParseFailedReason = "file_uri_parse_failed";
    internal const string FileUriNotLocalFileReason = "file_uri_not_local_file";

    // Best-effort: extract the filesystem path from a SQLite URI so -wal checks can run.
    // Returns null if parsing fails; the caller records why the freshness gate was skipped.
    // URI から filesystem path を取り出すベストエフォート。失敗理由は freshness 診断に載せる。
    internal static string? TryGetLocalPath(string uriText)
        => TryGetLocalPath(uriText, out var localPath, out _) ? localPath : null;

    internal static bool TryGetLocalPath(string uriText, out string? localPath, out string? failureReason)
    {
        localPath = null;
        failureReason = null;
        try
        {
            // Trim the query string (?immutable=1 etc.) before parsing so LocalPath is clean.
            if (!SqliteFileUri.TryGetPathBeforeQuery(uriText, out var trimmed, out _))
            {
                failureReason = FileUriPathParseFailedReason;
                return false;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || !uri.IsFile)
            {
                failureReason = FileUriNotLocalFileReason;
                return false;
            }

            if (!CodeIndex.FileUriPolicy.TryNormalizeFileUriPath(trimmed, out var normalizedPath, out _))
            {
                failureReason = FileUriPathParseFailedReason;
                return false;
            }

            localPath = normalizedPath;
            return true;
        }
        catch (UriFormatException)
        {
            failureReason = FileUriParseFailedReason;
            return false;
        }
    }

    internal static SqliteConnection OpenReadOnly(string dbPath)
        => OpenReadOnly(dbPath, out _);

    internal static SqliteConnection OpenReadOnly(string dbPath, out bool usedImmutableFallback)
    {
        if (OpenReadOnlyForTesting is { } openReadOnlyForTesting)
        {
            usedImmutableFallback = false;
            return openReadOnlyForTesting(dbPath);
        }

        usedImmutableFallback = false;
        // Attempt 1: Mode=ReadOnly. Works for most read-only FS scenarios and, crucially,
        // still reads hot -wal state so nothing committed but not yet checkpointed is lost.
        // 第一段: Mode=ReadOnly。多くの read-only 環境で動作し、hot -wal の未チェックポイント
        // 済みコミットも正しく読める。
        var conn = new SqliteConnection(SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ReadOnly));
        conn.Open();
        return conn;
    }

    internal static bool IsTransientBusyError(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6;

    internal static void SleepBeforeRetry(int milliseconds, Action<int>? sleep, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sleep != null)
        {
            sleep(milliseconds);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        WaitForRetryDelay(milliseconds, cancellationToken);
    }

    private static void WaitForRetryDelay(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        using var delay = new ManualResetEventSlim(initialState: false);
        if (cancellationToken.CanBeCanceled)
        {
            delay.Wait(milliseconds, cancellationToken);
            return;
        }

        delay.Wait(milliseconds);
    }
}
