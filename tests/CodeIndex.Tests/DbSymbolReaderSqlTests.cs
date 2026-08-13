namespace CodeIndex.Tests;

public class DbSymbolReaderSqlTests
{
    [Fact]
    public void HotspotFilteredCandidates_UseExplicitProjection()
    {
        var source = RepositoryTestPaths.ReadText(
            "src",
            "CodeIndex",
            "Database",
            "DbSymbolReader.HotspotCandidates.cs");
        var filteredBlock = Assert.Single(source
            .Replace("filtered_candidates AS MATERIALIZED (", "filtered_candidates AS (", StringComparison.Ordinal)
            .Split("filtered_candidates AS (", StringSplitOptions.None)
            .Skip(1));

        var projection = filteredBlock[..filteredBlock.IndexOf(
            "FROM all_candidate_symbols",
            StringComparison.Ordinal)];
        Assert.DoesNotContain("SELECT *", projection, StringComparison.Ordinal);
        Assert.Equal(
            "SELECT id, file_id, name, kind, path, lang, line, visibility, container_name, logical_target_key",
            string.Join(
                ' ',
                projection.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    }

}
