using System.Text;
using CodeIndex.Security;

namespace CodeIndex.Mcp;

internal sealed class BoundedJsonUtf8Stream(int maxBytes, bool captureSerialized, Func<int, Exception> createLimitExceededException) : Stream
{
    private readonly MemoryStream? _buffer = captureSerialized ? new MemoryStream(SensitiveBufferPolicy.GetBoundedGeneratedJsonInitialCapacity(maxBytes)) : null;

    public int BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public string? GetCapturedString()
    {
        if (_buffer is null)
            return null;
        return Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0)
            return;

        var remaining = maxBytes - BytesWritten;
        if (remaining < buffer.Length)
        {
            if (remaining > 0)
                _buffer?.Write(buffer[..remaining]);
            BytesWritten = maxBytes == int.MaxValue ? int.MaxValue : maxBytes + 1;
            throw createLimitExceededException(BytesWritten);
        }

        _buffer?.Write(buffer);
        BytesWritten += buffer.Length;
    }
}
