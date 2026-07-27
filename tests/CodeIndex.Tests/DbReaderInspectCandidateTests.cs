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

    [Fact]
    public void AnalyzeSymbol_DependencyLockUsesFileTargetOwnerAndNodeIdentity_Issue4845()
    {
        InsertIndexedFile("src/A/packages.lock.json", "dependency_lock", """
            {
              "version": 1,
              "dependencies": {
                "net8.0": {
                  "Microsoft.Data.Sqlite": {
                    "type": "Direct",
                    "resolved": "8.0.0",
                    "dependencies": {
                      "Microsoft.Data.Sqlite.Core": "8.0.0"
                    }
                  },
                  "Microsoft.Data.Sqlite.Core": {
                    "type": "Transitive",
                    "resolved": "8.0.0"
                  }
                },
                "net8.0/win-x64": {
                  "Microsoft.Data.Sqlite": {
                    "type": "Direct",
                    "resolved": "8.0.0",
                    "dependencies": {
                      "Microsoft.Data.Sqlite.Core": "8.0.0"
                    }
                  },
                  "Microsoft.Data.Sqlite.Core": {
                    "type": "Transitive",
                    "resolved": "8.0.0"
                  }
                },
                "net8.0/linux-x64": {
                  "Microsoft.Data.Sqlite": {
                    "type": "Direct",
                    "resolved": "8.0.0",
                    "dependencies": {
                      "Microsoft.Data.Sqlite.Core": "8.0.0"
                    }
                  },
                  "Microsoft.Data.Sqlite.Core": {
                    "type": "Transitive",
                    "resolved": "8.0.0"
                  }
                }
              }
            }
            """);
        InsertIndexedFile("src/B/packages.lock.json", "dependency_lock", """
            {
              "version": 1,
              "dependencies": {
                "net8.0": {
                  "Microsoft.Data.Sqlite": {
                    "type": "Direct",
                    "resolved": "9.0.0",
                    "dependencies": {
                      "Microsoft.Data.Sqlite.Core": "9.0.0"
                    }
                  },
                  "Microsoft.Data.Sqlite.Core": {
                    "type": "Transitive",
                    "resolved": "9.0.0"
                  }
                }
              }
            }
            """);

        var childAnalysis = _reader.AnalyzeSymbol(
            "Microsoft.Data.Sqlite.Core",
            limit: 10,
            lang: "dependency_lock",
            exact: true);
        var childBundles = Assert.IsType<List<CodeIndex.Database.SymbolCandidateBundle>>(
            childAnalysis.CandidateBundles);
        Assert.Equal(4, childBundles.Count);

        var aNet8Child = Assert.Single(
            childBundles,
            bundle => bundle.Definition.Path == "src/A/packages.lock.json"
                && bundle.Definition.ContainerName == "net8.0");
        Assert.Equal(12, aNet8Child.Definition.Line);
        var aNet8Caller = Assert.Single(aNet8Child.Callers);
        Assert.Equal("src/A/packages.lock.json", aNet8Caller.Path);
        Assert.Equal("Microsoft.Data.Sqlite", aNet8Caller.CallerName);
        Assert.Equal(9, aNet8Caller.FirstLine);

        var aRidChild = Assert.Single(
            childBundles,
            bundle => bundle.Definition.Path == "src/A/packages.lock.json"
                && bundle.Definition.ContainerName == "net8.0/win-x64");
        Assert.Equal(25, aRidChild.Definition.Line);
        var aRidCaller = Assert.Single(aRidChild.Callers);
        Assert.Equal("src/A/packages.lock.json", aRidCaller.Path);
        Assert.Equal("Microsoft.Data.Sqlite", aRidCaller.CallerName);
        Assert.Equal(22, aRidCaller.FirstLine);

        var aLinuxRidChild = Assert.Single(
            childBundles,
            bundle => bundle.Definition.Path == "src/A/packages.lock.json"
                && bundle.Definition.ContainerName == "net8.0/linux-x64");
        Assert.Equal(38, aLinuxRidChild.Definition.Line);
        var aLinuxRidCaller = Assert.Single(aLinuxRidChild.Callers);
        Assert.Equal("src/A/packages.lock.json", aLinuxRidCaller.Path);
        Assert.Equal("Microsoft.Data.Sqlite", aLinuxRidCaller.CallerName);
        Assert.Equal(35, aLinuxRidCaller.FirstLine);

        var bNet8Child = Assert.Single(
            childBundles,
            bundle => bundle.Definition.Path == "src/B/packages.lock.json"
                && bundle.Definition.ContainerName == "net8.0");
        Assert.Equal(12, bNet8Child.Definition.Line);
        var bNet8Caller = Assert.Single(bNet8Child.Callers);
        Assert.Equal("src/B/packages.lock.json", bNet8Caller.Path);
        Assert.Equal("Microsoft.Data.Sqlite", bNet8Caller.CallerName);
        Assert.Equal(9, bNet8Caller.FirstLine);

        Assert.All(
            childBundles,
            bundle => Assert.All(
                bundle.Callers,
                caller => Assert.Equal(bundle.Definition.Path, caller.Path)));

        var parentAnalysis = _reader.AnalyzeSymbol(
            "Microsoft.Data.Sqlite",
            limit: 10,
            lang: "dependency_lock",
            exact: true);
        var parentBundles = Assert.IsType<List<CodeIndex.Database.SymbolCandidateBundle>>(
            parentAnalysis.CandidateBundles);
        Assert.Equal(4, parentBundles.Count);

        AssertCallee(parentBundles, "src/A/packages.lock.json", "net8.0", definitionLine: 5, calleeLine: 9);
        AssertCallee(parentBundles, "src/A/packages.lock.json", "net8.0/win-x64", definitionLine: 18, calleeLine: 22);
        AssertCallee(parentBundles, "src/A/packages.lock.json", "net8.0/linux-x64", definitionLine: 31, calleeLine: 35);
        AssertCallee(parentBundles, "src/B/packages.lock.json", "net8.0", definitionLine: 5, calleeLine: 9);
    }

    [Fact]
    public void AnalyzeSymbol_DependencyLockDoesNotResolveNpmDependenciesAcrossFiles_Issue4845()
    {
        InsertIndexedFile("src/npm-a/package-lock.json", "dependency_lock", """
            {
              "lockfileVersion": 3,
              "packages": {
                "node_modules/left-pad": {
                  "version": "1.3.0",
                  "dependencies": {
                    "is-number": "7.0.0"
                  }
                }
              }
            }
            """);
        InsertIndexedFile("src/npm-b/package-lock.json", "dependency_lock", """
            {
              "lockfileVersion": 3,
              "packages": {
                "node_modules/is-number": {
                  "version": "7.0.0"
                },
                "node_modules/right-pad": {
                  "version": "1.0.1",
                  "dependencies": {
                    "is-number": "7.0.0"
                  }
                }
              }
            }
            """);

        var analysis = _reader.AnalyzeSymbol(
            "is-number",
            limit: 10,
            lang: "dependency_lock",
            exact: true);
        var bundle = Assert.Single(
            Assert.IsType<List<CodeIndex.Database.SymbolCandidateBundle>>(
                analysis.CandidateBundles));

        Assert.Equal("src/npm-b/package-lock.json", bundle.Definition.Path);
        var caller = Assert.Single(bundle.Callers);
        Assert.Equal("src/npm-b/package-lock.json", caller.Path);
        Assert.Equal("right-pad", caller.CallerName);
        Assert.Equal(10, caller.FirstLine);
        Assert.Equal(1, caller.ReferenceCount);
    }

    private static void AssertCallee(
        List<CodeIndex.Database.SymbolCandidateBundle> bundles,
        string path,
        string target,
        int definitionLine,
        int calleeLine)
    {
        var bundle = Assert.Single(
            bundles,
            candidate => candidate.Definition.Path == path
                && candidate.Definition.ContainerName == target);
        Assert.Equal(definitionLine, bundle.Definition.Line);
        var callee = Assert.Single(bundle.Callees);
        Assert.Equal(path, callee.Path);
        Assert.Equal("Microsoft.Data.Sqlite.Core", callee.CalleeName);
        Assert.Equal(calleeLine, callee.FirstLine);
    }
}
