using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal readonly record struct ResourceFileEntry(long Id, string Path, string? Lang, int Lines);

internal sealed record ResourceFilePage(
    long Generation,
    IReadOnlyList<ResourceFileEntry> Files,
    bool CursorRestartRequired,
    bool GenerationTrackingUnavailable = false);

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
        var generation = TryReadResourceListGeneration(transaction);
        if (generation is null)
        {
            transaction.Commit();
            return new ResourceFilePage(
                Generation: 0,
                Files: [],
                CursorRestartRequired: false,
                GenerationTrackingUnavailable: true);
        }
        var trustedGeneration = generation.Value;
        if (expectedGeneration is not null && expectedGeneration.Value != trustedGeneration)
        {
            transaction.Commit();
            return new ResourceFilePage(trustedGeneration, [], CursorRestartRequired: true);
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
                return new ResourceFilePage(trustedGeneration, [], CursorRestartRequired: true);
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
        return new ResourceFilePage(trustedGeneration, files, CursorRestartRequired: false);
    }

    private long? TryReadResourceListGeneration(SqliteTransaction transaction)
    {
        using var schemaCommand = _conn.CreateCommand();
        schemaCommand.Transaction = transaction;
        schemaCommand.CommandText = """
            SELECT
                EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'codeindex_meta'),
                (SELECT COUNT(*) FROM sqlite_master
                 WHERE type = 'trigger'
                   AND name IN ('files_resource_generation_ai', 'files_resource_generation_ad', 'files_resource_generation_au'))
            """;
        using var schemaReader = schemaCommand.ExecuteTrackedReader();
        if (!schemaReader.TrackedRead())
            return _immutableReadOnly ? 0 : null;

        var hasMetaTable = schemaReader.GetInt64(0) != 0;
        var hasAllGenerationTriggers = schemaReader.GetInt64(1) == DbContext.ResourceListGenerationTriggerNames.Length;
        if (!hasMetaTable)
            return _immutableReadOnly ? 0 : null;

        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
        SqliteCommandPolicy.Add(command, "@key", DbContext.ResourceListGenerationMetaKey);
        var raw = command.ExecuteScalar() as string;
        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            && generation >= 0
            && (hasAllGenerationTriggers || _immutableReadOnly))
        {
            return generation;
        }

        // An immutable snapshot cannot change between pages, so legacy databases may use a
        // connection-local generation zero even when the persisted tracking schema is absent.
        // immutable snapshot はページ間で変化しないため、永続世代 schema がない legacy DB でも
        // connection-local な世代 0 を安全に利用できる。
        return _immutableReadOnly ? 0 : null;
    }
}
