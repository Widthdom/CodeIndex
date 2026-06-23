using CodeIndex.Indexer;

namespace CodeIndex;

internal static class BoundedFile
{
    internal const int DefaultReadBufferSize = 81920;
    internal const int SmallReadBufferSize = 8192;

    internal static FileStream OpenReadForLengthCheckedText(string path)
        => OpenRead(path, FileShare.ReadWrite | FileShare.Delete, SmallReadBufferSize);

    internal static FileStream OpenReadForPrefixProbe(string path)
        => OpenRead(path, FileShare.ReadWrite | FileShare.Delete, SmallReadBufferSize);

    internal static FileStream OpenReadForTail(string path)
        => OpenRead(path, FileShare.ReadWrite | FileShare.Delete, SmallReadBufferSize);

    internal static Stream OpenReadForTailWindow(string path, long maxBytes, out bool bytesTruncated)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Maximum tail bytes must be positive.");

        var stream = OpenReadForTail(path);
        var length = stream.Length;
        var startOffset = Math.Max(0, length - maxBytes);
        bytesTruncated = startOffset > 0;
        stream.Seek(startOffset, SeekOrigin.Begin);
        return new BoundedReadStream(stream, length - startOffset, leaveOpen: false);
    }

    internal static FileStream OpenReadForHash(string path)
        => OpenRead(path, FileShare.Read, DefaultReadBufferSize);

    internal static FileStream OpenReadTrustedArchiveSource(string path)
        => OpenRead(path, FileShare.Read, DefaultReadBufferSize);

    internal static FileStream OpenRead(
        string path,
        FileShare share,
        int bufferSize = DefaultReadBufferSize,
        FileOptions options = FileOptions.SequentialScan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize), bufferSize, "Read buffer size must be positive.");

        return new FileStream(
            LongPath.EnsureWindowsPrefix(path),
            FileMode.Open,
            FileAccess.Read,
            share,
            bufferSize: bufferSize,
            options: options);
    }

    private sealed class BoundedReadStream(Stream inner, long maxBytes, bool leaveOpen) : Stream
    {
        private long _remaining = maxBytes;
        private bool _disposed;

        public override bool CanRead => !_disposed && inner.CanRead;

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
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_remaining <= 0)
                return 0;

            var boundedCount = (int)Math.Min(count, _remaining);
            var read = inner.Read(buffer, offset, boundedCount);
            _remaining -= read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_remaining <= 0)
                return 0;

            var boundedCount = (int)Math.Min(buffer.Length, _remaining);
            var read = inner.Read(buffer[..boundedCount]);
            _remaining -= read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing && !leaveOpen)
                inner.Dispose();
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
