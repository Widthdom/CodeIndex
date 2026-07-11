namespace CodeIndex.Tests;

public class DbSymbolReaderSqlTests
{
    [Fact]
    public void HotspotFilteredCandidates_UseExplicitProjection()
    {
        var source = RepositoryTestPaths.ReadText("src", "CodeIndex", "Database", "DbSymbolReader.Hotspots.cs");
        var filteredBlocks = source.Split("filtered_candidates AS (", StringSplitOptions.None).Skip(1).ToList();

        Assert.Equal(2, filteredBlocks.Count);
        foreach (var block in filteredBlocks)
        {
            var projection = block[..block.IndexOf("FROM all_candidate_symbols", StringComparison.Ordinal)];
            Assert.DoesNotContain("SELECT *", projection, StringComparison.Ordinal);
            Assert.Contains("SELECT id,", projection, StringComparison.Ordinal);
            Assert.Contains("file_id,", projection, StringComparison.Ordinal);
            Assert.Contains("name,", projection, StringComparison.Ordinal);
            Assert.Contains("kind,", projection, StringComparison.Ordinal);
            Assert.Contains("path,", projection, StringComparison.Ordinal);
            Assert.Contains("lang,", projection, StringComparison.Ordinal);
            Assert.Contains("line,", projection, StringComparison.Ordinal);
            Assert.Contains("visibility,", projection, StringComparison.Ordinal);
            Assert.Contains("container_name,", projection, StringComparison.Ordinal);
            Assert.Contains("logical_target_key", projection, StringComparison.Ordinal);
        }
    }

}
