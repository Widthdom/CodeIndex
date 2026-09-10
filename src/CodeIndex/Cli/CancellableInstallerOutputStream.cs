using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace CodeIndex.Cli;

// Windows Process pipes are synchronous: only read bytes PeekNamedPipe says are available.
// Windows の Process パイプは同期式のため、PeekNamedPipe で確認したバイトだけ読みます。
internal sealed class CancellableInstallerOutputStream(SafeHandle handle) : Stream
{
    private readonly byte[] _buffer = new byte[4096];

    internal static StreamReader CreateReader(StreamReader reader)
        => OperatingSystem.IsWindows()
            ? new StreamReader(new CancellableInstallerOutputStream(reader.BaseStream switch
                {
                    PipeStream pipe => pipe.SafePipeHandle,
                    FileStream file => file.SafeFileHandle,
                    _ => throw new NotSupportedException("Unsupported installer output pipe."),
                }),
                reader.CurrentEncoding, detectEncodingFromByteOrderMarks: true)
            : reader;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
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
