using System.Buffers;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal sealed record BoundedUtf8DecodeResult(
    StringBuilder Content,
    BoundedIndexedContentFailure? Failure,
    int NextLine,
    int NextByteOffset,
    int Utf8Bytes,
    int? FirstReturnedLine,
    int? LastReturnedLine,
    string? TruncationReason);

internal sealed class BoundedUtf8ContentDecoder
{
    private const int ReadBufferSize = 4 * 1024;

    private readonly SqliteConnection _connection;
    private readonly CancellationToken _cancellation;
    private readonly int? _scanByteLimitOverride;

    internal BoundedUtf8ContentDecoder(
        SqliteConnection connection,
        CancellationToken cancellation,
        int? scanByteLimitOverride)
    {
        _connection = connection;
        _cancellation = cancellation;
        _scanByteLimitOverride = scanByteLimitOverride;
    }

    internal BoundedUtf8DecodeResult Decode(
        BoundedIndexedContentContext context,
        IReadOnlyList<BoundedIndexedChunk> chunks)
    {
        var state = new ReadState(context.NextLine, context.ContinuationByteOffset);
        var scanByteLimit = _scanByteLimitOverride ?? DbReader.MaxBoundedFileReadScannedUtf8Bytes;
        if (scanByteLimit <= 0 || scanByteLimit > DbReader.MaxBoundedFileReadScannedUtf8Bytes)
            scanByteLimit = DbReader.MaxBoundedFileReadScannedUtf8Bytes;
        var settings = new ChunkDecodeSettings(
            context.EffectiveEndLine,
            context.MaxUtf8Bytes,
            context.MaxLines,
            scanByteLimit,
            _cancellation);
        var content = new StringBuilder(Math.Min(context.MaxUtf8Bytes, ReadBufferSize));
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            DecodeChunks(chunks, settings, content, buffer, ref state);
        }
        catch (BoundedFileScanLimitException)
        {
            state.ScanLimitExceeded = true;
        }
        catch (InvalidDataException)
        {
            state.InvalidTopologyReason = "invalid_utf8_content";
        }
        finally
        {
            // Indexed source may contain secrets. Clear the entire pooled array before reuse.
            // index済みsourceにsecretが含まれうるため、poolへ戻す前に配列全体を消去する。
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        return CreateResult(content, state);
    }

