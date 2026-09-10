using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace CodeIndex.Cli;

// End interrupted byte reads as EOF so StreamReader returns characters it already decoded.
// 中断したバイト読み取りを EOF として扱い、StreamReader が復号済みの文字を返せるようにします。
internal sealed class CancellableInstallerOutputStream(Stream source, CancellationToken drainToken) : Stream
{
    private readonly byte[] _buffer = new byte[4096];
    internal bool Incomplete { get; private set; }

    internal static StreamReader CreateReader(StreamReader reader, CancellationToken drainToken,
        out CancellableInstallerOutputStream stream)
    {
        stream = new CancellableInstallerOutputStream(reader.BaseStream, drainToken);
        return new StreamReader(stream, reader.CurrentEncoding, detectEncodingFromByteOrderMarks: true);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;
        try
        {
            if (!OperatingSystem.IsWindows())
                return await source.ReadAsync(buffer, drainToken).ConfigureAwait(false);
            return await ReadWindowsPipeAsync(buffer).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is OperationCanceledException && drainToken.IsCancellationRequested)
        {
            Incomplete = true;
            return 0;
        }
    }

    private async ValueTask<int> ReadWindowsPipeAsync(Memory<byte> buffer)
    {
        SafeHandle handle = source switch
        {
            PipeStream pipe => pipe.SafePipeHandle,
            FileStream file => file.SafeFileHandle,
            _ => throw new NotSupportedException("Unsupported installer output pipe."),
        };
        while (true)
        {
            drainToken.ThrowIfCancellationRequested();
            if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 109) // ERROR_BROKEN_PIPE / 書き込み側が全て閉じたパイプ
                    return 0;
                throw new IOException("Installer output pipe probe failed.");
            }
            if (available > 0)
            {
                var count = (int)Math.Min(available, (uint)Math.Min(buffer.Length, _buffer.Length));
                if (!ReadFile(handle, _buffer, count, out var read, IntPtr.Zero))
                    throw new IOException("Installer output pipe read failed.");
                _buffer.AsMemory(0, read).CopyTo(buffer);
                return read;
            }
            await Task.Delay(20, drainToken).ConfigureAwait(false);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // The Process owns the handle; this adapter has no pending native read when cancelled.
    // ハンドルは Process が所有し、中断後に未完了のネイティブ読み取りは残りません。
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafeHandle handle, IntPtr buffer, uint size,
        IntPtr bytesRead, out uint available, IntPtr bytesLeft);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeHandle handle, [Out] byte[] buffer, int size,
        out int bytesRead, IntPtr overlapped);
}
