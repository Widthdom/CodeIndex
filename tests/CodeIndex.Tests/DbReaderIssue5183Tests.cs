namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void ExactCallerAndImpactQueries_RequireIdentityBackedRoots_Issue5183()
    {
        InsertIssue5183GraphFixture();

        var missingExact = _reader.GetCallers(
            "MissingLeaf5183",
            lang: "csharp",
            exact: true,
            pathPatterns: ["src/issue5183/*"]);
        var missingCount = _reader.CountCallersTotal(
            "MissingLeaf5183",
            lang: "csharp",
            exact: true,
            pathPatterns: ["src/issue5183/*"]);
        var missingBroad = _reader.GetCallers(
            "MissingLeaf5183",
            lang: "csharp",
            exact: false,
            pathPatterns: ["src/issue5183/*"]);

        Assert.Empty(missingExact);
        Assert.Equal(0, missingCount.Count);
        Assert.False(missingCount.IdentityRootAvailable);
        Assert.Equal("no_identity_backed_root", missingCount.IdentityRootUnavailableReason);
        Assert.Equal("no_identity_root", missingCount.GraphEvidenceConfidence);
        Assert.Contains(missingBroad, caller => caller.CallerName == "MiddleMissing5183");

        var resolved = _reader.GetCallers(
            "IdentityLeaf5183",
            lang: "csharp",
            exact: true,
            pathPatterns: ["src/issue5183/*"]);
        var resolvedCaller = Assert.Single(resolved);
        Assert.Equal("CallResolved5183", resolvedCaller.CallerName);
        Assert.Equal(1, resolvedCaller.ReferenceCount);
        var resolvedCount = _reader.CountCallersTotal(
            "IdentityLeaf5183",
            lang: "csharp",
            exact: true,
            pathPatterns: ["src/issue5183/*"]);
        Assert.True(resolvedCount.IdentityRootAvailable);
        Assert.Equal("identity_backed", resolvedCount.GraphEvidenceConfidence);

        var collisions = _reader.GetCallers(
            "CollisionLeaf5183",
            lang: "csharp",
            exact: true,
            pathPatterns: ["src/issue5183/*"]);
        Assert.Equal(
            ["CallFirst5183", "CallSecond5183"],
            collisions.Select(caller => caller.CallerName).Order().ToArray());

        var missingImpact = _reader.AnalyzeImpact(
            "MissingLeaf5183",
            maxDepth: 5,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/issue5183/*"]);
        Assert.Empty(missingImpact.Callers);
        Assert.Equal(0, missingImpact.DefinitionCount);
        Assert.False(missingImpact.IdentityRootAvailable);
        Assert.Equal("no_identity_backed_root", missingImpact.IdentityRootUnavailableReason);
        Assert.Equal("no_identity_root", missingImpact.GraphEvidenceConfidence);
        Assert.True(missingImpact.Heuristic);
        Assert.False(missingImpact.CountIsAuthoritative);
        Assert.Contains("no_identity_backed_root", missingImpact.ImpactFailureChain!);

        var resolvedImpact = _reader.AnalyzeImpact(
            "Issue5183.ResolvedTarget5183.IdentityLeaf5183",
            maxDepth: 5,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/issue5183/*"]);
        Assert.True(resolvedImpact.IdentityRootAvailable);
        Assert.Equal(
            ["AboveResolved5183", "CallResolved5183"],
            resolvedImpact.Callers.Select(caller => caller.CallerName).Order().ToArray());

        var sameNameImpact = _reader.AnalyzeImpact(
            "CollisionLeaf5183",
            maxDepth: 5,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/issue5183/*"]);
        Assert.True(sameNameImpact.IdentityRootAvailable);
        Assert.Equal(
            ["CallFirst5183", "CallSecond5183"],
            sameNameImpact.Callers.Select(caller => caller.CallerName).Order().ToArray());

        var multilingualCallers = _reader.GetCallers(
            "IdentityLeaf5183",
            exact: true,
            pathPatterns: ["src/issue5183/*"]);
        Assert.Equal(
            ["CallResolved5183", "PythonCallsCSharp5183"],
            multilingualCallers.Select(caller => caller.CallerName).Order().ToArray());

        var multilingualImpact = _reader.AnalyzeImpact(
            "IdentityLeaf5183",
            maxDepth: 5,
            limit: 20,
            pathPatterns: ["src/issue5183/*"]);
        Assert.Equal("identity_backed", multilingualImpact.GraphEvidenceConfidence);
        Assert.Contains(multilingualImpact.Callers, caller => caller.CallerName == "CallResolved5183");
        Assert.Contains(multilingualImpact.Callers, caller => caller.CallerName == "PythonCallsCSharp5183");
        Assert.DoesNotContain(multilingualImpact.Callers, caller => caller.CallerName == "CallUnresolvedSameLeaf5183");

        var pythonOnlyCallers = _reader.GetCallers(
            "PythonOnlyLeaf5183",
            exact: true,
            pathPatterns: ["src/issue5183/*"]);
        var pythonOnlyCaller = Assert.Single(pythonOnlyCallers);
        Assert.Equal("PythonCaller5183", pythonOnlyCaller.CallerName);
        var pythonOnlyCount = _reader.CountCallersTotal(
            "PythonOnlyLeaf5183",
            exact: true,
            pathPatterns: ["src/issue5183/*"]);
        Assert.True(pythonOnlyCount.IdentityRootAvailable);
        Assert.Equal("language_graph", pythonOnlyCount.GraphEvidenceConfidence);

        var pythonOnlyImpact = _reader.AnalyzeImpact(
            "PythonOnlyLeaf5183",
            maxDepth: 5,
            limit: 20,
            pathPatterns: ["src/issue5183/*"]);
        Assert.Equal("language_graph", pythonOnlyImpact.GraphEvidenceConfidence);
        Assert.True(pythonOnlyImpact.IdentityRootAvailable);
        Assert.True(pythonOnlyImpact.CountIsAuthoritative);
        Assert.Equal(
            ["PythonCaller5183", "PythonTop5183"],
            pythonOnlyImpact.Callers.Select(caller => caller.CallerName).Order().ToArray());
        Assert.DoesNotContain(pythonOnlyImpact.Callers, caller => caller.CallerName == "CallUnresolvedPythonLeaf5183");

        var pythonExternalImpact = _reader.AnalyzeImpact(
            "ExternalOnlyLeaf5183",
            maxDepth: 5,
            limit: 20,
            pathPatterns: ["src/issue5183/*"]);
        Assert.False(pythonExternalImpact.IdentityRootAvailable);
        Assert.Equal("no_identity_backed_root", pythonExternalImpact.IdentityRootUnavailableReason);
        Assert.False(pythonExternalImpact.CountIsAuthoritative);
        Assert.Equal(
            ["PythonExternalCaller5183", "PythonExternalTop5183"],
            pythonExternalImpact.Callers.Select(caller => caller.CallerName).Order().ToArray());
        Assert.DoesNotContain(
            pythonExternalImpact.Callers,
            caller => caller.CallerName == "CallUnresolvedExternalOnly5183");

        _reader.ImpactGraphStateEntryBudgetForTesting = 1;
        try
        {
            var cappedResolvedImpact = _reader.AnalyzeImpact(
                "Issue5183.ResolvedTarget5183.IdentityLeaf5183",
                maxDepth: 5,
                limit: 20,
                lang: "csharp",
                pathPatterns: ["src/issue5183/*"]);
            Assert.Empty(cappedResolvedImpact.Callers);
            Assert.True(cappedResolvedImpact.IdentityRootAvailable);
            Assert.True(cappedResolvedImpact.Truncated);
            Assert.Equal(
                global::CodeIndex.Database.ImpactTruncatedReasons.GraphStateBudget,
                cappedResolvedImpact.TruncatedReason);
            Assert.Equal(
                global::CodeIndex.Database.ImpactTerminationReasons.GraphStateBudget,
                cappedResolvedImpact.TerminationReason);
            Assert.False(cappedResolvedImpact.CountIsAuthoritative);
        }
        finally
        {
            _reader.ImpactGraphStateEntryBudgetForTesting = null;
        }

        _writer.MarkIndexIncomplete(["reference_count_exceeded"]);
        using var incompleteReader = new global::CodeIndex.Database.DbReader(_db.Connection);
        var incompleteImpact = incompleteReader.AnalyzeImpact(
            "IdentityLeaf5183",
            maxDepth: 5,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/issue5183/*"]);
        Assert.True(incompleteImpact.IdentityRootAvailable);
        Assert.False(incompleteImpact.ReferenceGraphComplete);
        Assert.False(incompleteImpact.CountIsAuthoritative);

        _writer.ClearReferenceIdentityContractReady();
        using var staleReader = new global::CodeIndex.Database.DbReader(_db.Connection);
        var staleImpact = staleReader.AnalyzeImpact(
            "MissingLeaf5183",
            maxDepth: 5,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/issue5183/*"]);
        Assert.NotEmpty(staleImpact.Callers);
        Assert.False(staleImpact.IdentityRootAvailable);
        Assert.Equal("reference_identity_unavailable", staleImpact.IdentityRootUnavailableReason);
        Assert.Equal("name_fallback", staleImpact.GraphEvidenceConfidence);
        Assert.True(staleImpact.Heuristic);
        Assert.False(staleImpact.CountIsAuthoritative);
    }

    [Fact]
    public void InspectAndHotspots_DoNotPromoteUnresolvedSameLeafCalls_Issue5183()
    {
        InsertIssue5183GraphFixture();

        var inspect = _reader.AnalyzeSymbol(
            "MissingLeaf5183",
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/issue5183/*"],
            exact: true);

        Assert.Empty(inspect.Definitions);
        Assert.NotEmpty(inspect.References);
        Assert.All(inspect.References, reference => Assert.Equal("unresolved", reference.ResolutionState));
        Assert.Empty(inspect.Callers);
        Assert.Equal(0, inspect.GraphSections.Callers.Total);
        Assert.Equal("query_fallback", inspect.GraphScope);

        var hotspots = _reader.GetSymbolHotspots(
            50,
            "function",
            "csharp",
            ["src/issue5183/*"],
            excludePathPatterns: null,
            excludeTests: false);
        var resolvedHotspot = Assert.Single(
            hotspots,
            hotspot => hotspot.Symbol.Name == "IdentityLeaf5183");
        Assert.Equal(1, resolvedHotspot.ReferenceCount);
        Assert.DoesNotContain(
            hotspots,
            hotspot => hotspot.Symbol.Name == "MissingLeaf5183");
    }

    private void InsertIssue5183GraphFixture()
    {
        InsertIndexedFile(
            "src/issue5183/Targets.cs",
            "csharp",
            """
            namespace Issue5183;

            public class ResolvedTarget5183
            {
                public void IdentityLeaf5183() { }
            }

            public class FirstTarget5183
            {
                public void CollisionLeaf5183() { }
            }

            public class SecondTarget5183
            {
                public void CollisionLeaf5183() { }
            }
            """);
        InsertIndexedFile(
            "src/issue5183/Callers.cs",
            "csharp",
            """
            namespace Issue5183;

            public class CallerFixture5183
            {
                public void CallResolved5183(ResolvedTarget5183 target) => target.IdentityLeaf5183();
                public void AboveResolved5183() => CallResolved5183(new ResolvedTarget5183());
                public void CallUnresolvedSameLeaf5183() => ExternalApi5183.IdentityLeaf5183();
                public void CallUnresolvedPythonLeaf5183() => ExternalApi5183.PythonOnlyLeaf5183();
                public void CallUnresolvedExternalOnly5183() => ExternalApi5183.ExternalOnlyLeaf5183();
                public void CallFirst5183(FirstTarget5183 target) => target.CollisionLeaf5183();
                public void CallSecond5183(SecondTarget5183 target) => target.CollisionLeaf5183();
                public void CallUnresolvedCollision5183() => ExternalApi5183.CollisionLeaf5183();
                public void TopMissing5183() => MiddleMissing5183();
                public void MiddleMissing5183() => ExternalApi5183.MissingLeaf5183();
            }
            """);
        InsertIndexedFile(
            "src/issue5183/callers.py",
            "python",
            """
            def PythonOnlyLeaf5183():
                pass

            def PythonCaller5183():
                return PythonOnlyLeaf5183()

            def PythonTop5183():
                return PythonCaller5183()

            def PythonCallsCSharp5183():
                return IdentityLeaf5183()

            def PythonExternalCaller5183():
                return ExternalOnlyLeaf5183()

            def PythonExternalTop5183():
                return PythonExternalCaller5183()
            """);
    }
}