    private void DecodeChunks(
        IReadOnlyList<BoundedIndexedChunk> chunks,
        ChunkDecodeSettings settings,
        StringBuilder content,
        byte[] buffer,
        ref ReadState state)
    {
        foreach (var chunk in chunks)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (state.Stopped)
                break;
            if (chunk.EndLine < state.NextLine)
                continue;
            if (chunk.StartLine > state.NextLine)
            {
                state.IncompleteCoverage = true;
                break;
            }

            using var blob = new SqliteBlob(_connection, "chunks", "content", chunk.RowId, readOnly: true);
            ReadBoundedChunk(blob, chunk, settings, content, buffer, ref state);
        }
    }

    private static BoundedUtf8DecodeResult CreateResult(StringBuilder content, ReadState state)
    {
        BoundedIndexedContentFailure? failure = null;
        if (state.ScanLimitExceeded)
        {
            failure = new BoundedIndexedContentFailure(
                BoundedFileReadStatus.InvalidTopology,
                "scan_limit_exceeded");
        }
        else if (state.InvalidTopologyReason != null)
        {
            failure = new BoundedIndexedContentFailure(
                BoundedFileReadStatus.InvalidTopology,
                state.InvalidTopologyReason);
        }
        else if (state.InvalidContinuation)
        {
            failure = new BoundedIndexedContentFailure(
                BoundedFileReadStatus.InvalidContinuation,
                "invalid_continuation");
        }
        else if (state.IncompleteCoverage || (!state.Completed && state.TruncationReason == null))
        {
            failure = new BoundedIndexedContentFailure(
                BoundedFileReadStatus.IncompleteCoverage,
                "resource_chunk_coverage_incomplete");
        }

        return new BoundedUtf8DecodeResult(
            content,
            failure,
            state.NextLine,
            state.NextByteOffset,
            state.Utf8Bytes,
            state.FirstReturnedLine,
            state.LastReturnedLine,
            state.TruncationReason);
    }

    private static void ReadBoundedChunk(
        SqliteBlob blob,
        BoundedIndexedChunk chunk,
        ChunkDecodeSettings settings,
        StringBuilder content,
        byte[] buffer,
        ref ReadState state)
    {
        var cursor = new ChunkReadCursor(chunk.StartLine);
        Span<byte> runeBytes = stackalloc byte[4];
        Span<char> runeChars = stackalloc char[2];
        while (TryReadBlobByte(
                   blob,
                   buffer,
                   settings.MaxScannedUtf8Bytes,
                   settings.CancellationToken,
                   ref cursor,
                   ref state,
                   out var firstByte))
        {
            if (!ProcessChunkByte(
                    blob,
                    chunk,
                    firstByte,
                    settings,
                    content,
                    buffer,
                    runeBytes,
                    runeChars,
                    ref cursor,
                    ref state))
            {
                return;
            }
        }

        CompleteChunkFinalLine(chunk, settings, content, cursor, ref state);
    }

    private static bool ProcessChunkByte(
        SqliteBlob blob,
        BoundedIndexedChunk chunk,
        byte firstByte,
        ChunkDecodeSettings settings,
        StringBuilder content,
        byte[] buffer,
        Span<byte> runeBytes,
        Span<char> runeChars,
        ref ChunkReadCursor cursor,
        ref ReadState state)
    {
        if (cursor.LocalLine > chunk.EndLine)
            return false;
        if (cursor.LocalLine < state.NextLine)
        {
            AdvanceSkippedByte(firstByte, ref cursor);
            return true;
        }
        if (cursor.LocalLine > state.NextLine)
        {
            state.InvalidContinuation = true;
            return false;
        }
        if (firstByte == (byte)'\n')
            return ProcessLineBreak(settings, content, ref cursor, ref state);
        return ProcessRune(
            blob,
            firstByte,
            settings,
            content,
            buffer,
            runeBytes,
            runeChars,
            ref cursor,
            ref state);
    }

    private static void AdvanceSkippedByte(byte value, ref ChunkReadCursor cursor)
    {
        if (value == (byte)'\n')
        {
            cursor.LocalLine++;
            cursor.LocalByteOffset = 0;
        }
        else
        {
            cursor.LocalByteOffset++;
        }
    }

    private static bool ProcessLineBreak(
        ChunkDecodeSettings settings,
        StringBuilder content,
        ref ChunkReadCursor cursor,
        ref ReadState state)
    {
        if (state.NextByteOffset != cursor.LocalByteOffset)
        {
            state.InvalidContinuation = true;
            return false;
        }
        if (!CompleteBoundedLine(
                cursor.LocalLine,
                cursor.LocalByteOffset,
                settings,
                content,
                ref state))
        {
            return false;
        }

        cursor.LocalLine++;
        cursor.LocalByteOffset = 0;
        return true;
    }

    private static bool ProcessRune(
        SqliteBlob blob,
        byte firstByte,
        ChunkDecodeSettings settings,
        StringBuilder content,
        byte[] buffer,
        Span<byte> runeBytes,
        Span<char> runeChars,
        ref ChunkReadCursor cursor,
        ref ReadState state)
    {
        var runeByteCount = ReadUtf8Rune(
            blob,
            firstByte,
            buffer,
            settings.MaxScannedUtf8Bytes,
            settings.CancellationToken,
            runeBytes,
            ref cursor,
            ref state,
            out var rune);
        var scalarStartOffset = cursor.LocalByteOffset;
        cursor.LocalByteOffset += runeByteCount;
        if (scalarStartOffset < state.NextByteOffset)
        {
            if (cursor.LocalByteOffset > state.NextByteOffset)
                state.InvalidContinuation = true;
            return !state.InvalidContinuation;
        }
        if (scalarStartOffset != state.NextByteOffset)
        {
            state.InvalidContinuation = true;
            return false;
        }
        if (state.Utf8Bytes > settings.MaxUtf8Bytes - runeByteCount)
        {
            state.TruncationReason = "max_bytes";
            return false;
        }
        if (!TryEnterBoundedLine(cursor.LocalLine, settings.MaxLines, ref state))
            return false;

        var runeCharCount = rune.EncodeToUtf16(runeChars);
        content.Append(runeChars[..runeCharCount]);
        state.Utf8Bytes += runeByteCount;
        state.NextByteOffset = cursor.LocalByteOffset;
        return true;
    }

    private static void CompleteChunkFinalLine(
        BoundedIndexedChunk chunk,
        ChunkDecodeSettings settings,
        StringBuilder content,
        ChunkReadCursor cursor,
        ref ReadState state)
    {
        // Persisted chunks omit the separator after their final line. Treat blob EOF as
        // that line boundary and synthesize exactly one LF when the requested range continues.
        // 永続化chunkは最終行後のseparatorを持たないため、blob EOFを行境界として扱い、
        // 要求範囲が続く場合だけLFを1つ合成する。
        if (state.Stopped || cursor.LocalLine > chunk.EndLine || cursor.LocalLine != state.NextLine)
            return;
        if (state.NextByteOffset != cursor.LocalByteOffset)
        {
            state.InvalidContinuation = true;
            return;
        }

        _ = CompleteBoundedLine(
            cursor.LocalLine,
            cursor.LocalByteOffset,
            settings,
            content,
            ref state);
    }

    private static bool CompleteBoundedLine(
        int line,
        int lineByteLength,
        ChunkDecodeSettings settings,
        StringBuilder content,
        ref ReadState state)
    {
        if (!TryEnterBoundedLine(line, settings.MaxLines, ref state))
            return false;
        if (line >= settings.EffectiveEndLine)
        {
            state.Completed = true;
            return false;
        }
        if (state.Utf8Bytes >= settings.MaxUtf8Bytes)
        {
            state.NextLine = line;
            state.NextByteOffset = lineByteLength;
            state.TruncationReason = "max_bytes";
            return false;
        }

        content.Append('\n');
        state.Utf8Bytes++;
        state.NextLine = line + 1;
        state.NextByteOffset = 0;
        if (state.ReturnedLineCount >= settings.MaxLines)
        {
            state.TruncationReason = "max_lines";
            return false;
        }
        return true;
    }

    private static bool TryEnterBoundedLine(
        int line,
        int maxLines,
        ref ReadState state)
    {
        if (state.LastReturnedLine == line)
            return true;
        if (state.ReturnedLineCount >= maxLines)
        {
            state.TruncationReason = "max_lines";
            return false;
        }

        state.FirstReturnedLine ??= line;
        state.LastReturnedLine = line;
        state.ReturnedLineCount++;
        return true;
    }

    private static int ReadUtf8Rune(
        SqliteBlob blob,
        byte firstByte,
        byte[] buffer,
        int maxScannedUtf8Bytes,
        CancellationToken cancellationToken,
        Span<byte> runeBytes,
        ref ChunkReadCursor cursor,
        ref ReadState state,
        out Rune rune)
    {
        var byteCount = firstByte switch
        {
            <= 0x7f => 1,
            >= 0xc2 and <= 0xdf => 2,
            >= 0xe0 and <= 0xef => 3,
            >= 0xf0 and <= 0xf4 => 4,
            _ => throw new InvalidDataException("Indexed chunk contains invalid UTF-8."),
        };
        runeBytes[0] = firstByte;
        for (var index = 1; index < byteCount; index++)
        {
            if (!TryReadBlobByte(
                    blob,
                    buffer,
                    maxScannedUtf8Bytes,
                    cancellationToken,
                    ref cursor,
                    ref state,
                    out runeBytes[index]))
            {
                throw new InvalidDataException("Indexed chunk ends inside a UTF-8 scalar value.");
            }
        }

        var status = Rune.DecodeFromUtf8(runeBytes[..byteCount], out rune, out var bytesConsumed);
        if (status != OperationStatus.Done || bytesConsumed != byteCount)
            throw new InvalidDataException("Indexed chunk contains invalid UTF-8.");
        return byteCount;
    }

    private static bool TryReadBlobByte(
        SqliteBlob blob,
        byte[] buffer,
        int maxScannedUtf8Bytes,
        CancellationToken cancellationToken,
        ref ChunkReadCursor cursor,
        ref ReadState state,
        out byte value)
    {
        if (cursor.BufferOffset >= cursor.BufferedBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blob.Position >= blob.Length)
            {
                value = 0;
                return false;
            }
            var remaining = maxScannedUtf8Bytes - state.ScannedUtf8Bytes;
            if (remaining <= 0)
                throw new BoundedFileScanLimitException();
            cursor.BufferedBytes = blob.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            cursor.BufferOffset = 0;
            if (cursor.BufferedBytes == 0)
            {
                value = 0;
                return false;
            }
        }

        value = buffer[cursor.BufferOffset++];
        state.ScannedUtf8Bytes++;
        return true;
    }

    private readonly record struct ChunkDecodeSettings(
        int EffectiveEndLine,
        int MaxUtf8Bytes,
        int MaxLines,
        int MaxScannedUtf8Bytes,
        CancellationToken CancellationToken);

    private sealed class BoundedFileScanLimitException : Exception;

    private struct ChunkReadCursor(int localLine)
    {
        internal int BufferOffset;
        internal int BufferedBytes;
        internal int LocalLine = localLine;
        internal int LocalByteOffset;
    }

    private struct ReadState(int nextLine, int nextByteOffset)
    {
        internal int NextLine = nextLine;
        internal int NextByteOffset = nextByteOffset;
        internal int Utf8Bytes;
        internal int ScannedUtf8Bytes;
        internal int ReturnedLineCount;
        internal int? FirstReturnedLine;
        internal int? LastReturnedLine;
        internal string? TruncationReason;
        internal string? InvalidTopologyReason;
        internal bool Completed;
        internal bool InvalidContinuation;
        internal bool IncompleteCoverage;
        internal bool ScanLimitExceeded;

        internal readonly bool Stopped
            => Completed
               || InvalidContinuation
               || IncompleteCoverage
               || ScanLimitExceeded
               || TruncationReason != null;
    }
}
