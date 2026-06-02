using System.Net;
using System.Text;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class BoundedHttpContentReaderTests
{
    [Fact]
    public async Task WriteToPrivateFileAsync_WritesContentWithOwnerOnlyMode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx-install-test-{Guid.NewGuid():N}.sh");
        try
        {
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
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteToPrivateFileAsync_RejectsStreamOverLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx-install-test-{Guid.NewGuid():N}.sh");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                BoundedHttpContentReader.WriteToPrivateFileAsync(
                    new UnknownLengthContent(Encoding.UTF8.GetBytes("12345")),
                    path,
                    maxBytes: 4,
                    CancellationToken.None));

            Assert.Contains("4 byte limit", ex.Message);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsByteArrayAsync_RejectsDeclaredLengthOverLimit()
    {
        using var content = new ByteArrayContent([1]);
        content.Headers.ContentLength = 5;

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedHttpContentReader.ReadAsByteArrayAsync(content, maxBytes: 4, CancellationToken.None));

        Assert.Contains("4 byte limit", ex.Message);
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
}
