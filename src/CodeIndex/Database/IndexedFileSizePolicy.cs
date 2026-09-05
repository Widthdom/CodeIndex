using System.Globalization;
using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Database;

internal static class IndexedFileSizePolicy
{
    internal const string MetaKey = "indexed_max_file_size_bytes";

    internal static long Resolve(DbReader? reader, long? explicitLimit = null, bool freshness = false)
    {
        if (!freshness && explicitLimit is > 0 and <= int.MaxValue)
            return explicitLimit.Value;

        var environment = CdidxEnvironment.GetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable);
        var hasEnvironment = FileIndexer.TryParseMaxFileSizeBytes(environment, out var environmentLimit);
        var stored = reader?.GetMetaString(MetaKey);
        var hasStored = long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var storedLimit)
            && storedLimit is > 0 and <= int.MaxValue;
        if (!freshness && hasEnvironment)
            return environmentLimit;

        // Old writers did not save the admission policy. Existing file sizes provide a
        // bounded lower bound, without changing schema or requiring a rebuild.
        var largestIndexedFile = reader?.GetLargestIndexedFileSize() ?? 0;
        var fallback = Math.Max(FileIndexer.DefaultMaxFileSizeBytes, largestIndexedFile);
        var limit = hasStored ? Math.Max(storedLimit, largestIndexedFile) : fallback;
        return freshness
            ? Math.Max(Math.Max(limit, largestIndexedFile), hasEnvironment ? environmentLimit : 0)
            : limit;
    }
}

public partial class DbReader
{
    internal long GetLargestIndexedFileSize()
    {
        if (!_fileColumns.Contains("size"))
            return 0;
        using var command = _conn.CreateCommand();
        var size = GetFileColumnSql("size");
        command.CommandText = $"SELECT MAX({size}) FROM files f WHERE {size} BETWEEN 0 AND {int.MaxValue}";
        var value = command.ExecuteScalar();
        return value is long sizeBytes ? sizeBytes : 0;
    }
}

public partial class DbWriter
{
    internal void StampIndexedFileSizePolicy(long effectiveLimit, bool scoped)
    {
        if (scoped && long.TryParse(GetMetaString(IndexedFileSizePolicy.MetaKey),
                NumberStyles.None, CultureInfo.InvariantCulture, out var prior)
            && prior is > 0 and <= int.MaxValue)
            effectiveLimit = Math.Max(effectiveLimit, prior);

        using var command = _conn.CreateCommand();
        command.CommandText = $"SELECT MAX(size) FROM files WHERE size BETWEEN 0 AND {int.MaxValue}";
        if (command.ExecuteScalar() is long retainedSize)
            effectiveLimit = Math.Max(effectiveLimit, retainedSize);

        SetMeta(IndexedFileSizePolicy.MetaKey, effectiveLimit.ToString(CultureInfo.InvariantCulture));
    }
}
