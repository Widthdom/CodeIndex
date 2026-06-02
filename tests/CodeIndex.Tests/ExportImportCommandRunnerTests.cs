using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class ExportImportCommandRunnerTests
{
    [Fact]
    public void TryValidateDatabaseEntrySize_RejectsOversizedUncompressedLength()
    {
        var ok = ExportImportCommandRunner.TryValidateDatabaseEntrySize(
            uncompressedLength: ExportImportCommandRunner.MaxImportDatabaseBytes + 1,
            compressedLength: 1,
            message: out var message);

        Assert.False(ok);
        Assert.Contains("uncompressed exceeds the import limit", message);
    }

    [Fact]
    public void TryValidateDatabaseEntrySize_RejectsOversizedCompressedLength()
    {
        var ok = ExportImportCommandRunner.TryValidateDatabaseEntrySize(
            uncompressedLength: 1,
            compressedLength: ExportImportCommandRunner.MaxImportDatabaseBytes + 1,
            message: out var message);

        Assert.False(ok);
        Assert.Contains("compressed exceeds the import limit", message);
    }

    [Fact]
    public void CopyToWithLimit_ThrowsBeforeWritingPastLimit()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var target = new MemoryStream();

        var ex = Assert.Throws<InvalidDataException>(() => ExportImportCommandRunner.CopyToWithLimit(source, target, maxBytes: 3));

        Assert.Contains("codeindex.db exceeds the import limit", ex.Message);
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public void CopyToWithLimit_AllowsExactLimit()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var target = new MemoryStream();

        var copied = ExportImportCommandRunner.CopyToWithLimit(source, target, maxBytes: 4);

        Assert.Equal(4, copied);
        Assert.Equal([1, 2, 3, 4], target.ToArray());
    }
}
