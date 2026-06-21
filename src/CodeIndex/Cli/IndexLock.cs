using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

/// <summary>
/// Process-exclusive lock for `cdidx index` runs against a single database file.
/// SQLite's `busy_timeout` only serializes individual writes, so two concurrent
/// `cdidx index` invocations could otherwise interleave schema and data work and
/// leave the DB in a corrupted half-and-half state. The holder opens the lock
/// file with <see cref="FileShare.None"/> for cross-process exclusion (matching
/// the precedent in <c>SuggestionStore.WithFileLock</c>) and writes PID/start-time
/// metadata to a sibling <c>.info</c> file so a second cdidx can identify the
/// conflicting holder before exiting.
/// 単一の DB ファイルに対する `cdidx index` 実行を排他化するためのロック。
/// SQLite の `busy_timeout` は個々の write しか直列化しないため、2 つの
/// `cdidx index` が同時に走るとスキーマ操作とデータ書き込みが交錯し DB が
/// 破損し得る。保持者は <c>SuggestionStore.WithFileLock</c> と同じく
/// <see cref="FileShare.None"/> でロックファイルを開いてプロセス間排他を確保し、
/// PID と起動時刻を隣接 <c>.info</c> ファイルに書き出して、2 つ目の cdidx が
/// 終了前に競合相手を表示できるようにする。
/// </summary>
internal sealed class IndexLock : IDisposable
{
    private const int MaxInfoBytes = 16 * 1024;

    private readonly FileStream _stream;
    private readonly string _lockPath;
    private readonly string _infoPath;
    private bool _disposed;

    internal static Action<string> DeleteFileForTesting { get; set; } = File.Delete;
    internal static Action<LockCleanupDiagnostic>? CleanupDiagnosticSinkForTesting { get; set; }
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    private static readonly TimeSpan HolderStartTimeTolerance = TimeSpan.FromSeconds(2);

    private static DateTime GetUtcNow() => TimeProvider.GetUtcNow().UtcDateTime;

    private IndexLock(FileStream stream, string lockPath, string infoPath)
    {
        _stream = stream;
        _lockPath = lockPath;
        _infoPath = infoPath;
    }

    /// <summary>
    /// Resolve the lockfile path next to the resolved database path.
    /// 解決済み DB パスの隣接ロックファイルパスを返す。
    /// </summary>
    public static string GetLockPath(string resolvedDbPath) => resolvedDbPath + ".lock";

    /// <summary>
    /// Resolve the metadata sidecar path next to the lockfile.
    /// ロックファイルの隣接メタデータパスを返す。
    /// </summary>
    public static string GetInfoPath(string lockPath) => lockPath + ".info";

