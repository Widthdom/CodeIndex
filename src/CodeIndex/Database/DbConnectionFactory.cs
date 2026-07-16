using CodeIndex.Cli;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CodeIndex.Database;

internal static class DbConnectionFactory
{
    private static readonly AsyncLocal<Func<string, SqliteConnection>?> ScopedOpenReadOnlyForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedQueryOnlySnapshotCapturedForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedQueryOnlySnapshotDirectoryCreatedForTesting = new();
    private static readonly AsyncLocal<Action<string, string>?> ScopedQueryOnlySnapshotFileCopyingForTesting = new();

    internal static Func<string, SqliteConnection>? OpenReadOnlyForTesting
    {
        get => ScopedOpenReadOnlyForTesting.Value;
        set => ScopedOpenReadOnlyForTesting.Value = value;
    }

    internal static Action? QueryOnlySnapshotCapturedForTesting
    {
        get => ScopedQueryOnlySnapshotCapturedForTesting.Value;
        set => ScopedQueryOnlySnapshotCapturedForTesting.Value = value;
    }

    internal static Action<string>? QueryOnlySnapshotDirectoryCreatedForTesting
    {
        get => ScopedQueryOnlySnapshotDirectoryCreatedForTesting.Value;
        set => ScopedQueryOnlySnapshotDirectoryCreatedForTesting.Value = value;
    }

    internal static Action<string, string>? QueryOnlySnapshotFileCopyingForTesting
    {
        get => ScopedQueryOnlySnapshotFileCopyingForTesting.Value;
        set => ScopedQueryOnlySnapshotFileCopyingForTesting.Value = value;
    }

    internal const string QueryOnlySnapshotCopyFailedCode = "query_only_snapshot_copy_failed";

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

    /// <summary>
    /// Build a query-only connection that cannot create or touch source WAL sidecars.
    /// source WAL sidecar を作成・更新しない query-only connection を構築する。
    /// </summary>
    internal static SqliteConnection CreateArtifactPreservingQueryOnlyConnection(
        string dbPath,
        bool pooling,
        out bool immutableSnapshot,
        out bool immutableWalRisk)
        => CreateArtifactPreservingQueryOnlyConnection(
            dbPath,
            pooling,
            out immutableSnapshot,
            out immutableWalRisk,
            out _,
            out _);

    internal static SqliteConnection CreateArtifactPreservingQueryOnlyConnection(
        string dbPath,
        bool pooling,
        out bool immutableSnapshot,
        out bool immutableWalRisk,
        out bool detachedSnapshot)
        => CreateArtifactPreservingQueryOnlyConnection(
            dbPath,
            pooling,
            out immutableSnapshot,
            out immutableWalRisk,
            out detachedSnapshot,
            out _);

    internal static SqliteConnection CreateArtifactPreservingQueryOnlyConnection(
        string dbPath,
        bool pooling,
        out bool immutableSnapshot,
        out bool immutableWalRisk,
        out bool detachedSnapshot,
        out QueryOnlySnapshotSourceState? snapshotSourceState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        cancellationToken.ThrowIfCancellationRequested();
        detachedSnapshot = false;
        snapshotSourceState = null;

        if (OpenReadOnlyForTesting is { } openReadOnlyForTesting)
        {
            immutableSnapshot = false;
            immutableWalRisk = false;
            return openReadOnlyForTesting(dbPath);
        }

        immutableWalRisk = SqliteFileUri.RequestsImmutableSnapshot(dbPath);
        if (immutableWalRisk)
        {
            immutableSnapshot = true;
            return new SqliteConnection(SqliteConnectionPolicy.BuildConnectionString(
                dbPath,
                pooling
                    ? SqliteConnectionPolicyMode.ImmutableReadOnlyUri
                    : SqliteConnectionPolicyMode.ImmutableReadOnlyUriUnpooled));
        }

        var walState = InspectQueryOnlyWalState(dbPath, out var localDbPath);
        if (walState != QueryOnlyWalState.NotWal)
        {
            immutableWalRisk = false;
            detachedSnapshot = true;
            var connection = CreateStableWalSnapshotConnection(
                localDbPath,
                out var copiedHotWal,
                out var capturedState,
                cancellationToken);
            immutableSnapshot = !copiedHotWal;
            snapshotSourceState = capturedState;
            return connection;
        }

        immutableSnapshot = false;
        var mode = pooling
            ? SqliteConnectionPolicyMode.ReadOnly
            : SqliteConnectionPolicyMode.ReadOnlyUnpooled;
        return new SqliteConnection(SqliteConnectionPolicy.BuildConnectionString(dbPath, mode));
    }

