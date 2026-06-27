using CodeIndex.Cli;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal static class DbPragmaPolicy
{
    internal const string TempStoreMemoryPragmaSql = "PRAGMA temp_store=MEMORY";
    internal const string AutoVacuumIncrementalPragmaSql = "PRAGMA auto_vacuum=INCREMENTAL";
    internal const int DefaultBusyTimeoutMs = 5000;
    internal const int MaxBusyTimeoutMs = 3_600_000;

    internal static DbConnectionPragmaSettings ReadConnectionPragmaSettings(
        string cacheSizeEnvironmentVariable,
        int defaultCacheSizeKb,
        int maxCacheSizeKb,
        string mmapSizeEnvironmentVariable,
        long defaultMmapSizeBytes,
        long maxMmapSizeBytes,
        bool is64BitProcess)
    {
        var cacheSizeKb = ReadPositiveIntEnvironment(
            cacheSizeEnvironmentVariable,
            defaultCacheSizeKb,
            maxCacheSizeKb);
        var mmapSizeBytes = is64BitProcess
            ? ReadNonNegativeLongEnvironment(
                mmapSizeEnvironmentVariable,
                defaultMmapSizeBytes,
                maxMmapSizeBytes)
            : (long?)null;
        return new DbConnectionPragmaSettings(cacheSizeKb, mmapSizeBytes);
    }

    internal static void ApplyConnectionPerformancePragmas(
        Action<string> execute,
        DbConnectionPragmaSettings settings)
    {
        execute(CacheSizePragmaSql(settings.CacheSizeKb));
        execute(TempStoreMemoryPragmaSql);
        if (settings.MmapSizeBytes.HasValue)
            execute(MmapSizePragmaSql(settings.MmapSizeBytes.Value));
    }

    internal static void ExecuteSynchronousPragmaWithFallback(
        Action<string> execute,
        string synchronousMode)
    {
        try
        {
            execute($"PRAGMA synchronous={synchronousMode}");
        }
        catch (SqliteException ex) when (IsSafetyLevelTransactionError(ex))
        {
            // SQLite can reject PRAGMA synchronous while another pooled connection is in a
            // transaction. Keep the connection usable; the setting is a durability/perf knob,
            // not a schema precondition for readers.
        }
    }

    internal static bool IsSafetyLevelTransactionError(SqliteException ex) =>
        ex.SqliteErrorCode == 1;

    internal static int ReadBusyTimeoutMs(string environmentVariable)
        => ReadNonNegativeIntEnvironment(
            environmentVariable,
            DefaultBusyTimeoutMs,
            MaxBusyTimeoutMs);

    internal static string ReadBusyTimeoutPragmaSql(string environmentVariable)
        => BusyTimeoutPragmaSql(ReadBusyTimeoutMs(environmentVariable));

    internal static string BusyTimeoutPragmaSql(int busyTimeoutMs)
    {
        if (busyTimeoutMs is < 0 or > MaxBusyTimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutMs), busyTimeoutMs, $"SQLite busy_timeout must be between 0 and {MaxBusyTimeoutMs} milliseconds.");
        return $"PRAGMA busy_timeout={busyTimeoutMs}";
    }

    internal static string CacheSizePragmaSql(int cacheSizeKb)
    {
        if (cacheSizeKb <= 0)
            throw new ArgumentOutOfRangeException(nameof(cacheSizeKb), cacheSizeKb, "SQLite cache_size kilobytes must be positive.");
        return $"PRAGMA cache_size=-{cacheSizeKb}";
    }

    internal static string MmapSizePragmaSql(long mmapSizeBytes)
    {
        if (mmapSizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(mmapSizeBytes), mmapSizeBytes, "SQLite mmap_size bytes must be non-negative.");
        return $"PRAGMA mmap_size={mmapSizeBytes}";
    }

    private static int ReadPositiveIntEnvironment(string name, int fallback, int maximum)
        => EnvironmentOptionParser.ReadInt32(name, fallback, minimum: 1, maximum).Value;

    private static int ReadNonNegativeIntEnvironment(string name, int fallback, int maximum)
        => EnvironmentOptionParser.ReadInt32(name, fallback, minimum: 0, maximum).Value;

    private static long ReadNonNegativeLongEnvironment(string name, long fallback, long maximum)
        => EnvironmentOptionParser.ReadInt64(name, fallback, minimum: 0, maximum).Value;
}

internal readonly record struct DbConnectionPragmaSettings(
    int CacheSizeKb,
    long? MmapSizeBytes);
