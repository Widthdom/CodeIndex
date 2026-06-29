namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void GetFileSymbolHotspots_SymbolCountIncludesUnreferencedFilteredSymbols_Issue4159()
    {
        InsertIndexedFile("src/file_hotspot_symbol_count.py", "python",
            "def ReferencedTarget():\n    return True\n\n" +
            "def UnreferencedTarget():\n    return True\n\n" +
            "def use_target():\n    ReferencedTarget()\n");

        var results = _reader.GetFileSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/file_hotspot_symbol_count.py"],
            excludePathPatterns: null,
            excludeTests: false);

        var file = Assert.Single(results);
        Assert.Equal("src/file_hotspot_symbol_count.py", file.Path);
        Assert.Equal(1, file.ReferenceCount);
        Assert.Equal(3, file.SymbolCount);
    }
}