    internal static bool IsQueryOnlySnapshotCurrent(
        string dbPath,
        QueryOnlySnapshotSourceState snapshotSourceState,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localDbPath = dbPath;
        if (SqliteFileUri.StartsWithFileScheme(dbPath))
        {
            if (!TryGetLocalPath(dbPath, out var parsedPath, out _) || parsedPath == null)
                return false;
            localDbPath = parsedPath;
        }

        return TryCaptureQueryOnlySnapshotState(localDbPath, cancellationToken, out var currentState)
            && currentState == snapshotSourceState;
    }

    private static SqliteConnection CreateStableWalSnapshotConnection(
        string localDbPath,
        out bool copiedHotWal,
        out QueryOnlySnapshotSourceState snapshotSourceState,
        CancellationToken cancellationToken)
    {
        const int maxCopyAttempts = 3;
        var normalizedDbPath = LongPath.EnsureWindowsPrefix(localDbPath);
        var walPath = normalizedDbPath + "-wal";
        DirectoryInfo snapshotDirectory;
        try
        {
            snapshotDirectory = DataDirectorySecurity.CreateSensitiveTempDirectory("query-wal-snapshot");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw CreateQueryOnlySnapshotCopyFailed(localDbPath, ex);
        }
        var snapshotDbPath = Path.Combine(snapshotDirectory.FullName, "snapshot.db");
        var snapshotWalPath = snapshotDbPath + "-wal";
        try
        {
            for (var attempt = 1; attempt <= maxCopyAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryCaptureQueryOnlySnapshotState(normalizedDbPath, cancellationToken, out var before))
                    continue;
                QueryOnlySnapshotCapturedForTesting?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    CopySnapshotFile(normalizedDbPath, snapshotDbPath, cancellationToken);
                    if (before.WalLength > 0)
                        CopySnapshotFile(walPath, snapshotWalPath, cancellationToken);
                    else if (File.Exists(snapshotWalPath))
                        File.Delete(snapshotWalPath);
                }
                catch (QueryOnlySnapshotSourceChangedException)
                {
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw CreateQueryOnlySnapshotCopyFailed(localDbPath, ex);
                }

                if (TryCaptureQueryOnlySnapshotState(snapshotDbPath, cancellationToken, out var copied)
                    && TryCaptureQueryOnlySnapshotState(normalizedDbPath, cancellationToken, out var after)
                    && before == copied
                    && before == after)
                {
                    copiedHotWal = before.WalLength > 0;
                    snapshotSourceState = before;
                    var connection = new SqliteConnection(SqliteConnectionPolicy.BuildConnectionString(
                        snapshotDbPath,
                        copiedHotWal
                            ? SqliteConnectionPolicyMode.ReadOnlyUnpooled
                            : SqliteConnectionPolicyMode.ImmutableReadOnlyUriUnpooled));
                    AttachSnapshotCleanup(connection, snapshotDirectory.FullName);
                    QueryOnlySnapshotDirectoryCreatedForTesting?.Invoke(snapshotDirectory.FullName);
                    return connection;
                }
            }
        }
        catch
        {
            TryDeleteSnapshotDirectory(snapshotDirectory.FullName);
            throw;
        }

        TryDeleteSnapshotDirectory(snapshotDirectory.FullName);
        throw new CodeIndexException(
            "query_only_wal_changed",
            CodeIndexExceptionCategory.Database,
            "Query-only open was refused because the database or WAL generation changed while an artifact-preserving snapshot was being created.",
            path: localDbPath,
            hint: "Let the writer finish and retry, or create a SQLite backup snapshot and query that snapshot.");
    }

    private static CodeIndexException CreateQueryOnlySnapshotCopyFailed(string localDbPath, Exception innerException)
        => new(
            QueryOnlySnapshotCopyFailedCode,
            CodeIndexExceptionCategory.Filesystem,
            "Query-only open could not copy the database into a private temporary snapshot.",
            path: localDbPath,
            hint: "Check temporary-storage capacity and permissions, then retry or query a SQLite backup snapshot.",
            innerException: innerException);

    private static void CopySnapshotFile(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream source;
        try
        {
            source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new QueryOnlySnapshotSourceChangedException(ex);
        }

        using (source)
        {
            QueryOnlySnapshotFileCopyingForTesting?.Invoke(sourcePath, destinationPath);
            using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            source.CopyToAsync(destination, 1024 * 1024, cancellationToken).GetAwaiter().GetResult();
            destination.Flush();
        }

        DataDirectorySecurity.ApplyPrivateFileMode(destinationPath);
    }

    private static bool TryCaptureQueryOnlySnapshotState(
        string localDbPath,
        CancellationToken cancellationToken,
        out QueryOnlySnapshotSourceState state)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedDbPath = LongPath.EnsureWindowsPrefix(localDbPath);
            Span<byte> dbHeader = stackalloc byte[100];
            long dbLength;
            int dbHeaderLength;
            using (var db = new FileStream(
                       normalizedDbPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                dbLength = db.Length;
                dbHeaderLength = ReadAtMost(db, dbHeader, cancellationToken);
            }

            if (dbHeaderLength < 20
                || !dbHeader[..16].SequenceEqual("SQLite format 3\0"u8)
                || dbHeader[18] != 2
                || dbHeader[19] != 2)
            {
                state = default;
                return false;
            }

            var walPath = normalizedDbPath + "-wal";
            long walLength = 0;
            string? walHeaderFingerprint = null;
            string? walLastFrameFingerprint = null;
            try
            {
                using var wal = new FileStream(
                    walPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                walLength = wal.Length;
                if (walLength > 0)
                {
                    Span<byte> walHeader = stackalloc byte[32];
                    var walHeaderLength = ReadAtMost(wal, walHeader, cancellationToken);
                    walHeaderFingerprint = Fingerprint(walHeader[..walHeaderLength]);

                    if (walHeaderLength == walHeader.Length)
                    {
                        var rawPageSize = BinaryPrimitives.ReadUInt32BigEndian(walHeader[8..12]);
                        var pageSize = rawPageSize == 1 ? 65536L : rawPageSize;
                        var frameSize = 24L + pageSize;
                        var frameCount = frameSize > 24 && walLength > walHeader.Length
                            ? (walLength - walHeader.Length) / frameSize
                            : 0;
                        if (frameCount > 0)
                        {
                            Span<byte> lastFrameHeader = stackalloc byte[24];
                            wal.Position = walHeader.Length + ((frameCount - 1) * frameSize);
                            var lastFrameHeaderLength = ReadAtMost(wal, lastFrameHeader, cancellationToken);
                            walLastFrameFingerprint = Fingerprint(lastFrameHeader[..lastFrameHeaderLength]);
                        }
                    }
                }
            }
            catch (FileNotFoundException)
            {
                walLength = 0;
            }

            state = new QueryOnlySnapshotSourceState(
                dbLength,
                Fingerprint(dbHeader[..dbHeaderLength]),
                walLength,
                walHeaderFingerprint,
                walLastFrameFingerprint);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            state = default;
            return false;
        }
    }

    private static int ReadAtMost(Stream stream, Span<byte> destination, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(destination[total..]);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    private static string Fingerprint(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static void AttachSnapshotCleanup(SqliteConnection connection, string snapshotDirectory)
    {
        var cleanupStarted = 0;
        connection.Disposed += (_, _) =>
        {
            if (Interlocked.Exchange(ref cleanupStarted, 1) == 0)
                TryDeleteSnapshotDirectory(snapshotDirectory);
        };
    }

    private static void TryDeleteSnapshotDirectory(string snapshotDirectory)
    {
        try
        {
            if (Directory.Exists(snapshotDirectory))
                Directory.Delete(snapshotDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GlobalToolLog.Error($"query_only_snapshot_cleanup_failed directory={snapshotDirectory} error={ex.GetType().Name}");
        }
    }

    private static QueryOnlyWalState InspectQueryOnlyWalState(string dbPath, out string localDbPath)
    {
        localDbPath = dbPath;
        if (SqliteFileUri.StartsWithFileScheme(dbPath))
        {
            if (!TryGetLocalPath(dbPath, out var parsedPath, out _) || parsedPath == null)
                return QueryOnlyWalState.NotWal;
            localDbPath = parsedPath;
        }

        try
        {
            var normalizedDbPath = LongPath.EnsureWindowsPrefix(localDbPath);
            Span<byte> header = stackalloc byte[20];
            using (var stream = new FileStream(
                       normalizedDbPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Read(header) < header.Length
                    || !header[..16].SequenceEqual("SQLite format 3\0"u8)
                    || header[18] != 2
                    || header[19] != 2)
                {
                    return QueryOnlyWalState.NotWal;
                }
            }

            var walPath = normalizedDbPath + "-wal";
            return File.Exists(walPath) && new FileInfo(walPath).Length > 0
                ? QueryOnlyWalState.HotWal
                : QueryOnlyWalState.CheckpointedWal;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return QueryOnlyWalState.NotWal;
        }
    }

    private enum QueryOnlyWalState
    {
        NotWal,
        CheckpointedWal,
        HotWal,
    }

    internal readonly record struct QueryOnlySnapshotSourceState(
        long DbLength,
        string DbHeaderFingerprint,
        long WalLength,
        string? WalHeaderFingerprint,
        string? WalLastFrameFingerprint);

    private sealed class QueryOnlySnapshotSourceChangedException(Exception innerException)
        : IOException("The query-only snapshot source disappeared while it was being copied.", innerException);

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
