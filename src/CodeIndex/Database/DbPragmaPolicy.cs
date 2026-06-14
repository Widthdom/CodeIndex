using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal static class DbPragmaPolicy
{
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
        execute($"PRAGMA cache_size=-{settings.CacheSizeKb}");
        execute("PRAGMA temp_store=MEMORY");
        if (settings.MmapSizeBytes.HasValue)
            execute($"PRAGMA mmap_size={settings.MmapSizeBytes.Value}");
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
        ex.SqliteErrorCode == 1 &&
        ex.Message.Contains("Safety level may not be changed inside a transaction", StringComparison.OrdinalIgnoreCase);

    private static int ReadPositiveIntEnvironment(string name, int fallback, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            && parsed <= maximum
                ? parsed
                : fallback;
    }

    private static long ReadNonNegativeLongEnvironment(string name, long fallback, long maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0
            && parsed <= maximum
                ? parsed
                : fallback;
    }
}

internal readonly record struct DbConnectionPragmaSettings(
    int CacheSizeKb,
    long? MmapSizeBytes);