    /// <summary>
    /// Try to acquire the lock. Throws <see cref="IndexLockConflictException"/> when
    /// another holder owns the lockfile. Stale lockfiles left by a crashed cdidx
    /// release the OS lock automatically, so this call recovers without manual cleanup.
    /// ロック取得を試みる。他のプロセスが保持していれば
    /// <see cref="IndexLockConflictException"/> を投げる。クラッシュで残った lockfile は
    /// OS が自動でロックを解放するため、手動清掃なしで回復する。
    /// </summary>
    public static IndexLock Acquire(string lockPath, string projectPath)
    {
        var dir = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var infoPath = GetInfoPath(lockPath);

        FileStream stream;
        try
        {
            // FileShare.None gives cross-process exclusion on every platform we
            // support, the same approach SuggestionStore uses. The diagnostic
            // metadata for competitors lives in a sibling .info file so we never
            // need to relax this share mode.
            // FileShare.None は全プラットフォームでプロセス間の排他を提供する
            // （SuggestionStore と同じ手法）。競合相手向け診断メタデータは隣接
            // .info ファイルに分離されるため、この共有モードを緩める必要はない。
            stream = ExclusiveFileLock.Open(lockPath);
        }
        catch (IOException ex)
        {
            var holder = TryReadHolderInfo(lockPath);
            throw new IndexLockConflictException(lockPath, holder, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            var holder = TryReadHolderInfo(lockPath);
            if (holder == null)
                throw;

            throw new IndexLockConflictException(lockPath, holder, ex);
        }

        try
        {
            var info = new IndexLockInfo(
                Pid: Environment.ProcessId,
                StartedAt: GetCurrentProcessStartTimeUtc(),
                Verification: IndexLockHolderVerification.Verified);
            ExclusiveFileLock.WriteHolderInfo(infoPath, SerializeInfo(info), Encoding.UTF8);
        }
        catch (Exception)
        {
            stream.Dispose();
            throw;
        }

        return new IndexLock(stream, lockPath, infoPath);
    }

    /// <summary>
    /// Read the holder metadata if present. Returns null when the file is missing,
    /// empty, or the metadata cannot be parsed.
    /// 保持者メタデータを読む。ファイルが無い・空・解析不能の場合は null。
    /// </summary>
    public static IndexLockInfo? TryReadHolderInfo(string lockPath)
    {
        var infoPath = GetInfoPath(lockPath);
        try
        {
            if (!ExclusiveFileLock.TryReadHolderInfoText(infoPath, MaxInfoBytes, out var text))
                return null;
            var info = ParseInfo(text!);
            return info is null
                ? null
                : info with { Verification = VerifyHolder(info) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        ExclusiveFileLock.TryDeleteCleanupTarget(_infoPath, "index_lock", "metadata", DeleteFileForTesting, CleanupDiagnosticSinkForTesting);

        try
        {
            _stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Best-effort. / ベストエフォート。
        }
    }

    // --- Tiny key=value serializer (avoids touching JsonSerializerContext) ---
    // --- 小さな key=value シリアライザ（JsonSerializerContext を触らない） ---

    private static string SerializeInfo(IndexLockInfo info)
    {
        var sb = new StringBuilder();
        sb.Append("pid=").Append(info.Pid.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("started_at=").Append(info.StartedAt.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    private static IndexLockInfo? ParseInfo(string text)
    {
        int? pid = null;
        DateTime? started = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrEmpty(line))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            var key = line[..eq];
            var value = UnescapeValue(line[(eq + 1)..]);
            switch (key)
            {
                case "pid":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0)
                        pid = p;
                    break;
                case "started_at":
                    if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s))
                        started = s;
                    break;
            }
        }

        if (pid is null || started is null)
            return null;
        return new IndexLockInfo(pid.Value, started.Value);
    }

    private static DateTime GetCurrentProcessStartTimeUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return GetUtcNow();
        }
    }

    private static IndexLockHolderVerification VerifyHolder(IndexLockInfo info)
    {
        try
        {
            using var process = Process.GetProcessById(info.Pid);
            if (process.HasExited)
                return IndexLockHolderVerification.Stale;

            var processStartedAt = process.StartTime.ToUniversalTime();
            var holderStartedAt = NormalizeUtc(info.StartedAt);
            return (processStartedAt - holderStartedAt).Duration() <= HolderStartTimeTolerance
                ? IndexLockHolderVerification.Verified
                : IndexLockHolderVerification.Stale;
        }
        catch (ArgumentException)
        {
            return IndexLockHolderVerification.Stale;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            return IndexLockHolderVerification.Unverified;
        }
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string UnescapeValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    '\\' => '\\',
                    _ => next,
                });
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

}

internal sealed record IndexLockInfo(
    int Pid,
    DateTime StartedAt,
    IndexLockHolderVerification Verification = IndexLockHolderVerification.Unverified);

internal enum IndexLockHolderVerification
{
    Verified,
    Unverified,
    Stale,
}

internal sealed class IndexLockConflictException : Exception
{
    public string LockPath { get; }
    public IndexLockInfo? Holder { get; }

    public IndexLockConflictException(string lockPath, IndexLockInfo? holder, Exception inner)
        : base("Another cdidx index is already running on this database.", inner)
    {
        LockPath = lockPath;
        Holder = holder;
    }
}
