using System.Globalization;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal static class IndexedFileSizePolicy
{
    internal const string MetaKey = "indexed_max_file_size_bytes";

    internal static long Resolve(DbReader? reader, long? explicitLimit = null, bool freshness = false)
        => ResolveStored(reader?.GetMetaString(MetaKey), reader?.GetLargestIndexedFileSize() ?? 0, explicitLimit, freshness);

    internal static long ResolveForIndex(DbContext db, long? explicitLimit)
        => ResolveStored(db.GetMetaString(MetaKey), ReadLargestFileSize(db.Connection, hasSizeColumn: true), explicitLimit);

    internal static long ResolveStored(string? stored, long largestIndexedFile, long? explicitLimit = null, bool freshness = false)
    {
        if (!freshness && explicitLimit is > 0 and <= int.MaxValue)
            return explicitLimit.Value;

        var environment = CdidxEnvironment.GetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable);
        var hasEnvironment = FileIndexer.TryParseMaxFileSizeBytes(environment, out var environmentLimit);
        var hasStored = long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var storedLimit)
            && storedLimit is > 0 and <= int.MaxValue;
        if (!freshness && hasEnvironment)
            return environmentLimit;
        if (!freshness && !string.IsNullOrWhiteSpace(environment))
            return FileIndexer.DefaultMaxFileSizeBytes;

        // Old writers did not save the admission policy. Existing file sizes provide a
        // bounded lower bound, without changing schema or requiring a rebuild.
        var fallback = Math.Max(FileIndexer.DefaultMaxFileSizeBytes, largestIndexedFile);
        var limit = hasStored ? Math.Max(storedLimit, largestIndexedFile) : fallback;
        return freshness
            ? Math.Max(Math.Max(limit, largestIndexedFile), hasEnvironment ? environmentLimit : 0)
            : limit;
    }

    internal static long ReadLargestFileSize(SqliteConnection connection, bool hasSizeColumn)
    {
        if (!hasSizeColumn)
            return 0;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MAX(size) FROM files WHERE size BETWEEN 0 AND {int.MaxValue}";
        return command.ExecuteScalar() is long size ? size : 0;
    }
}

public partial class DbReader
{
    internal long GetLargestIndexedFileSize()
        => IndexedFileSizePolicy.ReadLargestFileSize(_conn, _fileColumns.Contains("size"));
}

public partial class DbWriter
{
    internal void StampIndexedFileSizePolicy(long effectiveLimit, bool scoped)
    {
        if (scoped && long.TryParse(GetMetaString(IndexedFileSizePolicy.MetaKey),
                NumberStyles.None, CultureInfo.InvariantCulture, out var prior)
            && prior is > 0 and <= int.MaxValue)
            effectiveLimit = Math.Max(effectiveLimit, prior);

        effectiveLimit = Math.Max(effectiveLimit, IndexedFileSizePolicy.ReadLargestFileSize(_conn, hasSizeColumn: true));

        SetMeta(IndexedFileSizePolicy.MetaKey, effectiveLimit.ToString(CultureInfo.InvariantCulture));
    }
}
