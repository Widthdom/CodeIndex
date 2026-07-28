using System.Text;
using CodeIndex.Lsp;

namespace CodeIndex.Tests;

public class LspProtocolTests
{
    [Fact]
    public async Task TryReadMessageAsync_ReadsContentLengthFramedPayload()
    {
        const string payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}";
        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}");
        using var stream = new MemoryStream(bytes);

        var result = await LspProtocol.TryReadMessageAsync(stream);

        Assert.True(result.Success);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void LiveDocumentStore_EvictsOldestDocumentWhenCapacityIsExceeded()
    {
        var store = new LspLiveDocumentStore(
            StringComparer.Ordinal,
            StringComparison.Ordinal,
            maxDocuments: 1,
            maxDocumentBytes: 100,
            maxLiveBytes: 100);

        store.SetText("/first.cs", "first");
        store.SetText("/second.cs", "second");

        Assert.False(store.TryGetText("/first.cs", out _));
        Assert.True(store.TryGetText("/second.cs", out var retainedText));
        Assert.Equal("second", retainedText);
        Assert.Equal(1, store.EvictionCount);
        Assert.Equal(Encoding.UTF8.GetByteCount("first"), store.EvictedBytes);
    }

    [Fact]
    public void LiveDocumentStore_PreservesBoundedVersionTombstonesAcrossEviction()
    {
        var store = new LspLiveDocumentStore(
            StringComparer.Ordinal,
            StringComparison.Ordinal,
            maxDocuments: 1,
            maxDocumentBytes: 100,
            maxLiveBytes: 100);

        Assert.True(store.SetText("/first.cs", "first", version: 3));
        Assert.True(store.SetText("/second.cs", "second", version: 2));
        Assert.False(store.SetText("/first.cs", "stale", version: 2));
        Assert.Equal(1, store.VersionTombstoneCount);

        store.Remove("/first.cs");

        Assert.True(store.SetText("/first.cs", "reopened", version: 2));
        Assert.True(store.VersionTombstoneCount <= 1);
    }

    [Fact]
    public void LiveDocumentStore_PreservesNewerVersionWhenOversizedTextIsDropped()
    {
        var store = new LspLiveDocumentStore(
            StringComparer.Ordinal,
            StringComparison.Ordinal,
            maxDocuments: 1,
            maxDocumentBytes: 5,
            maxLiveBytes: 5);

        Assert.True(store.SetText("/app.cs", "small", version: 3));
        Assert.False(store.SetText("/app.cs", "oversized", version: 4));
        Assert.False(store.TryGetText("/app.cs", out _));
        Assert.False(store.SetText("/app.cs", "old", version: 3));
        Assert.Equal(1, store.VersionTombstoneCount);
    }
}
