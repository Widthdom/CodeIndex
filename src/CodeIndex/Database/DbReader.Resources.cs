using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal readonly record struct ResourceFileEntry(long Id, string Path, string? Lang, int Lines);

internal sealed record ResourceFilePage(
    long Generation,
    IReadOnlyList<ResourceFileEntry> Files,
    bool CursorRestartRequired);

public partial class DbReader
{
    /// <summary>
    /// Read one resources/list page from a single SQLite snapshot. Opaque cursors resolve
    /// their bounded file-id anchor back to the existing bucket/path sort key before seeking.
    /// 単一 SQLite スナップショットから resources/list の 1 ページを読み込む。不透明カーソルの
    /// 有界な file-id anchor を既存の bucket/path sort key に解決してから keyset seek する。
    /// </summary>
    internal ResourceFilePage ListResourceFiles(
        int limit,
        long? afterFileId = null,
        long? expectedGeneration = null,
        int legacyOffset = 0)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be positive.");
        if (legacyOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(legacyOffset), legacyOffset, "Offset must be non-negative.");
        if (afterFileId is not null && legacyOffset != 0)
            throw new ArgumentException("A keyset anchor and legacy offset cannot be combined.", nameof(legacyOffset));

        using var transaction = _conn.BeginTransaction(deferred: true);
        var generation = ReadResourceListGeneration(transaction);
        if (expectedGeneration is not null && expectedGeneration.Value != generation)
        {
            transaction.Commit();
            return new ResourceFilePage(generation, [], CursorRestartRequired: true);
        }

        int? afterBucket = null;
        string? afterPath = null;
        if (afterFileId is not null)
        {
            using (var anchorCommand = _conn.CreateCommand())
            {
                anchorCommand.Transaction = transaction;
                anchorCommand.CommandText = $"""
                    SELECT {PathBucketOrder} AS path_bucket, f.path
                    FROM files f
                    WHERE f.id = @afterFileId
                    """;
                SqliteCommandPolicy.Add(anchorCommand, "@afterFileId", afterFileId.Value);
                using var anchorReader = anchorCommand.ExecuteTrackedReader();
                if (anchorReader.TrackedRead())
                {
                    afterBucket = anchorReader.GetInt32(0);
                    afterPath = anchorReader.GetString(1);
                }
            }

            if (afterPath is null)
            {
                transaction.Commit();
                return new ResourceFilePage(generation, [], CursorRestartRequired: true);
            }
        }

        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        var seekPredicate = afterFileId is null
            ? string.Empty
            : "WHERE path_bucket > @afterBucket OR (path_bucket = @afterBucket AND path > @afterPath)";
        var offsetClause = afterFileId is null && legacyOffset > 0 ? "OFFSET @legacyOffset" : string.Empty;
        command.CommandText = $"""
            WITH resource_files AS (
                SELECT f.id, f.path, f.lang, f.lines, {PathBucketOrder} AS path_bucket
                FROM files f
            )
            SELECT id, path, lang, lines
            FROM resource_files
            {seekPredicate}
            ORDER BY path_bucket, path
            LIMIT @limit {offsetClause}
            """;
        SqliteCommandPolicy.Add(command, "@limit", limit);
        if (afterFileId is not null)
        {
            SqliteCommandPolicy.Add(command, "@afterBucket", afterBucket!.Value);
            SqliteCommandPolicy.Add(command, "@afterPath", afterPath!);
        }
        else if (legacyOffset > 0)
        {
            SqliteCommandPolicy.Add(command, "@legacyOffset", legacyOffset);
        }

        var files = new List<ResourceFileEntry>(limit);
        using (var reader = command.ExecuteTrackedReader())
        {
            while (reader.TrackedRead())
            {
                files.Add(new ResourceFileEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3)));
            }
        }

        transaction.Commit();
        return new ResourceFilePage(generation, files, CursorRestartRequired: false);
    }

    private long ReadResourceListGeneration(SqliteTransaction transaction)
    {
        try
        {
            using var command = _conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
            SqliteCommandPolicy.Add(command, "@key", DbContext.ResourceListGenerationMetaKey);
            var raw = command.ExecuteScalar() as string;
            return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
                && generation >= 0
                    ? generation
                    : 0;
        }
        catch (SqliteException)
        {
            // Legacy immutable DBs may not have codeindex_meta or generation triggers yet.
            // legacy immutable DB では codeindex_meta / generation trigger が未導入の場合がある。
            return 0;
        }
    }
}
