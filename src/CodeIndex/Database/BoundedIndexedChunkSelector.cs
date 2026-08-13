using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal sealed class BoundedIndexedChunkSelector
{
    private const int LegacyResourceReadProgressOperations = 100;

    private static readonly string LegacyPredecessorSql = $"""
        SELECT c.id, c.start_line, c.end_line, c.chunk_index
        FROM chunks c INDEXED BY {DbReader.LegacyBoundedResourceReadFileIndexName}
        WHERE c.file_id = @fileId
          AND c.content IS NOT NULL
          AND c.start_line <= @startLine
        ORDER BY c.start_line DESC, c.chunk_index DESC
        LIMIT @chunkLimit
        """;

    private static readonly string LegacyEndSql = $"""
        SELECT c.id, c.start_line, c.end_line, c.chunk_index
        FROM chunks c INDEXED BY {DbReader.LegacyBoundedResourceReadFileIndexName}
        WHERE c.file_id = @fileId
          AND c.content IS NOT NULL
          AND c.end_line >= @startLine
        ORDER BY c.end_line, c.start_line, c.chunk_index
        LIMIT @chunkLimit
        """;

    private static readonly string LegacyForwardSql = $"""
        SELECT c.id, c.start_line, c.end_line, c.chunk_index
        FROM chunks c INDEXED BY {DbReader.LegacyBoundedResourceReadFileIndexName}
        WHERE c.file_id = @fileId
          AND c.content IS NOT NULL
          AND c.start_line > @startLine
          AND c.start_line <= @endLine
        ORDER BY c.start_line, c.chunk_index
        LIMIT @chunkLimit
        """;

    private readonly SqliteConnection _connection;
    private readonly CancellationToken _cancellation;
    private readonly bool _hasChunksTable;
    private readonly IReadOnlySet<string> _chunkIndexes;
    private readonly int? _legacyVmStepLimitOverride;

    internal BoundedIndexedChunkSelector(
        SqliteConnection connection,
        CancellationToken cancellation,
        bool hasChunksTable,
        IReadOnlySet<string> chunkIndexes,
        int? legacyVmStepLimitOverride)
    {
        _connection = connection;
        _cancellation = cancellation;
        _hasChunksTable = hasChunksTable;
        _chunkIndexes = chunkIndexes;
        _legacyVmStepLimitOverride = legacyVmStepLimitOverride;
    }

    internal IReadOnlyList<BoundedIndexedChunk>? Select(
        BoundedIndexedContentContext context,
        out BoundedIndexedContentFailure? failure)
    {
        failure = ValidateFileMetadata(context);
        if (failure != null)
            return null;

        var queryMode = ResolveChunkQueryMode(context.File.Id, out failure);
        if (failure != null)
            return null;
        if (context.NextLine > context.EffectiveEndLine)
        {
            failure = new BoundedIndexedContentFailure(BoundedFileReadStatus.InvalidContinuation);
            return null;
        }

        return SelectChunks(context, queryMode, out failure);
    }

    private BoundedIndexedContentFailure? ValidateFileMetadata(BoundedIndexedContentContext context)
    {
        var file = context.File;
        if (file.Size < 0 || file.Lines < 0 || (file.Size == 0 && file.Lines > 0))
        {
            return new BoundedIndexedContentFailure(
                BoundedFileReadStatus.InvalidTopology,
                "resource_file_metadata_inconsistent");
        }
        if (file.Lines != 0)
            return null;

        if (_hasChunksTable && HasAnyResourceChunk(file.Id))
        {
            return new BoundedIndexedContentFailure(
                BoundedFileReadStatus.InvalidTopology,
                "resource_file_metadata_inconsistent");
        }
        if (DbReader.IsAffirmativelyEmptyIndexedFile(file.Lines, file.Checksum))
            return new BoundedIndexedContentFailure(BoundedFileReadStatus.Empty);
        if (file.Size == 0 && file.Checksum != null)
        {
            return new BoundedIndexedContentFailure(
                BoundedFileReadStatus.InvalidTopology,
                "resource_file_metadata_inconsistent");
        }
        return new BoundedIndexedContentFailure(
            BoundedFileReadStatus.ContentUnavailable,
            "resource_content_unavailable");
    }

    private ChunkQueryMode ResolveChunkQueryMode(
        long fileId,
        out BoundedIndexedContentFailure? failure)
    {
        if (!_hasChunksTable)
        {
            failure = new BoundedIndexedContentFailure(
                BoundedFileReadStatus.ContentUnavailable,
                "resource_content_unavailable");
            return default;
        }

        var hasBoundedIndexes = _chunkIndexes.Contains(DbReader.BoundedResourceReadChunkIndexName)
                                && _chunkIndexes.Contains(DbReader.BoundedResourceReadChunkEndIndexName);
        var mode = hasBoundedIndexes
            ? ChunkQueryMode.Bounded
            : _chunkIndexes.Contains(DbReader.LegacyBoundedResourceReadFileIndexName)
                ? ChunkQueryMode.Legacy
                : ChunkQueryMode.Unavailable;
        if (mode == ChunkQueryMode.Unavailable)
        {
            failure = new BoundedIndexedContentFailure(
                BoundedFileReadStatus.ContentUnavailable,
                "resource_bounded_read_index_unavailable");
            return mode;
        }
        if (!TryHasNullResourceChunkBoundary(fileId, mode, out var hasNullBoundary))
        {
            failure = new BoundedIndexedContentFailure(
                BoundedFileReadStatus.ContentUnavailable,
                "resource_bounded_read_index_unavailable");
            return mode;
        }
        if (hasNullBoundary)
        {
            failure = new BoundedIndexedContentFailure(
                BoundedFileReadStatus.InvalidTopology,
                "resource_chunk_topology_invalid");
            return mode;
        }

        failure = null;
        return mode;
    }

    private IReadOnlyList<BoundedIndexedChunk>? SelectChunks(
        BoundedIndexedContentContext context,
        ChunkQueryMode queryMode,
        out BoundedIndexedContentFailure? failure)
    {
        var scanEndLine = (int)Math.Min(
            context.EffectiveEndLine,
            (long)context.NextLine + context.MaxLines - 1L);
        var candidates = new ChunkCandidateSet();

        failure = ReadCoveringCandidates(context, scanEndLine, queryMode, candidates);
        if (failure != null)
            return null;

        ReadForwardCandidates(context, scanEndLine, queryMode, candidates);
        return FinalizeChunkSelection(context, queryMode, candidates, out failure);
    }

    private BoundedIndexedContentFailure? ReadCoveringCandidates(
        BoundedIndexedContentContext context,
        int scanEndLine,
        ChunkQueryMode queryMode,
        ChunkCandidateSet candidates)
    {
        var predecessorCount = ReadChunkCandidates(
            context,
            scanEndLine,
            queryMode,
            candidates,
            queryMode == ChunkQueryMode.Legacy ? LegacyPredecessorSql : DbReader.BoundedResourceReadPredecessorSql,
            includeEndLine: false);
        if (candidates.IndexUnavailable)
            return IndexUnavailableFailure();

        if (candidates.TopologyFailure != null
            || candidates.Chunks.Count > DbReader.MaxBoundedFileReadChunks
            || candidates.Covers(context.NextLine)
            || predecessorCount <= DbReader.MaxBoundedFileReadChunks)
        {
            return null;
        }

        var endCount = ReadChunkCandidates(
            context,
            scanEndLine,
            queryMode,
            candidates,
            queryMode == ChunkQueryMode.Legacy ? LegacyEndSql : DbReader.BoundedResourceReadEndSql,
            includeEndLine: false);
        if (candidates.IndexUnavailable)
            return IndexUnavailableFailure();
        if (candidates.TopologyFailure == null
            && !candidates.Covers(context.NextLine)
            && endCount > DbReader.MaxBoundedFileReadChunks)
        {
            return new BoundedIndexedContentFailure(
                BoundedFileReadStatus.InvalidTopology,
                "chunk_candidate_scan_limit_exceeded");
        }
        return null;
    }

    private void ReadForwardCandidates(
        BoundedIndexedContentContext context,
        int scanEndLine,
        ChunkQueryMode queryMode,
        ChunkCandidateSet candidates)
    {
        if (candidates.TopologyFailure != null
            || candidates.Chunks.Count > DbReader.MaxBoundedFileReadChunks)
        {
            return;
        }

        ReadChunkCandidates(
            context,
            scanEndLine,
            queryMode,
            candidates,
            queryMode == ChunkQueryMode.Legacy ? LegacyForwardSql : DbReader.BoundedResourceReadForwardSql,
            includeEndLine: true);
    }

    private IReadOnlyList<BoundedIndexedChunk>? FinalizeChunkSelection(
        BoundedIndexedContentContext context,
        ChunkQueryMode queryMode,
        ChunkCandidateSet candidates,
        out BoundedIndexedContentFailure? failure)
    {
        if (candidates.IndexUnavailable)
            return Fail(IndexUnavailableFailure(), out failure);
        if (candidates.TopologyFailure != null)
        {
            return Fail(
                new BoundedIndexedContentFailure(
                    BoundedFileReadStatus.InvalidTopology,
                    candidates.TopologyFailure),
                out failure);
        }
        if (candidates.Chunks.Count > DbReader.MaxBoundedFileReadChunks)
        {
            return Fail(
                new BoundedIndexedContentFailure(BoundedFileReadStatus.InvalidTopology, "chunk_limit_exceeded"),
                out failure);
        }

        candidates.Chunks.Sort(static (left, right) =>
        {
            var byStartLine = left.StartLine.CompareTo(right.StartLine);
            return byStartLine != 0 ? byStartLine : left.ChunkIndex.CompareTo(right.ChunkIndex);
        });
        if (candidates.Chunks.Count != 0)
        {
            failure = null;
            return candidates.Chunks;
        }

        if (!TryHasAnyStoredResourceChunk(context.File.Id, queryMode, out var hasStoredChunk))
            return Fail(IndexUnavailableFailure(), out failure);
        var emptyFailure = hasStoredChunk
            ? new BoundedIndexedContentFailure(
                BoundedFileReadStatus.IncompleteCoverage,
                "resource_chunk_coverage_incomplete")
            : new BoundedIndexedContentFailure(
                BoundedFileReadStatus.ContentUnavailable,
                "resource_content_unavailable");
        return Fail(emptyFailure, out failure);
    }

    private static IReadOnlyList<BoundedIndexedChunk>? Fail(
        BoundedIndexedContentFailure value,
        out BoundedIndexedContentFailure? failure)
    {
        failure = value;
        return null;
    }

    private static BoundedIndexedContentFailure IndexUnavailableFailure()
        => new(
            BoundedFileReadStatus.ContentUnavailable,
            "resource_bounded_read_index_unavailable");

    private int ReadChunkCandidates(
        BoundedIndexedContentContext context,
        int scanEndLine,
        ChunkQueryMode queryMode,
        ChunkCandidateSet candidates,
        string commandText,
        bool includeEndLine)
    {
        int ExecuteQuery()
            => ExecuteChunkCandidateQuery(
                context,
                scanEndLine,
                candidates,
                commandText,
                includeEndLine);

        if (queryMode != ChunkQueryMode.Legacy)
            return ExecuteQuery();
        if (TryRunLegacyResourceMetadataQuery(ExecuteQuery, out var candidateCount))
            return candidateCount;

        candidates.IndexUnavailable = true;
        return 0;
    }

    private int ExecuteChunkCandidateQuery(
        BoundedIndexedContentContext context,
        int scanEndLine,
        ChunkCandidateSet candidates,
        string commandText,
        bool includeEndLine)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = commandText;
        SqliteCommandPolicy.AddInt64(command, "@fileId", context.File.Id);
        SqliteCommandPolicy.AddInt32(command, "@startLine", context.NextLine);
        if (includeEndLine)
            SqliteCommandPolicy.AddInt32(command, "@endLine", scanEndLine);
        SqliteCommandPolicy.AddInt32(command, "@chunkLimit", DbReader.MaxBoundedFileReadChunks + 1);

        var rawCandidateCount = 0;
        using var reader = command.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            rawCandidateCount++;
            _cancellation.ThrowIfCancellationRequested();
            if (!TryReadChunkCandidate(reader, candidates, out var chunk))
                break;
            if (chunk.EndLine < context.NextLine || chunk.StartLine > scanEndLine)
                continue;

            if (candidates.RowIds.Add(chunk.RowId))
                candidates.Chunks.Add(chunk);
            if (candidates.Chunks.Count > DbReader.MaxBoundedFileReadChunks)
                break;
        }
        return rawCandidateCount;
    }

    private static bool TryReadChunkCandidate(
        SqliteDataReader reader,
        ChunkCandidateSet candidates,
        out BoundedIndexedChunk chunk)
    {
        if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
        {
            candidates.TopologyFailure = "resource_chunk_topology_invalid";
            chunk = default;
            return false;
        }

        chunk = new BoundedIndexedChunk(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
        if (chunk.StartLine > 0 && chunk.EndLine >= chunk.StartLine && chunk.ChunkIndex >= 0)
            return true;

        candidates.TopologyFailure = "resource_chunk_topology_invalid";
        return false;
    }

    private bool HasAnyResourceChunk(long fileId)
    {
        _cancellation.ThrowIfCancellationRequested();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM chunks WHERE file_id = @fileId LIMIT 1";
        SqliteCommandPolicy.AddInt64(command, "@fileId", fileId);
        var hasChunk = command.ExecuteScalar() != null;
        _cancellation.ThrowIfCancellationRequested();
        return hasChunk;
    }

    private bool TryHasAnyStoredResourceChunk(
        long fileId,
        ChunkQueryMode queryMode,
        out bool hasChunk)
    {
        bool ExecuteQuery()
        {
            _cancellation.ThrowIfCancellationRequested();
            using var command = _connection.CreateCommand();
            var indexName = queryMode == ChunkQueryMode.Legacy
                ? DbReader.LegacyBoundedResourceReadFileIndexName
                : DbReader.BoundedResourceReadChunkIndexName;
            command.CommandText = $"""
                SELECT 1
                FROM chunks INDEXED BY {indexName}
                WHERE file_id = @fileId AND content IS NOT NULL
                LIMIT 1
                """;
            SqliteCommandPolicy.AddInt64(command, "@fileId", fileId);
            var result = command.ExecuteScalar() != null;
            _cancellation.ThrowIfCancellationRequested();
            return result;
        }

        if (queryMode != ChunkQueryMode.Legacy)
        {
            hasChunk = ExecuteQuery();
            return true;
        }
        return TryRunLegacyResourceMetadataQuery(ExecuteQuery, out hasChunk);
    }

    private bool TryHasNullResourceChunkBoundary(
        long fileId,
        ChunkQueryMode queryMode,
        out bool hasNullBoundary)
    {
        bool ExecuteQuery()
        {
            var startIndexName = queryMode == ChunkQueryMode.Legacy
                ? DbReader.LegacyBoundedResourceReadFileIndexName
                : DbReader.BoundedResourceReadChunkIndexName;
            var endIndexName = queryMode == ChunkQueryMode.Legacy
                ? DbReader.LegacyBoundedResourceReadFileIndexName
                : DbReader.BoundedResourceReadChunkEndIndexName;
            _cancellation.ThrowIfCancellationRequested();
            var result = ResourceChunkExists($"""
                SELECT 1
                FROM chunks INDEXED BY {startIndexName}
                WHERE file_id = @fileId
                  AND content IS NOT NULL
                  AND start_line IS NULL
                LIMIT 1
                """, fileId) || ResourceChunkExists($"""
                SELECT 1
                FROM chunks INDEXED BY {endIndexName}
                WHERE file_id = @fileId
                  AND content IS NOT NULL
                  AND end_line IS NULL
                LIMIT 1
                """, fileId);
            _cancellation.ThrowIfCancellationRequested();
            return result;
        }

        if (queryMode != ChunkQueryMode.Legacy)
        {
            hasNullBoundary = ExecuteQuery();
            return true;
        }
        return TryRunLegacyResourceMetadataQuery(ExecuteQuery, out hasNullBoundary);
    }

    private bool ResourceChunkExists(string commandText, long fileId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = commandText;
        SqliteCommandPolicy.AddInt64(command, "@fileId", fileId);
        return command.ExecuteScalar() != null;
    }

    private bool TryRunLegacyResourceMetadataQuery<T>(Func<T> action, out T result)
    {
        var maxVmSteps = _legacyVmStepLimitOverride ?? DbReader.MaxLegacyResourceReadSqliteVmSteps;
        maxVmSteps = Math.Clamp(maxVmSteps, 1, DbReader.MaxLegacyResourceReadSqliteVmSteps);
        var callbackLimit = Math.Max(1, (maxVmSteps + LegacyResourceReadProgressOperations - 1)
                                        / LegacyResourceReadProgressOperations);
        var callbackCount = 0;
        var budgetExceeded = false;
        SQLitePCL.delegate_progress progress = _ =>
        {
            callbackCount++;
            if (callbackCount <= callbackLimit)
                return 0;

            budgetExceeded = true;
            return 1;
        };

        SQLitePCL.raw.sqlite3_progress_handler(
            _connection.Handle,
            LegacyResourceReadProgressOperations,
            progress,
            null!);
        try
        {
            result = action();
            return true;
        }
        catch (SqliteException exception) when (
            budgetExceeded
            && !_cancellation.IsCancellationRequested
            && exception.SqliteErrorCode == 9)
        {
            result = default!;
            return false;
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(_connection.Handle, 0, null!, null!);
            GC.KeepAlive(progress);
        }
    }

    private enum ChunkQueryMode
    {
        Unavailable,
        Bounded,
        Legacy,
    }

    private sealed class ChunkCandidateSet
    {
        internal List<BoundedIndexedChunk> Chunks { get; } = [];
        internal HashSet<long> RowIds { get; } = [];
        internal string? TopologyFailure { get; set; }
        internal bool IndexUnavailable { get; set; }

        internal bool Covers(int line)
            => Chunks.Any(chunk => chunk.StartLine <= line && chunk.EndLine >= line);
    }
}
