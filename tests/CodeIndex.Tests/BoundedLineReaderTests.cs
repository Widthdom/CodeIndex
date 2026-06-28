using System.Text;

namespace CodeIndex.Tests;

public class BoundedLineReaderTests
{
    [Fact]
    public async Task ReadLineAsync_BuffersRemainderAfterChunkRead_Issue3836()
    {
        using var reader = new StringReader("first line\nsecond line\n");

        var first = await BoundedLineReader.ReadLineAsync(reader, 100, 100, CancellationToken.None);
        var second = await BoundedLineReader.ReadLineAsync(reader, 100, 100, CancellationToken.None);
        var end = await BoundedLineReader.ReadLineAsync(reader, 100, 100, CancellationToken.None);

        Assert.Equal("first line", first);
        Assert.Equal("second line", second);
        Assert.Null(end);
    }

    [Fact]
    public void TryReadUtf8File_EnforcesStreamingByteLimit_Issue4114()
    {
        var ok = BoundedLineReader.TryReadUtf8File(
            "input.txt",
            maxBytes: 5,
            maxLines: 10,
            maxLineCharacters: 120,
            out var lines,
            out var failure,
            _ => new NonSeekableReadStream(Encoding.UTF8.GetBytes("abcdef")));

        Assert.False(ok);
        Assert.Empty(lines);
        Assert.Equal(BoundedTextFileReadFailureKind.BytesExceeded, failure.Kind);
        Assert.Equal(5, failure.Limit);
    }

    [Fact]
    public void TryReadUtf8File_EnforcesLineLimit_Issue4114()
    {
        var ok = BoundedLineReader.TryReadUtf8File(
            "input.txt",
            maxBytes: 1024,
            maxLines: 1,
            maxLineCharacters: 120,
            out _,
            out var failure,
            _ => new MemoryStream(Encoding.UTF8.GetBytes("first\nsecond\n")));

        Assert.False(ok);
        Assert.Equal(BoundedTextFileReadFailureKind.LinesExceeded, failure.Kind);
        Assert.Equal(1, failure.Limit);
    }

    [Fact]
    public void TryReadUtf8File_RejectsInvalidUtf8_Issue4114()
    {
        var ok = BoundedLineReader.TryReadUtf8File(
            "input.txt",
            maxBytes: 1024,
            maxLines: 10,
            maxLineCharacters: 120,
            out var lines,
            out var failure,
            _ => new MemoryStream([0xC3, 0x28]));

        Assert.False(ok);
        Assert.Empty(lines);
        Assert.Equal(BoundedTextFileReadFailureKind.InvalidUtf8, failure.Kind);
        Assert.Equal(nameof(DecoderFallbackException), failure.ExceptionType);
        Assert.Contains("not valid UTF-8", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadLine_SyncAndAsyncMatchUtf8AndLineBoundaries_Issue4114()
    {
        const string text = "alpha\r\nβeta\nlast";
        using var syncReader = new StringReader(text);
        using var asyncReader = new StringReader(text);

        var syncLines = new[]
        {
            BoundedLineReader.ReadLine(syncReader, 120, 120),
            BoundedLineReader.ReadLine(syncReader, 120, 120),
            BoundedLineReader.ReadLine(syncReader, 120, 120),
            BoundedLineReader.ReadLine(syncReader, 120, 120),
        };
        var asyncLines = new[]
        {
            await BoundedLineReader.ReadLineAsync(asyncReader, 120, 120, CancellationToken.None),
            await BoundedLineReader.ReadLineAsync(asyncReader, 120, 120, CancellationToken.None),
            await BoundedLineReader.ReadLineAsync(asyncReader, 120, 120, CancellationToken.None),
            await BoundedLineReader.ReadLineAsync(asyncReader, 120, 120, CancellationToken.None),
        };

        Assert.Equal(new string?[] { "alpha", "βeta", "last", null }, syncLines);
        Assert.Equal(syncLines, asyncLines);

        using var syncLimitReader = new StringReader("é");
        using var asyncLimitReader = new StringReader("é");
        var syncException = Assert.Throws<BoundedLineLengthException>(
            () => BoundedLineReader.ReadLine(syncLimitReader, 10, 1));
        var asyncException = await Assert.ThrowsAsync<BoundedLineLengthException>(
            () => BoundedLineReader.ReadLineAsync(asyncLimitReader, 10, 1, CancellationToken.None));

        Assert.Equal(syncException.Utf8BytesRead, asyncException.Utf8BytesRead);
        Assert.Equal(syncException.MaxUtf8Bytes, asyncException.MaxUtf8Bytes);
    }

    [Fact]
    public void BoundedTextWriter_TruncatesAcrossWriteOverloads_Issue4114()
    {
        using var writer = new BoundedTextWriter(maxChars: 5);

        writer.Write("abc");
        writer.Write(['d', 'e', 'f'], 0, 3);
        writer.Write('g');

        var captured = writer.GetCapturedText();

        Assert.StartsWith("abcde", captured, StringComparison.Ordinal);
        Assert.Contains("captured worker console output truncated", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("fg", captured, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadUtf8File_ReadFailureSanitizesExceptionMessage_Issue4069()
    {
        var ok = BoundedLineReader.TryReadUtf8File(
            "/tmp/codeindex/private/input.txt",
            maxBytes: 1024,
            maxLines: 10,
            maxLineCharacters: 120,
            out _,
            out var failure,
            _ => throw new IOException("open failed at /tmp/codeindex/private/input.txt --token=abc123"));

        Assert.False(ok);
        Assert.Equal(BoundedTextFileReadFailureKind.ReadFailed, failure.Kind);
        Assert.Contains("<path>", failure.Reason, StringComparison.Ordinal);
        Assert.Contains("--token=<redacted>", failure.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/codeindex/private", failure.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerOutputBuffer_KeepsBoundedTail_Issue3836()
    {
        var buffer = new WorkerOutputBuffer(maxCharacters: 24, maxLines: 2, maxLineCharacters: 12);

        buffer.AppendLine("first-sensitive-line");
        buffer.AppendLine("second-line");
        buffer.AppendLine("third-line");

        var captured = buffer.GetCapturedText();

        Assert.Contains("worker stderr truncated", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("first-sensitive", captured, StringComparison.Ordinal);
        Assert.Contains("third-line", captured, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatWorkerExit_IncludesSanitizedBoundedStderrTail_Issue3836()
    {
        var message = SafeDiagnosticFormatter.FormatWorkerExit(
            "worker_protocol_error",
            7,
            "worker exited before returning a response",
            "/private/secret/project/file.cs: raw stderr detail");

        Assert.Contains("worker exited with code 7", message, StringComparison.Ordinal);
        Assert.Contains("stderr_tail=", message, StringComparison.Ordinal);
        Assert.Contains("<path>", message, StringComparison.Ordinal);
        Assert.DoesNotContain("/private/secret", message, StringComparison.Ordinal);
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
