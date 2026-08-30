using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void AnalyzeImpact_CountOnlyUsesDedicatedSafetyCapWithoutMaterializingRows_Issue5226()
    {
        InsertIndexedFile(
            "src/issue5226/Target.cs",
            "csharp",
            "public static class Issue5226Target { public static void Hit() { } }");
        for (int i = 0; i < 4; i++)
        {
            InsertIndexedFile(
                $"src/issue5226/Caller{i}.cs",
                "csharp",
                $"public sealed class Issue5226Caller{i} {{ public void Run() {{ Issue5226Target.Hit(); }} }}");
        }

        var count = _reader.AnalyzeImpact(
            "Issue5226Target.Hit",
            maxDepth: 1,
            limit: 1,
            lang: "csharp",
            pathPatterns: ["src/issue5226/*"],
            countOnly: true);

        Assert.Empty(count.Callers);
        Assert.Equal(4, count.ConfirmedCount);
        Assert.Equal(4, count.ConfirmedFileCount);
        Assert.Equal(4, count.CountFileHistogram.Count);
        Assert.Equal(1, count.ActualDepth);
        Assert.False(count.Truncated);
        Assert.True(count.CountIsAuthoritative);

        var rows = _reader.AnalyzeImpact(
            "Issue5226Target.Hit",
            maxDepth: 1,
            limit: 1,
            lang: "csharp",
            pathPatterns: ["src/issue5226/*"]);
        Assert.Single(rows.Callers);
        Assert.Equal(1, rows.ConfirmedCount);
        Assert.True(rows.Truncated);
        Assert.Equal(ImpactTruncatedReasons.UserLimit, rows.TruncatedReason);

        _reader.ImpactCountTraversalLimitForTesting = 2;
        try
        {
            var capped = _reader.AnalyzeImpact(
                "Issue5226Target.Hit",
                maxDepth: 1,
                limit: 1,
                lang: "csharp",
                pathPatterns: ["src/issue5226/*"],
                countOnly: true);

            Assert.Empty(capped.Callers);
            Assert.Equal(2, capped.ConfirmedCount);
            Assert.True(capped.Truncated);
            Assert.Equal(ImpactTruncatedReasons.SafetyCap, capped.TruncatedReason);
            Assert.Equal(ImpactTerminationReasons.SafetyCap, capped.TerminationReason);
            Assert.False(capped.CountIsAuthoritative);
        }
        finally
        {
            _reader.ImpactCountTraversalLimitForTesting = null;
        }
    }
}
