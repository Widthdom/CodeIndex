using System.Net;
using System.Text;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class BoundedHttpContentReaderTests
{
    [Fact]
    public async Task WriteToPrivateFileAsync_WritesPrivateContentAndRejectsOverflow()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_install_content");
        try
        {
            var path = TestProjectHelper.ProjectPath(projectRoot, "install.sh");
            await BoundedHttpContentReader.WriteToPrivateFileAsync(
                new UnknownLengthContent(Encoding.UTF8.GetBytes("#!/bin/sh\nexit 0\n")),
                path,
                maxBytes: 1024,
                CancellationToken.None);

            Assert.Equal("#!/bin/sh\nexit 0\n", File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
            {
                var permissions = File.GetUnixFileMode(path) & PermissionBits;
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, permissions);
            }

            var overflowPath = TestProjectHelper.ProjectPath(projectRoot, "overflow.sh");
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                BoundedHttpContentReader.WriteToPrivateFileAsync(
                    new UnknownLengthContent(Encoding.UTF8.GetBytes("12345")),
                    overflowPath,
                    maxBytes: 4,
                    CancellationToken.None));

            Assert.Contains("4 byte limit", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task ReadAsByteArrayAsync_ReadsUnknownLengthAndRejectsDeclaredOverflow_Issue3799()
    {
        var payload = Encoding.UTF8.GetBytes("normal payload");

        var bytes = await BoundedHttpContentReader.ReadAsByteArrayAsync(
            new UnknownLengthContent(payload),
            maxBytes: 1024,
            CancellationToken.None);

        Assert.Equal(payload, bytes);

        using var content = new ByteArrayContent([1]);
        content.Headers.ContentLength = 5;

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedHttpContentReader.ReadAsByteArrayAsync(content, maxBytes: 4, CancellationToken.None));

        Assert.Contains("4 byte limit", ex.Message);
    }

    [Fact]
    public async Task MemorySafety_AvoidsHugePreallocationAndClearsPooledBuffer_Issues3799_3964()
    {
        using var content = new DeclaredLengthContent([], int.MaxValue);

        var bytes = await BoundedHttpContentReader.ReadAsByteArrayAsync(
            content,
            maxBytes: int.MaxValue,
            CancellationToken.None);

        Assert.Empty(bytes);

        var buffer = Enumerable.Range(1, BoundedHttpContentReader.PooledCopyBufferSize)
            .Select(i => (byte)(i % 251))
            .ToArray();

        BoundedHttpContentReader.ClearSensitiveCopyBufferForTests(buffer);

        Assert.All(buffer, value => Assert.Equal(0, value));
    }

    private const UnixFileMode PermissionBits =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherWrite
        | UnixFileMode.OtherExecute;

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _payload;

        internal UnknownLengthContent(byte[] payload)
        {
            _payload = payload;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_payload, 0, _payload.Length);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(_payload, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class DeclaredLengthContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly long _declaredLength;

        internal DeclaredLengthContent(byte[] payload, long declaredLength)
        {
            _payload = payload;
            _declaredLength = declaredLength;
            Headers.ContentLength = declaredLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_payload, 0, _payload.Length);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(_payload, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = _declaredLength;
            return true;
        }
    }
}
