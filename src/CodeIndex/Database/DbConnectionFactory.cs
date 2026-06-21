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

    // SQLITE_READONLY(8), SQLITE_CANTOPEN(14), SQLITE_IOERR(10). A read-only filesystem
    // typically surfaces as CANTOPEN because -journal/-shm cannot be created.
    // read-only FS では -journal / -shm を作れず CANTOPEN(14) を返すことが多い。
    internal static bool IsReadOnlyOpenError(SqliteException ex) =>
        ex.SqliteErrorCode is 8 or 14 or 10;

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
    {
        if (SqliteFileUri.StartsWithFileScheme(dbPath))
        {
            if (!SqliteFileUri.TryValidateBounds(dbPath, out var boundsError))
                throw boundsError ?? new FormatException("Invalid SQLite file URI.");

            return AppendReadOnlyQuery(dbPath);
        }

        var fileUri = new Uri(Path.GetFullPath(dbPath)).AbsoluteUri;
        return $"{fileUri}?immutable=1&mode=ro";
    }

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

            var uri = new Uri(trimmed);
            if (!uri.IsFile)
            {
                failureReason = FileUriNotLocalFileReason;
                return false;
            }

            localPath = uri.LocalPath;
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
        try
        {
            var roBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
            };
            var conn = new SqliteConnection(roBuilder.ConnectionString);
            conn.Open();
            return conn;
        }
        catch (SqliteException ex) when (IsReadOnlyOpenError(ex))
        {
            // Attempt 2: immutable=1 URI. This bypasses -shm/-wal entirely, which is the only
            // way to survive a sandbox that cannot touch side files. Trade-off documented:
            // if the base DB has uncheckpointed WAL state, immutable will serve data that
            // predates those commits. We warn to stderr so the caller can see it, but do not
            // block — a file-size heuristic on `-wal` produces false positives (WAL files
            // remain allocated after checkpoint), and real hot-WAL detection requires the
            // very -shm/-wal access the sandbox is blocking. The explicit escape hatch
            // `--db file:///...?immutable=1` is the user's way to opt into the same
            // trade-off knowingly.
            // サンドボックスで -shm/-wal に触れない場合の最終手段。hot WAL 誤判定を避けるため、
            // ファイルサイズでの拒否はやめ、stderr 警告のみ出してフォールバック。
            CommandErrorWriter.WriteStderr("Warning: falling back to SQLite immutable=1 read-only open. " +
                "If the base DB has uncheckpointed WAL state, the snapshot may be stale. " +
                "Re-run cdidx on writable storage to checkpoint WAL if this matters.");

            // Build the connection string directly instead of routing through
            // SqliteConnectionStringBuilder. The builder quotes DataSource values that
            // contain special characters, and the extra quoting was enough in some sandboxes
            // (observed by Codex: raw sqlite3 file:///... ?immutable=1 succeeds while the
            // builder-wrapped form fails with SQLITE_CANTOPEN). Uri.AbsoluteUri already
            // percent-encodes everything unsafe in a connection-string context (spaces, %,
            // ;, ", ', etc. all become %XX), so a raw concatenation is still injection-safe
            // for this specific input shape. Mode=ReadOnly is redundant with immutable=1 but
            // kept explicit so cdidx's intent is visible in logs / traces.
            // builder は DataSource を quote して URI 解釈を壊すため直接組む。
            // Uri.AbsoluteUri が全ての危険文字を %-エンコードするので raw 連結でも injection 安全。
            var fileUri = new Uri(Path.GetFullPath(dbPath)).AbsoluteUri; // e.g. file:///abs/path.db
            var rawConnStr = $"Data Source={fileUri}?immutable=1;Mode=ReadOnly";
            var conn = new SqliteConnection(rawConnStr);
            conn.Open();
            usedImmutableFallback = true;
            return conn;
        }
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

        if (!cancellationToken.CanBeCanceled)
        {
            System.Threading.Thread.Sleep(milliseconds);
            return;
        }

        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static string AppendReadOnlyQuery(string uriText)
    {
        var separator = uriText.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var result = uriText;
        if (!uriText.Contains("immutable=1", StringComparison.OrdinalIgnoreCase))
        {
            result += $"{separator}immutable=1";
            separator = "&";
        }
        if (!uriText.Contains("mode=ro", StringComparison.OrdinalIgnoreCase))
            result += $"{separator}mode=ro";
        return result;
    }
}
