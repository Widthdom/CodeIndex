namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void AnalyzeSymbol_SameNameDefinitionsReturnIdentityScopedCandidateBundles()
    {
        InsertIndexedFile("src/inspect/IndexCommandRunner.cs", "csharp", """
            namespace InspectCandidates;
            public sealed class IndexCommandRunner
            {
                public void ParseArgs() => IndexOnly();
                private void IndexOnly() { }
                public void InvokeIndex() => ParseArgs();
            }
            """);
        InsertIndexedFile("src/inspect/QueryCommandRunner.cs", "csharp", """
            namespace InspectCandidates;
            public sealed class QueryCommandRunner
            {
                public void ParseArgs() => QueryOnly();
                private void QueryOnly() { }
                public void InvokeQuery() => ParseArgs();
            }
            """);

        var analysis = _reader.AnalyzeSymbol(
            "ParseArgs",
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/inspect/*"],
            exact: true);

        Assert.Equal(2, analysis.CandidateCount);
        Assert.Equal("primary_candidate", analysis.GraphScope);
        Assert.False(analysis.SelectionRequired);
        Assert.DoesNotContain(analysis.Callers, caller => caller.CallerName == "InvokeQuery");
        Assert.DoesNotContain(analysis.Callees, callee => callee.CalleeName == "QueryOnly");

        var bundles = Assert.IsType<List<CodeIndex.Database.SymbolCandidateBundle>>(analysis.CandidateBundles);
        Assert.Equal(2, bundles.Count);

        var indexBundle = Assert.Single(bundles, bundle => bundle.Definition.Path.EndsWith("IndexCommandRunner.cs", StringComparison.Ordinal));
        Assert.True(indexBundle.IdentityScoped);
        Assert.NotNull(indexBundle.Selector.SymbolId);
        Assert.StartsWith("id:", indexBundle.Selector.Selector, StringComparison.Ordinal);
        Assert.Equal("InspectCandidates.IndexCommandRunner.ParseArgs", indexBundle.Selector.QualifiedName);
        Assert.Contains("ParseArgs", indexBundle.Selector.Signature);
        Assert.Contains(indexBundle.Callers, caller => caller.CallerName == "InvokeIndex");
        Assert.DoesNotContain(indexBundle.Callers, caller => caller.CallerName == "InvokeQuery");
        Assert.Contains(indexBundle.Callees, callee => callee.CalleeName == "IndexOnly");
        Assert.DoesNotContain(indexBundle.Callees, callee => callee.CalleeName == "QueryOnly");

        var queryBundle = Assert.Single(bundles, bundle => bundle.Definition.Path.EndsWith("QueryCommandRunner.cs", StringComparison.Ordinal));
        Assert.True(queryBundle.IdentityScoped);
        Assert.NotNull(queryBundle.Selector.SymbolId);
        Assert.NotEqual(indexBundle.Selector.SymbolId, queryBundle.Selector.SymbolId);
        Assert.Equal("InspectCandidates.QueryCommandRunner.ParseArgs", queryBundle.Selector.QualifiedName);
        Assert.Contains(queryBundle.Callers, caller => caller.CallerName == "InvokeQuery");
        Assert.DoesNotContain(queryBundle.Callers, caller => caller.CallerName == "InvokeIndex");
        Assert.Contains(queryBundle.Callees, callee => callee.CalleeName == "QueryOnly");
        Assert.DoesNotContain(queryBundle.Callees, callee => callee.CalleeName == "IndexOnly");
    }
}
