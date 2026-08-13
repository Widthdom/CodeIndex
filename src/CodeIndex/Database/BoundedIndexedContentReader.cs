using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal readonly record struct BoundedIndexedContentRequest(
    ResourceFileMetadata File,
    int StartLine,
    int EndLine,
    int MaxUtf8Bytes,
    int MaxLines,
    int? ContinuationLine,
    int ContinuationByteOffset);

internal readonly record struct BoundedIndexedContentFailure(
    BoundedFileReadStatus Status,
    string? Reason = null);

internal sealed record BoundedIndexedContentContext(
    ResourceFileMetadata File,
    int StartLine,
    int EndLine,
    int MaxUtf8Bytes,
    int MaxLines,
    int NextLine,
    int ContinuationByteOffset)
{
    internal int EffectiveEndLine => Math.Min(EndLine, File.Lines);
}

internal readonly record struct BoundedIndexedChunk(
    long RowId,
    int StartLine,
    int EndLine,
    int ChunkIndex);

internal sealed class BoundedIndexedContentReader
{
    private readonly BoundedIndexedChunkSelector _chunkSelector;
    private readonly BoundedUtf8ContentDecoder _decoder;

    internal BoundedIndexedContentReader(
        SqliteConnection connection,
        CancellationToken cancellation,
        bool hasChunksTable,
        IReadOnlySet<string> chunkIndexes,
        int? scanByteLimitOverride,
        int? legacyVmStepLimitOverride)
    {
        _chunkSelector = new BoundedIndexedChunkSelector(
            connection,
            cancellation,
            hasChunksTable,
            chunkIndexes,
            legacyVmStepLimitOverride);
        _decoder = new BoundedUtf8ContentDecoder(
            connection,
            cancellation,
            scanByteLimitOverride);
    }

    internal BoundedFileReadResult Read(BoundedIndexedContentRequest request)
    {
        var context = NormalizeRequest(request, out var earlyResult);
        if (context == null)
            return earlyResult!;

        var chunks = _chunkSelector.Select(context, out var selectionFailure);
        if (selectionFailure is { } failure)
            return CreateResult(context, failure.Status, failure.Reason);

        var decoded = _decoder.Decode(context, chunks!);
        return BuildDecodedResult(context, decoded);
    }

    private static BoundedIndexedContentContext? NormalizeRequest(
        BoundedIndexedContentRequest request,
        out BoundedFileReadResult? earlyResult)
    {
        var path = request.File.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            earlyResult = new BoundedFileReadResult { Status = BoundedFileReadStatus.FileNotFound };
            return null;
        }

        ValidateBudgets(request);
        if (!request.ContinuationLine.HasValue && request.ContinuationByteOffset != 0)
        {
            earlyResult = CreateInvalidContinuationResult(path);
            return null;
        }

        var startLine = Math.Max(1, request.StartLine);
        var endLine = Math.Max(startLine, request.EndLine);
        var nextLine = request.ContinuationLine ?? startLine;
        if (nextLine < startLine || nextLine > endLine)
        {
            earlyResult = CreateInvalidContinuationResult(path);
            return null;
        }

        earlyResult = null;
        return new BoundedIndexedContentContext(
            request.File,
            startLine,
            endLine,
            request.MaxUtf8Bytes,
            request.MaxLines,
            nextLine,
            request.ContinuationByteOffset);
    }

    private static void ValidateBudgets(BoundedIndexedContentRequest request)
    {
        if (request.MaxUtf8Bytes is <= 0 or > DbReader.MaxBoundedFileReadUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                "maxUtf8Bytes",
                request.MaxUtf8Bytes,
                $"UTF-8 byte budget must be between 1 and {DbReader.MaxBoundedFileReadUtf8Bytes}.");
        }
        if (request.MaxLines is <= 0 or > DbReader.MaxBoundedFileReadLines)
        {
            throw new ArgumentOutOfRangeException(
                "maxLines",
                request.MaxLines,
                $"Line budget must be between 1 and {DbReader.MaxBoundedFileReadLines}.");
        }
        if (request.ContinuationByteOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                "continuationByteOffset",
                request.ContinuationByteOffset,
                "Continuation byte offset must be non-negative.");
        }
    }

    private static BoundedFileReadResult CreateInvalidContinuationResult(string path)
        => new()
        {
            Status = BoundedFileReadStatus.InvalidContinuation,
            Path = path,
        };

    private static BoundedFileReadResult BuildDecodedResult(
        BoundedIndexedContentContext context,
        BoundedUtf8DecodeResult decoded)
    {
        if (decoded.Failure is { } failure)
            return CreateResult(context, failure.Status, failure.Reason);

        return new BoundedFileReadResult
        {
            Status = BoundedFileReadStatus.Success,
            Path = context.File.Path,
            Lang = context.File.Lang,
            TotalLines = context.File.Lines,
            RequestedStartLine = context.StartLine,
            RequestedEndLine = context.EndLine,
            StartLine = decoded.FirstReturnedLine ?? context.NextLine,
            EndLine = decoded.LastReturnedLine ?? context.NextLine,
            Content = decoded.Content.ToString(),
            Utf8Bytes = decoded.Utf8Bytes,
            Truncated = decoded.TruncationReason != null,
            TruncationReason = decoded.TruncationReason,
            NextLine = decoded.TruncationReason != null ? decoded.NextLine : null,
            NextByteOffset = decoded.TruncationReason != null ? decoded.NextByteOffset : null,
        };
    }

    private static BoundedFileReadResult CreateResult(
        BoundedIndexedContentContext context,
        BoundedFileReadStatus status,
        string? failureReason = null)
        => new()
        {
            Status = status,
            FailureReason = failureReason,
            Path = context.File.Path,
            Lang = context.File.Lang,
            TotalLines = context.File.Lines,
            RequestedStartLine = context.StartLine,
            RequestedEndLine = context.EndLine,
            StartLine = context.NextLine,
            EndLine = context.NextLine,
        };
}
