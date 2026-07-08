using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class FileIndexerTests
{
    [Fact]
    public void IsGeneratedCodeFile_GeneratedCsMarker_ReturnsTrue_Issue4334()
    {
        Assert.True(FileIndexer.IsGeneratedCodeFile("src/Beta.Generated.cs", "class Beta { }"));
    }
}
