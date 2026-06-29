using System.Reflection;
using System.Text;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void GetSymbolHotspots_RanksRealCallsAboveManyLowerWeightSubscribeEdges()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/hotspot_weights.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 20,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "RealCallTarget",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
                BodyStartLine = 2,
                BodyEndLine = 3,
                Signature = "public void RealCallTarget()",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "SubscribeOnlyTarget",
                Line = 5,
                StartLine = 5,
                EndLine = 7,
                BodyStartLine = 6,
                BodyEndLine = 7,
                Signature = "public void SubscribeOnlyTarget()",
            },
        ]);

        var references = new List<ReferenceRecord>();
        for (var i = 0; i < 2; i++)
        {
            references.Add(new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "RealCallTarget",
                ReferenceKind = "call",
                Line = 10 + i,
                Column = 9,
                Context = "RealCallTarget();",
            });
        }
        for (var i = 0; i < 5; i++)
        {
            references.Add(new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "SubscribeOnlyTarget",
                ReferenceKind = "subscribe",
                Line = 14 + i,
                Column = 9,
                Context = "SubscribeOnlyTarget += Handler;",
            });
        }
        _writer.InsertReferences(references);

        var hotspots = _reader.GetSymbolHotspots(limit: 2, kind: "function", lang: "csharp", pathPatterns: null, excludePathPatterns: null, excludeTests: false);

        Assert.Equal("RealCallTarget", hotspots[0].Symbol.Name);
        Assert.Equal(2, hotspots[0].ReferenceCount);
        Assert.Equal(2.0, hotspots[0].ReferenceScore);
        Assert.Equal("SubscribeOnlyTarget", hotspots[1].Symbol.Name);
        Assert.Equal(5, hotspots[1].ReferenceCount);
        Assert.Equal(1.5, hotspots[1].ReferenceScore, precision: 6);
    }

    [Fact]
    public void GetSymbolHotspots_DemotesGenericNamesInRankingDiagnostics()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/generic_hotspot_rank.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 30,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Combine",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                BodyStartLine = 1,
                BodyEndLine = 1,
                Signature = "public void Combine()",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "ProcessInvoiceWorkflow",
                Line = 3,
                StartLine = 3,
                EndLine = 12,
                BodyStartLine = 4,
                BodyEndLine = 12,
                Signature = "public void ProcessInvoiceWorkflow()",
            },
        ]);

        var references = new List<ReferenceRecord>();
        for (var i = 0; i < 8; i++)
        {
            references.Add(new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Combine",
                ReferenceKind = "call",
                Line = 15 + i,
                Column = 9,
                Context = "Combine();",
            });
        }
        for (var i = 0; i < 3; i++)
        {
            references.Add(new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "ProcessInvoiceWorkflow",
                ReferenceKind = "call",
                Line = 24 + i,
                Column = 9,
                Context = "ProcessInvoiceWorkflow();",
            });
        }
        _writer.InsertReferences(references);

        var hotspots = _reader.GetSymbolHotspots(limit: 2, kind: "function", lang: "csharp", pathPatterns: null, excludePathPatterns: null, excludeTests: false);

        Assert.Equal("ProcessInvoiceWorkflow", hotspots[0].Symbol.Name);
        Assert.Equal(3, hotspots[0].ReferenceCount);
        Assert.Equal(3.0, hotspots[0].RankingScore, precision: 6);
        var combine = Assert.Single(hotspots, item => item.Symbol.Name == "Combine");
        Assert.Equal(8, combine.ReferenceCount);
        Assert.Equal(8.0, combine.ReferenceScore, precision: 6);
        Assert.Equal(0.35, combine.GenericNamePenalty, precision: 6);
        Assert.Equal(2.8, combine.RankingScore, precision: 6);

        var grouped = _reader.GetGroupedSymbolHotspots(limit: 2, kind: "function", lang: "csharp", pathPatterns: null, excludePathPatterns: null, excludeTests: false);
        Assert.Equal("ProcessInvoiceWorkflow", grouped[0].Symbol.Name);
        var groupedCombine = Assert.Single(grouped, item => item.Symbol.Name == "Combine");
        Assert.Equal(0.35, groupedCombine.GenericNamePenalty, precision: 6);
        Assert.Equal(2.8, groupedCombine.RankingScore, precision: 6);

        var complexity = _reader.SearchSymbols(
            queries: null,
            limit: 2,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/*generic_hotspot_rank*"],
            excludePathPatterns: null,
            excludeTests: false,
            sortMode: SymbolSortMode.Complexity);

        Assert.Equal("ProcessInvoiceWorkflow", complexity[0].Name);
        Assert.Equal("Combine", complexity[1].Name);
        Assert.True(complexity[0].ComplexityScore > complexity[1].ComplexityScore);
    }

    [Fact]
    public void GetSymbolHotspots_BreaksEqualCountsByPathLineNameKind()
    {
        var betaFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/z_hotspot_tie.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var alphaFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/a_hotspot_tie.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertSymbols(
        [
            new SymbolRecord { FileId = betaFileId, Kind = "function", Name = "BetaTieTarget", Line = 5, StartLine = 5, EndLine = 7, BodyStartLine = 6, BodyEndLine = 7, Signature = "void BetaTieTarget()" },
            new SymbolRecord { FileId = alphaFileId, Kind = "function", Name = "AlphaTieTarget", Line = 5, StartLine = 5, EndLine = 7, BodyStartLine = 6, BodyEndLine = 7, Signature = "void AlphaTieTarget()" },
        ]);
        _writer.InsertReferences(
        [
            new ReferenceRecord { FileId = betaFileId, SymbolName = "BetaTieTarget", ReferenceKind = "call", Line = 8, Column = 9, Context = "BetaTieTarget();" },
            new ReferenceRecord { FileId = alphaFileId, SymbolName = "AlphaTieTarget", ReferenceKind = "call", Line = 8, Column = 9, Context = "AlphaTieTarget();" },
        ]);

        var hotspots = _reader.GetSymbolHotspots(limit: 10, kind: "function", lang: "csharp", pathPatterns: ["src/a_hotspot_tie.cs", "src/z_hotspot_tie.cs"], excludePathPatterns: null, excludeTests: false)
            .Where(item => item.Symbol.Name.EndsWith("TieTarget", StringComparison.Ordinal))
            .ToList();

        Assert.Collection(hotspots,
            first => Assert.Equal("src/a_hotspot_tie.cs", first.Symbol.Path),
            second => Assert.Equal("src/z_hotspot_tie.cs", second.Symbol.Path));
    }

    [Fact]
    public void GetSymbolHotspots_CountsSameNameReferencesPerSymbolFile()
    {
        InsertIndexedFile("src/hotspots_alpha.py", "python",
            "def Shared():\n    return True\n\n" +
            "def alpha_use():\n    Shared()\n    Shared()\n");
        InsertIndexedFile("src/hotspots_beta.py", "python",
            "def Shared():\n    return True\n\n" +
            "def beta_use():\n    Shared()\n");
        InsertIndexedFile("src/hotspots_gamma.py", "python",
            "def gamma_use():\n    Shared()\n");

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/hotspots_alpha.py", "src/hotspots_beta.py"],
            excludePathPatterns: null,
            excludeTests: false);

        var shared = results
            .Where(result => result.Symbol.Name == "Shared")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Collection(shared,
            alpha =>
            {
                Assert.Equal("src/hotspots_alpha.py", alpha.Symbol.Path);
                Assert.Equal(2, alpha.ReferenceCount);
            },
            beta =>
            {
                Assert.Equal("src/hotspots_beta.py", beta.Symbol.Path);
                Assert.Equal(1, beta.ReferenceCount);
            });
    }

    [Fact]
    public void GetSymbolHotspots_PathFilterStillTreatsOutOfScopeDuplicateAsAmbiguous()
    {
        InsertIndexedFile("src/hotspots_alpha.py", "python",
            "def Shared():\n    return True\n\n" +
            "def alpha_use():\n    Shared()\n");
        InsertIndexedFile("src/hotspots_beta.py", "python",
            "def Shared():\n    return True\n\n" +
            "def beta_use():\n    Shared()\n    Shared()\n");
        InsertIndexedFile("src/hotspots_gamma.py", "python",
            "def gamma_use():\n    Shared()\n");

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/hotspots_alpha.py"],
            excludePathPatterns: null,
            excludeTests: false);

        var shared = Assert.Single(results.Where(result => result.Symbol.Name == "Shared"));
        Assert.Equal("src/hotspots_alpha.py", shared.Symbol.Path);
        Assert.Equal(1, shared.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_CountsCrossFileReferencesForUniqueName()
    {
        InsertIndexedFile("src/api.py", "python", "def SharedApi():\n    return True\n");
        InsertIndexedFile("src/use1.py", "python",
            "def use_one():\n    SharedApi()\n    SharedApi()\n");
        InsertIndexedFile("src/use2.py", "python",
            "def use_two():\n    SharedApi()\n");

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var sharedApi = Assert.Single(results.Where(result => result.Symbol.Name == "SharedApi"));
        Assert.Equal("src/api.py", sharedApi.Symbol.Path);
        Assert.Equal(3, sharedApi.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_KeepsCrossFileCountsForSameContainerOverloadFamily()
    {
        InsertIndexedFile("src/api.cs", "csharp",
            """
            public class Api
            {
                public void Run() { }
                public void Run(int value) { }
            }
            """);
        InsertIndexedFile("src/caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(Api api)
                {
                    api.Run();
                    api.Run(1);
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run"));
        Assert.Equal("src/api.cs", run.Symbol.Path);
        Assert.Equal("Api", run.Symbol.ContainerName);
        Assert.Equal(2, run.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_CSharpSkipsBodylessCallSiteFunctionCandidates()
    {
        InsertIndexedFile("src/reader.cs", "csharp",
            """
            public class Reader
            {
                public T Identity<T>(T value)
                {
                    return value;
                }

                public void Load(Microsoft.Data.Sqlite.SqliteDataReader reader)
                {
                    var first = reader.GetInt32(0);
                    var second = reader.GetInt32(1);
                    var max = Math.Max(
                        first,
                        second);
                    _ = Identity(max);
                }
            }

            public class App
            {
                public void Run(Reader reader, Microsoft.Data.Sqlite.SqliteDataReader dataReader)
                {
                    reader.Load(dataReader);
                    reader.Load(dataReader);
                }
            }

            public interface IService
            {
                void Execute();
            }

            public class ServiceConsumer
            {
                public void Run(IService service)
                {
                    service.Execute();
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/reader.cs"],
            excludePathPatterns: null,
            excludeTests: false);

        Assert.DoesNotContain(results, result => result.Symbol.Name == "GetInt32");
        Assert.DoesNotContain(results, result => result.Symbol.Name == "Max");
        var load = Assert.Single(results.Where(result => result.Symbol.Name == "Load"));
        Assert.Equal(2, load.ReferenceCount);
        var identity = Assert.Single(results.Where(result => result.Symbol.Name == "Identity"));
        Assert.Equal(1, identity.ReferenceCount);
        var execute = Assert.Single(results.Where(result => result.Symbol.Name == "Execute"));
        Assert.Equal(1, execute.ReferenceCount);

        var groupedResults = _reader.GetGroupedSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/reader.cs"],
            excludePathPatterns: null,
            excludeTests: false);

        Assert.DoesNotContain(groupedResults, result => result.Symbol.Name == "GetInt32");
        Assert.DoesNotContain(groupedResults, result => result.Symbol.Name == "Max");
        var groupedLoad = Assert.Single(groupedResults.Where(result => result.Symbol.Name == "Load"));
        Assert.Equal(2, groupedLoad.ReferenceCount);
        var groupedIdentity = Assert.Single(groupedResults.Where(result => result.Symbol.Name == "Identity"));
        Assert.Equal(1, groupedIdentity.ReferenceCount);
        var groupedExecute = Assert.Single(groupedResults.Where(result => result.Symbol.Name == "Execute"));
        Assert.Equal(1, groupedExecute.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_KeepsCrossFileCountsForPartialClassOverloadFamily()
    {
        InsertIndexedFile("src/Api.Part1.cs", "csharp",
            """
            public partial class Api
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Api.Part2.cs", "csharp",
            """
            public partial class Api
            {
                public void Run(int value) { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(Api api)
                {
                    api.Run();
                    api.Run(1);
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run"));
        Assert.Equal("Api", run.Symbol.ContainerName);
        Assert.Equal(2, run.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_SeparatesSameSimpleContainerNamesAcrossNamespaces()
    {
        InsertIndexedFile("src/One.Api.cs", "csharp",
            """
            namespace One;

            public class Api
            {
                public void Run() { }

                public void LocalOne()
                {
                    Run();
                }
            }
            """);
        InsertIndexedFile("src/Two.Api.cs", "csharp",
            """
            namespace Two;

            public class Api
            {
                public void Run() { }

                public void LocalTwo()
                {
                    Run();
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var runs = results
            .Where(result => result.Symbol.Name == "Run")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Collection(runs,
            first =>
            {
                Assert.Equal("src/One.Api.cs", first.Symbol.Path);
                Assert.Equal("Api", first.Symbol.ContainerName);
                Assert.Equal(1, first.ReferenceCount);
            },
            second =>
            {
                Assert.Equal("src/Two.Api.cs", second.Symbol.Path);
                Assert.Equal("Api", second.Symbol.ContainerName);
                Assert.Equal(1, second.ReferenceCount);
            });
    }

    [Fact]
    public void GetSymbolHotspots_DoesNotMergeSameContainerNameAcrossPythonModules()
    {
        InsertIndexedFile("src/alpha.py", "python",
            """
            class Api:
                def Run(self):
                    return True

                def Use(self):
                    self.Run()
                    self.Run()
            """);
        InsertIndexedFile("src/beta.py", "python",
            """
            class Api:
                def Run(self):
                    return True

                def Use(self):
                    self.Run()
                    self.Run()
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var runs = results
            .Where(result => result.Symbol.Name == "Run")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Collection(runs,
            first =>
            {
                Assert.Equal("src/alpha.py", first.Symbol.Path);
                Assert.Equal("Api", first.Symbol.ContainerName);
                Assert.Equal(2, first.ReferenceCount);
            },
            second =>
            {
                Assert.Equal("src/beta.py", second.Symbol.Path);
                Assert.Equal("Api", second.Symbol.ContainerName);
                Assert.Equal(2, second.ReferenceCount);
            });
    }

    [Fact]
    public void GetSymbolHotspots_DoesNotMergeSameQualifiedTypeAcrossProjectRoots()
    {
        InsertIndexedFile("projA/src/Api.cs", "csharp",
            """
            namespace Shared;

            public class Api
            {
                public void Run() { }

                public void LocalA()
                {
                    Run();
                }
            }
            """);
        InsertIndexedFile("projB/src/Api.cs", "csharp",
            """
            namespace Shared;

            public class Api
            {
                public void Run() { }

                public void LocalB()
                {
                    Run();
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["projA/", "projB/"],
            excludePathPatterns: null,
            excludeTests: false);

        var runs = results
            .Where(result => result.Symbol.Name == "Run")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Collection(runs,
            first =>
            {
                Assert.Equal("projA/src/Api.cs", first.Symbol.Path);
                Assert.Equal("Api", first.Symbol.ContainerName);
                Assert.Equal(1, first.ReferenceCount);
            },
            second =>
            {
                Assert.Equal("projB/src/Api.cs", second.Symbol.Path);
                Assert.Equal("Api", second.Symbol.ContainerName);
                Assert.Equal(1, second.ReferenceCount);
            });
    }

    [Fact]
    public void GetSymbolHotspots_CSharpPropertyDoesNotUseRepoWideBareNameCounts()
    {
        InsertIndexedFile("src/Diff.cs", "csharp",
            """
            public sealed record OrderedRowsDiff(bool Equal);

            public class DiffUse
            {
                public bool Use(OrderedRowsDiff diff)
                {
                    return diff.Equal;
                }
            }
            """);
        InsertIndexedFile("src/Unrelated.cs", "csharp",
            """
            public class Unrelated
            {
                public bool Run()
                {
                    var equal = true;
                    equal = equal && true;
                    return equal;
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "property",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        Assert.DoesNotContain(results, result => result.Symbol.Name == "Equal");
    }

    [Fact]
    public void GetSymbolHotspots_DoesNotMergePartialFamiliesAcrossProjectRoots()
    {
        InsertIndexedFile("projA/src/Api.Part1.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run()
                {
                    Run(1);
                }
            }
            """,
            familyScopeKey: "projA");
        InsertIndexedFile("projA/src/Api.Part2.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run(int value) { }
            }
            """,
            familyScopeKey: "projA");
        InsertIndexedFile("projB/src/Api.Part1.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run()
                {
                    Run(1);
                }
            }
            """,
            familyScopeKey: "projB");
        InsertIndexedFile("projB/src/Api.Part2.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run(int value) { }
            }
            """,
            familyScopeKey: "projB");

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["projA/", "projB/"],
            excludePathPatterns: null,
            excludeTests: false);

        var runs = results
            .Where(result => result.Symbol.Name == "Run")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Collection(runs,
            first =>
            {
                Assert.Equal("projA/src/Api.Part1.cs", first.Symbol.Path);
                Assert.Equal("Api", first.Symbol.ContainerName);
                Assert.Equal(1, first.ReferenceCount);
            },
            second =>
            {
                Assert.Equal("projB/src/Api.Part1.cs", second.Symbol.Path);
                Assert.Equal("Api", second.Symbol.ContainerName);
                Assert.Equal(1, second.ReferenceCount);
            });
    }

    [Fact]
    public void GetSymbolHotspots_KeepsCrossFileCountsForVbPartialClassFamily()
    {
        InsertIndexedFile("src/Api.Part1.vb", "vb",
            """
            Public Partial Class Api
                Public Sub Run()
                End Sub
            End Class
            """);
        InsertIndexedFile("src/Api.Part2.vb", "vb",
            """
            Public Partial Class Api
                Public Sub Run(value As Integer)
                End Sub
            End Class
            """);
        InsertIndexedFile("src/Caller.vb", "vb",
            """
            Public Class Caller
                Public Sub Call(api As Api)
                    api.Run()
                    api.Run(1)
                End Sub
            End Class
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "vb",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run"));
        Assert.Equal("Api", run.Symbol.ContainerName);
        Assert.Equal(2, run.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_GroupedFamilyReturnsRealDefinitionLocation()
    {
        InsertIndexedFile("src/APart.cs", "csharp",
            """
            public partial class Api
            {
                public void Helper()
                {
                }

                public void Run()
                {
                }
            }
            """);
        InsertIndexedFile("src/BPart.cs", "csharp",
            """
            public partial class Api
            {
                public void Run(int value)
                {
                }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(Api api)
                {
                    api.Run();
                    api.Run(1);
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run"));
        Assert.Equal(2, run.ReferenceCount);

        var definitions = _reader.SearchSymbols(
            "Run",
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false,
            exact: true);

        Assert.Contains(definitions, definition =>
            definition.Path == run.Symbol.Path &&
            definition.Line == run.Symbol.Line &&
            definition.Name == run.Symbol.Name);
    }

    [Fact]
    public void GetSymbolHotspots_CollapsesSameFileDuplicateNames()
    {
        InsertIndexedFile("src/duplicate_names.py", "python",
            "def Run():\n    return True\n\n" +
            "def Run(value=None):\n    return value\n\n" +
            "def caller():\n    Run()\n");

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/duplicate_names.py"],
            excludePathPatterns: null,
            excludeTests: false);

        var runResults = results
            .Where(result => result.Symbol.Name == "Run")
            .ToList();

        var run = Assert.Single(runResults);
        Assert.Equal("src/duplicate_names.py", run.Symbol.Path);
        Assert.Equal(1, run.ReferenceCount);
        Assert.Equal(1, run.Symbol.Line);
    }

    [Fact]
    public void GetSymbolHotspots_WithoutHotspotFamilyReadyFallsBackForMixedPartialFamilies()
    {
        InsertIndexedFile("src/Api.Part1.cs", "csharp",
            """
            public partial class Api
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Api.Part2.cs", "csharp",
            """
            public partial class Api
            {
                public void Run(int value) { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(Api api)
                {
                    api.Run();
                    api.Run(1);
                }
            }
            """);

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE symbols
                SET family_key = NULL
                WHERE file_id IN (
                    SELECT id FROM files WHERE path = 'src/Api.Part2.cs'
                )
                """;
            cmd.ExecuteNonQuery();
        }
        _writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);

        var reader = new DbReader(_db.Connection);
        var results = reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        Assert.DoesNotContain(results, result => result.Symbol.Name == "Run");
    }

    [Fact]
    public void GetSymbolHotspots_StaleVersionOneMarkerlessFamiliesDegradeAndDoNotMerge()
    {
        InsertIndexedFile("projA/src/Api.Part1.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run()
                {
                    Run(1);
                }
            }
            """,
            familyScopeKey: ".");
        InsertIndexedFile("projA/src/Api.Part2.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run(int value) { }
            }
            """,
            familyScopeKey: ".");
        InsertIndexedFile("projB/src/Api.Part1.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run()
                {
                    Run(1);
                }
            }
            """,
            familyScopeKey: ".");
        InsertIndexedFile("projB/src/Api.Part2.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run(int value) { }
            }
            """,
            familyScopeKey: ".");

        _writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), "1");
        _writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), "stale-v1");

        var reader = new DbReader(_db.Connection);
        var signal = reader.GetHotspotFamilySignal("csharp");
        Assert.True(signal.Relevant);
        Assert.False(signal.Ready);
        Assert.Contains("csharp", signal.DegradedReason);

        var runs = reader.GetSymbolHotspots(
                limit: 10,
                kind: "function",
                lang: "csharp",
                pathPatterns: ["projA/", "projB/"],
                excludePathPatterns: null,
                excludeTests: false)
            .Where(result => result.Symbol.Name == "Run")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, runs.Count);
        Assert.StartsWith("projA/src/Api.Part", runs[0].Symbol.Path, StringComparison.Ordinal);
        Assert.Equal(1, runs[0].ReferenceCount);
        Assert.StartsWith("projB/src/Api.Part", runs[1].Symbol.Path, StringComparison.Ordinal);
        Assert.Equal(1, runs[1].ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_DoesNotPromoteSameFileDifferentContainersToGlobalCounts()
    {
        InsertIndexedFile("src/Duplicate.cs", "csharp",
            """
            public class A
            {
                public void Run() { }
            }

            public class B
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(A api)
                {
                    api.Run();
                    api.Run();
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        Assert.DoesNotContain(results, result => result.Symbol.Name == "Run");
    }

    [Fact]
    public void GetSymbolHotspots_DoesNotCountAmbiguousSameFileSiblingContainerReferences()
    {
        InsertIndexedFile("src/Duplicate.cs", "csharp",
            """
            public class A
            {
                public void Run() { }

                public void CallA()
                {
                    Run();
                }
            }

            public class B
            {
                public void Run() { }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        Assert.DoesNotContain(results, result => result.Symbol.Name == "Run");
    }

    [Fact]
    public void GetSymbolHotspots_LangFilterIgnoresCrossLanguageReferences()
    {
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(App app)
                {
                    app.Run();
                    app.Run();
                }
            }
            """);
        InsertIndexedFile("src/tool.py", "python",
            """
            def helper():
                Run()
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run"));
        Assert.Equal("src/App.cs", run.Symbol.Path);
        Assert.Equal(2, run.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_CrossLanguageDefinitionsDoNotSuppressSameLanguageHotspots()
    {
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(App app)
                {
                    app.Run();
                    app.Run();
                }
            }
            """);
        InsertIndexedFile("src/tool.py", "python",
            """
            def Run():
                return True
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: null,
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run" && result.Symbol.Lang == "csharp"));
        Assert.Equal("src/App.cs", run.Symbol.Path);
        Assert.Equal(2, run.ReferenceCount);
    }

    [Fact]
    public void GetUnusedSymbols_ClassifiesConfidenceBucketsAndSortsPrivateFirst()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/config/unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 20,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Hidden",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "ExportedApi",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "ExportedApi",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "PathResolver",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = "public class PathResolver",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "AdoptionService",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public class AdoptionService",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "TokenService",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public class TokenService",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "AppSettings",
                Line = 9,
                StartLine = 9,
                EndLine = 11,
                Signature = "public class AppSettings",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "ApplyConfiguration",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public void ApplyConfiguration()",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "UseIOptions",
                Line = 13,
                StartLine = 13,
                EndLine = 13,
                Signature = "public void UseIOptions()",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ConnectionString",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string ConnectionString { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/config/unused_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal(["Hidden", "InternalOnly", "PathResolver", "ConnectionString", "AdoptionService", "AppSettings", "TokenService", "ApplyConfiguration", "UseIOptions"], unused.Select(symbol => symbol.Name).ToArray());
        Assert.Equal("likely_unused_private", unused[0].UnusedBucket);
        Assert.Equal("medium", unused[0].UnusedConfidence);
        Assert.Equal("maybe_unused_nonpublic", unused[1].UnusedBucket);
        Assert.Equal("low", unused[1].UnusedConfidence);
        Assert.Equal("public_or_exported_no_refs", unused[2].UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", unused[3].UnusedBucket);
        Assert.Contains("serialization, config", unused[3].UnusedReason);
        Assert.Equal("public_or_exported_no_refs", unused[4].UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", unused[5].UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", unused[6].UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", unused[7].UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", unused[8].UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "PathResolver").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "AdoptionService").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "TokenService").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "AppSettings").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "ApplyConfiguration").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "UseIOptions").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_ClassifiesContractSurfacesAsLowConfidence_Issue3902()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/output_models.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 40,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "SearchResponseDto",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "record SearchResponseDto(string Path)",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "SearchJsonContext",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "internal partial class SearchJsonContext : JsonSerializerContext",
                Visibility = "internal",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(
            limit: 10,
            kind: null,
            lang: "csharp",
            pathPatterns: ["src/*output_models.cs*"],
            excludePathPatterns: null,
            excludeTests: false,
            bucketFilter: "reflection_or_config_suspect");

        var dto = Assert.Single(unused, symbol => symbol.Name == "SearchResponseDto");
        Assert.Equal("reflection_or_config_suspect", dto.UnusedBucket);
        Assert.Contains("serialization_contract", dto.UnusedReasonTags);

        var jsonContext = Assert.Single(unused, symbol => symbol.Name == "SearchJsonContext");
        Assert.Equal("reflection_or_config_suspect", jsonContext.UnusedBucket);
        Assert.Contains("source_generated_json_context", jsonContext.UnusedReasonTags);
    }

    [Fact]
    public void GetUnusedSymbols_ClassifiesTestHooksAndMetadataAsLowConfidence_Issue3953()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/metadata_hooks.cs",
            Lang = "csharp",
            Size = 160,
            Lines = 20,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "ResetForTests",
                Line = 9,
                StartLine = 9,
                EndLine = 9,
                Signature = "internal void ResetForTests()",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "SearchResponseDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "CharactersRead",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public int CharactersRead { get; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "ParseException",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(
            limit: 10,
            kind: null,
            lang: "csharp",
            pathPatterns: ["src/*metadata_hooks.cs*"],
            excludePathPatterns: null,
            excludeTests: false,
            bucketFilter: "reflection_or_config_suspect");

        var testHook = Assert.Single(unused, symbol => symbol.Name == "ResetForTests");
        Assert.Equal("reflection_or_config_suspect", testHook.UnusedBucket);
        Assert.Contains("test_hook", testHook.UnusedReasonTags);

        var exceptionMetadata = Assert.Single(unused, symbol => symbol.Name == "CharactersRead");
        Assert.Equal("reflection_or_config_suspect", exceptionMetadata.UnusedBucket);
        Assert.Contains("exception_metadata", exceptionMetadata.UnusedReasonTags);
    }

    [Fact]
    public void GetUnusedSymbols_NonSqlScope_FiltersReferencedPrivateSymbolsBeforeLimit()
    {
        const string path = "src/fast_unused_limit_fixture.cs";
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = "csharp",
            Size = 4096,
            Lines = 80,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var content = new StringBuilder();
        var symbols = new List<SymbolRecord>();
        var references = new List<ReferenceRecord>();
        for (var i = 0; i < 64; i++)
        {
            var name = $"UsedBeforeLimit{i:D2}";
            var line = i + 1;
            content.AppendLine($"    private void {name}() {{ }}");
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = name,
                Line = line,
                StartLine = line,
                EndLine = line,
                Signature = $"private void {name}() {{ }}",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "FastUnusedLimitFixture",
            });
            references.Add(new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = name,
                ReferenceKind = "call",
                Line = line,
                Column = 17,
                Context = $"{name}();",
                ContainerKind = "class",
                ContainerName = "FastUnusedLimitFixture",
            });
        }

        content.AppendLine("    private void HiddenUnusedAfterReferencedPrefix() { }");
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            Name = "HiddenUnusedAfterReferencedPrefix",
            Line = 65,
            StartLine = 65,
            EndLine = 65,
            Signature = "private void HiddenUnusedAfterReferencedPrefix() { }",
            Visibility = "private",
            ContainerKind = "class",
            ContainerName = "FastUnusedLimitFixture",
        });

        _writer.InsertChunks([
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 65,
                Content = content.ToString(),
            }
        ]);
        _writer.InsertSymbols(symbols);
        _writer.InsertReferences(references);

        var unused = _reader.GetUnusedSymbols(limit: 1, kind: null, lang: "csharp",
            pathPatterns: ["src/*fast_unused_limit_fixture.cs*"], excludePathPatterns: null, excludeTests: false);
        var count = _reader.CountUnusedSymbols(kind: null, lang: "csharp",
            pathPatterns: ["src/*fast_unused_limit_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        var result = Assert.Single(unused);
        Assert.Equal("HiddenUnusedAfterReferencedPrefix", result.Name);
        Assert.Equal("likely_unused_private", result.UnusedBucket);
        Assert.Equal(1, count.Count);
        Assert.Equal(1, count.FileCount);
    }

    [Fact]
    public void GetUnusedSymbols_PlainCliOptionsProperties_StayInPublicBucket()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/cli_options_fixture.cs",
            Lang = "csharp",
            Size = 160,
            Lines = 6,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "CliOptions",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public sealed class CliOptions",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ShowHelp",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "public bool ShowHelp { get; init; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "CliOptions",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ProjectPath",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "public string? ProjectPath { get; init; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "CliOptions",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*cli_options_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "ShowHelp").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "ProjectPath").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_PrivateHelperWithSameFileUse_IsNotReported()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/local_use_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 20,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 13,
                Content = """""
                public class LocalUseFixture
                {
                    public void Run() { Hidden(); }
                    public void RunInterpolated() { _ = $"{HiddenInterpolated()}"; }
                    public void RunRawInterpolated() { _ = $"""{RawInterpolated()}"""; }
                    private void Hidden() { }
                    private void HiddenInterpolated() { }
                    private void RawInterpolated() { }
                    // CommentOnly is not a real use.
                    private void CommentOnly() { }
                    private void StringOnly() { _ = "StringOnly"; }
                    private void RawStringOnly() { _ = """RawStringOnly"""; }
                }
                """"",
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Run",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "public void Run() { Hidden(); }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "HiddenInterpolated",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "private void HiddenInterpolated() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "RawInterpolated",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "private void RawInterpolated() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "CommentOnly",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "private void CommentOnly() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "StringOnly",
                Line = 11,
                StartLine = 11,
                EndLine = 11,
                Signature = "private void StringOnly() { _ = \"StringOnly\"; }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "RawStringOnly",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "private void RawStringOnly() { _ = \"\"\"RawStringOnly\"\"\"; }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Hidden",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*local_use_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.DoesNotContain(unused, symbol => symbol.Name == "Hidden");
        Assert.DoesNotContain(unused, symbol => symbol.Name == "HiddenInterpolated");
        Assert.DoesNotContain(unused, symbol => symbol.Name == "RawInterpolated");
        Assert.Contains(unused, symbol => symbol.Name == "CommentOnly");
        Assert.Contains(unused, symbol => symbol.Name == "StringOnly");
        Assert.Contains(unused, symbol => symbol.Name == "RawStringOnly");
    }

    [Fact]
    public void GetUnusedSymbols_PrivateConstUseAfterRawStringChunkBoundary_IsNotReported()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/chunked_raw_string_fixture.cs",
            Lang = "csharp",
            Size = 512,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 5,
                Content = """"
                public class ChunkedRawStringFixture
                {
                    private const string UsedRowsSql = "SELECT 1";
                    private const string FillerSql = """
                        SELECT
                """",
            },
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 1,
                StartLine = 5,
                EndLine = 10,
                Content = """"
                        SELECT
                            value
                        """;
                    public void Compare() { _ = UsedRowsSql; }
                    private const string ActuallyUnused = "unused";
                }
                """",
            },
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "UsedRowsSql",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "private const string UsedRowsSql = \"SELECT 1\";",
                Visibility = "private",
                ReturnType = "string",
                ContainerKind = "class",
                ContainerName = "ChunkedRawStringFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "ActuallyUnused",
                Line = 9,
                StartLine = 9,
                EndLine = 9,
                Signature = "private const string ActuallyUnused = \"unused\";",
                Visibility = "private",
                ReturnType = "string",
                ContainerKind = "class",
                ContainerName = "ChunkedRawStringFixture",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*chunked_raw_string_fixture.cs*"], excludePathPatterns: null, excludeTests: false);
        var count = _reader.CountUnusedSymbols(kind: null, lang: "csharp",
            pathPatterns: ["src/*chunked_raw_string_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.DoesNotContain(unused, symbol => symbol.Name == "UsedRowsSql");
        Assert.Contains(unused, symbol => symbol.Name == "ActuallyUnused");
        Assert.Equal(1, count.Count);
        Assert.Equal(1, count.FileCount);
    }

    [Fact]
    public void GetUnusedSymbols_ReflectionAttributedProperty_IsClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_unused_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "UserDto").UnusedBucket);
        var property = Assert.Single(unused, symbol => symbol.Name == "FullName");
        Assert.Equal("reflection_or_config_suspect", property.UnusedBucket);
        Assert.Contains("attribute-driven reflection surface", property.UnusedReason);
    }

    [Fact]
    public void GetUnusedSymbols_ReflectionAttributedTypes_AreClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_type_fixture.cs",
            Lang = "csharp",
            Size = 520,
            Lines = 16,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 15,
                Content = """
                using System;
                using System.Text.Json.Serialization;
                using System.ComponentModel.DataAnnotations.Schema;

                [Serializable]
                public class ReflectiveModel { }

                [JsonSerializable(typeof(ApiResponse))]
                public partial class MyJsonContext : JsonSerializerContext { }

                [Table("users")]
                public class UserEntity { }

                [AttributeUsage(AttributeTargets.Class)]
                public class ReflectiveAttribute : Attribute { }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "ReflectiveModel",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public class ReflectiveModel { }",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "MyJsonContext",
                Line = 9,
                StartLine = 9,
                EndLine = 9,
                Signature = "public partial class MyJsonContext : JsonSerializerContext { }",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserEntity",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public class UserEntity { }",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "ReflectiveAttribute",
                Line = 15,
                StartLine = 15,
                EndLine = 15,
                Signature = "public class ReflectiveAttribute : Attribute { }",
                Visibility = "public",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_type_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "ReflectiveModel").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "MyJsonContext").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "UserEntity").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "ReflectiveAttribute").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_CommonReflectionPropertyAttributes_KeyAndRequired_AreClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_property_fixture.cs",
            Lang = "csharp",
            Size = 900,
            Lines = 29,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 29,
                Content = """
                using System;
                using System.ComponentModel.DataAnnotations;
                using Microsoft.AspNetCore.Components;
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.ModelBinding;

                public class Target
                {
                    [Key]
                    public int Id { get; set; }

                    [Required]
                    public string Name { get; set; } = string.Empty;

                    [BindProperty]
                    public string? BoundValue { get; set; }

                    [Parameter]
                    public string? Title { get; set; }

                    [Inject]
                    public IServiceProvider? Services { get; set; }

                    public string? LegacyName { get; set; }

                    [BindNever]
                    public string? IgnoredValue { get; set; }
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 7,
                StartLine = 7,
                EndLine = 29,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Id",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public int Id { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Name",
                Line = 13,
                StartLine = 13,
                EndLine = 13,
                Signature = "public string Name { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "BoundValue",
                Line = 16,
                StartLine = 16,
                EndLine = 16,
                Signature = "public string? BoundValue { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Title",
                Line = 19,
                StartLine = 19,
                EndLine = 19,
                Signature = "public string? Title { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Services",
                Line = 22,
                StartLine = 22,
                EndLine = 22,
                Signature = "public IServiceProvider? Services { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "LegacyName",
                Line = 25,
                StartLine = 25,
                EndLine = 25,
                Signature = "public string? LegacyName { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "IgnoredValue",
                Line = 28,
                StartLine = 28,
                EndLine = 28,
                Signature = "public string? IgnoredValue { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_property_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "Target").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "Id").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "Name").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_CommonReflectionPropertyAttributes_BindPropertyAndParameter_AreClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_property_fixture.cs",
            Lang = "csharp",
            Size = 900,
            Lines = 29,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 29,
                Content = """
                using System;
                using System.ComponentModel.DataAnnotations;
                using Microsoft.AspNetCore.Components;
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.ModelBinding;

                public class Target
                {
                    [Key]
                    public int Id { get; set; }

                    [Required]
                    public string Name { get; set; } = string.Empty;

                    [BindProperty]
                    public string? BoundValue { get; set; }

                    [Parameter]
                    public string? Title { get; set; }

                    [Inject]
                    public IServiceProvider? Services { get; set; }

                    [Obsolete]
                    public string? LegacyName { get; set; }

                    [BindNever]
                    public string? IgnoredValue { get; set; }
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 7,
                StartLine = 7,
                EndLine = 29,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "BoundValue",
                Line = 16,
                StartLine = 16,
                EndLine = 16,
                Signature = "public string? BoundValue { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Title",
                Line = 19,
                StartLine = 19,
                EndLine = 19,
                Signature = "public string? Title { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_property_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "BoundValue").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "Title").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_CommonReflectionPropertyAttributes_BindNever_AreClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_property_fixture.cs",
            Lang = "csharp",
            Size = 900,
            Lines = 29,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 29,
                Content = """
                using System;
                using System.ComponentModel.DataAnnotations;
                using Microsoft.AspNetCore.Components;
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.ModelBinding;

                public class Target
                {
                    [Key]
                    public int Id { get; set; }

                    [Required]
                    public string Name { get; set; } = string.Empty;

                    [BindProperty]
                    public string? BoundValue { get; set; }

                    [Parameter]
                    public string? Title { get; set; }

                    public IServiceProvider? Services { get; set; }

                    public string? LegacyName { get; set; }

                    [BindNever]
                    public string? IgnoredValue { get; set; }
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 7,
                StartLine = 7,
                EndLine = 27,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Services",
                Line = 21,
                StartLine = 21,
                EndLine = 21,
                Signature = "public IServiceProvider? Services { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "LegacyName",
                Line = 23,
                StartLine = 23,
                EndLine = 23,
                Signature = "public string? LegacyName { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "IgnoredValue",
                Line = 26,
                StartLine = 26,
                EndLine = 26,
                Signature = "public string? IgnoredValue { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_property_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "Services").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "LegacyName").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "IgnoredValue").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_SerializationAndReflectionContractAttributes_AreClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/serialization_reflection_contract_fixture.cs",
            Lang = "csharp",
            Size = 940,
            Lines = 26,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 26,
                Content = """
                using System;
                using System.Collections.Generic;
                using System.Diagnostics.CodeAnalysis;
                using System.Text.Json.Serialization;

                public class ContractDto
                {
                    [JsonExtensionData]
                    public Dictionary<string, object?> ExtensionData { get; set; } = new();

                    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
                    public Type? ReflectedType { get; set; }

                    [JsonInclude]
                    public string? IncludedField;

                    public string? PlainName { get; set; }

                    [JsonConstructor]
                    public ContractDto(string name) { }

                    [DynamicDependency(nameof(PlainMethod))]
                    public void PreservedMethod() { }

                    public void PlainMethod() { }
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "ContractDto",
                Line = 6,
                StartLine = 6,
                EndLine = 26,
                Signature = "public class ContractDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ExtensionData",
                Line = 9,
                StartLine = 9,
                EndLine = 9,
                Signature = "public Dictionary<string, object?> ExtensionData { get; set; } = new();",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "ContractDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ReflectedType",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public Type? ReflectedType { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "ContractDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "PlainName",
                Line = 17,
                StartLine = 17,
                EndLine = 17,
                Signature = "public string? PlainName { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "ContractDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "field",
                Name = "IncludedField",
                Line = 15,
                StartLine = 15,
                EndLine = 15,
                Signature = "public string? IncludedField;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "ContractDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "ContractDto",
                Line = 20,
                StartLine = 20,
                EndLine = 20,
                Signature = "public ContractDto(string name) { }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "ContractDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "PreservedMethod",
                Line = 23,
                StartLine = 23,
                EndLine = 23,
                Signature = "public void PreservedMethod() { }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "ContractDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "PlainMethod",
                Line = 25,
                StartLine = 25,
                EndLine = 25,
                Signature = "public void PlainMethod() { }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "ContractDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*serialization_reflection_contract_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "ExtensionData").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "ReflectedType").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "IncludedField").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "PlainName").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "ContractDto" && symbol.Kind == "function").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "PreservedMethod").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "PlainMethod").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_MultilineReflectionAttribute_IsClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_multiline_fixture.cs",
            Lang = "csharp",
            Size = 240,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 9,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName(
                        "full_name")]
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 7,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 7,
                StartLine = 5,
                EndLine = 7,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_multiline_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        var property = Assert.Single(unused, symbol => symbol.Name == "FullName");
        Assert.Equal("reflection_or_config_suspect", property.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_InlineAttributeWithBracketInString_DoesNotLeakToAdjacentProperty()
    {
        // Regression for #375 — `[` or `]` inside an attribute string argument must not
        // confuse the bracket-depth scanner. Without the fix, the adjacent plain property
        // below inherited the reflection attribute and flipped into the wrong bucket.
        // #375 回帰: 属性文字列引数内の `[` / `]` が bracket-depth スキャナを乱すと、
        // 直下の属性なしプロパティが誤って reflection 属性を継承して分類が狂う。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_string_bracket_fixture.cs",
            Lang = "csharp",
            Size = 320,
            Lines = 12,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Text.Json.Serialization;

                public class Target
                {
                    [JsonPropertyName("a[")] public string BuggyName { get; set; } = "";

                    public string PlainName { get; set; } = "";
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 8,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "BuggyName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "[JsonPropertyName(\"a[\")] public string BuggyName { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "PlainName",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public string PlainName { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_string_bracket_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        var buggy = Assert.Single(unused, symbol => symbol.Name == "BuggyName");
        Assert.Equal("reflection_or_config_suspect", buggy.UnusedBucket);

        var plain = Assert.Single(unused, symbol => symbol.Name == "PlainName");
        Assert.Equal("public_or_exported_no_refs", plain.UnusedBucket);
    }

    [Theory]
    [InlineData("[JsonPropertyName(\"a[\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName(\"a]b\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName(\"]\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName(@\"a[\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName(\"\"\"a[\"\"\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName($\"\"\"a[\"\"\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName($$\"\"\"a[\"\"\")] public string Name { get; set; } = \"\";")]
    public void GetUnusedSymbols_InlineReflectionAttributeWithBracketInString_IsStillSuspect(string anchor)
    {
        // The inline-declaration line itself must still be recognized as having
        // both an attribute and a declaration, regardless of `[` / `]` in string args.
        // 属性文字列中の `[` / `]` によらず、インライン宣言行自身は
        // 「属性 + 宣言が同じ行にある」と認識されねばならない。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = $"src/reflection_string_bracket_inline_{anchor.GetHashCode():x8}.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 8,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 7,
                Content = $$"""
                using System.Text.Json.Serialization;

                public class Target
                {
                    {{anchor}}
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Name",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = anchor,
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: [$"src/*reflection_string_bracket_inline_{anchor.GetHashCode():x8}*"],
            excludePathPatterns: null, excludeTests: false);

        var property = Assert.Single(unused, symbol => symbol.Name == "Name");
        Assert.Equal("reflection_or_config_suspect", property.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_StandaloneAttributeWithBracketInString_DoesNotLeakToAdjacentDeclaration()
    {
        // Regression extension for #375 — when a standalone attribute line
        // (attribute on its own line, declaration on the next line) contains `[`
        // or `]` inside a string literal, the upward scan must not treat that as
        // an extra bracket and swallow the previous member's attribute block.
        // #375 の追加回帰: 属性単独行 (属性と宣言が別行) の文字列リテラル内の `[` / `]` が
        // 上方スキャンで bracket depth として誤算されると、直前メンバーの属性ブロックまで
        // 吸い込まれて無関係なシンボルに属性が漏れ出す。これを防ぐ。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_standalone_bracket_fixture.cs",
            Lang = "csharp",
            Size = 400,
            Lines = 14,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 12,
                Content = "using System;\nusing System.Text.Json.Serialization;\n\npublic class Target\n{\n    [JsonPropertyName(\"name\")]\n    public string A { get; set; } = \"\";\n\n    [Obsolete(\"]\")]\n    public string B { get; set; } = \"\";\n}\n",
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 4,
                StartLine = 4,
                EndLine = 11,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "A",
                Line = 7,
                StartLine = 6,
                EndLine = 7,
                Signature = "public string A { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "B",
                Line = 10,
                StartLine = 9,
                EndLine = 10,
                Signature = "public string B { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_standalone_bracket_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        // A sits under a real reflection attribute → suspect.
        // B is a plain property and must not inherit A's reflection attribute through
        // the bracket-leak path.
        // A は本物の reflection 属性の下なので suspect。
        // B は plain property なので、bracket leak で A の reflection 属性を
        // 継承してはならない。
        var a = Assert.Single(unused, symbol => symbol.Name == "A");
        Assert.Equal("reflection_or_config_suspect", a.UnusedBucket);

        var b = Assert.Single(unused, symbol => symbol.Name == "B");
        Assert.Equal("public_or_exported_no_refs", b.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_CommentPrefixedInlineAttribute_KeepsReflectionContext()
    {
        // Regression for #409 follow-up (iteration 5) — a line-leading `/* ... */`
        // block comment followed by an inline attribute + declaration — e.g.
        // `/* note */ [JsonPropertyName("ok")] public string A ...` — must keep
        // the reflection context. The anchor's inline-decl check must run against
        // the sanitized line so the leading block comment (blanked by the
        // cross-line sanitizer) does not break the leading-`[` anchor.
        // #409 追加回帰 (iteration 5): 行頭の `/* ... */` ブロックコメント直後に
        // 続くインライン属性 + 宣言（例: `/* note */ [JsonPropertyName("ok")] public string A ...`）は、
        // 対象プロパティが reflection コンテキストを保たなければならない。
        // anchor のインライン宣言判定は sanitize 済み行に対して行い、
        // 行頭ブロックコメントが先頭 `[` アンカーを阻害しないようにする。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_comment_prefixed_inline_fixture.cs",
            Lang = "csharp",
            Size = 260,
            Lines = 7,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 6,
                Content = """
                using System.Text.Json.Serialization;

                public class Target
                {
                    /* note */ [JsonPropertyName("ok")] public string A { get; set; } = "";
                }

                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "A",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string A { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_comment_prefixed_inline_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        var a = Assert.Single(unused, symbol => symbol.Name == "A");
        Assert.Equal("reflection_or_config_suspect", a.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_AttributeLineWithTrailingLineComment_KeepsReflectionContext()
    {
        // Regression for #409 follow-up — a trailing `// comment` after the closing
        // `]` of an attribute must not flip the following property out of
        // `reflection_or_config_suspect`. The guard that detects inline `[attr] decl`
        // rows must run against sanitized lines so blanked comments do not pose as
        // declaration bodies.
        // #409 追加回帰: 属性行末尾の `// コメント` が、下のプロパティを
        // `reflection_or_config_suspect` から外してはならない。インライン `[attr] decl`
        // 判定は sanitize 済み行に対して行い、消されたコメントが宣言本体と誤認されないこと。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_trailing_comment_fixture.cs",
            Lang = "csharp",
            Size = 280,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 9,
                Content = """
                using System.Text.Json.Serialization;

                public class Target
                {
                    [JsonPropertyName("ok")] // trailing comment
                    public string C { get; set; } = "";
                }

                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 7,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "C",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public string C { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_trailing_comment_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        var c = Assert.Single(unused, symbol => symbol.Name == "C");
        Assert.Equal("reflection_or_config_suspect", c.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_AttributeLineWithTrailingBlockComment_KeepsReflectionContext()
    {
        // Regression for #409 follow-up — a trailing `/* ... */` block comment
        // after the closing `]` of an attribute must not flip the following
        // property out of `reflection_or_config_suspect`. The previous
        // BuildTriviaMask heuristic flagged any line containing `*/` as trivia,
        // so the `[JsonPropertyName(...)] /* note */` row was skipped by
        // FindPreviousNonTriviaLine and the real attribute block was lost.
        // #409 追加回帰: 属性行末尾の `/* ... */` ブロックコメントが、下のプロパティを
        // `reflection_or_config_suspect` から外してはならない。以前の BuildTriviaMask は
        // `*/` を含むだけで trivia 判定していたため、`[JsonPropertyName(...)] /* note */`
        // の行が FindPreviousNonTriviaLine に飛ばされ、本来の属性ブロックが失われていた。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_trailing_block_comment_fixture.cs",
            Lang = "csharp",
            Size = 300,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 9,
                Content = """
                using System.Text.Json.Serialization;

                public class Target
                {
                    [JsonPropertyName("ok")] /* trailing block comment */
                    public string D { get; set; } = "";
                }

                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 7,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "D",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public string D { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_trailing_block_comment_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        var d = Assert.Single(unused, symbol => symbol.Name == "D");
        Assert.Equal("reflection_or_config_suspect", d.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_MultilineAttributeWithEmbeddedBlockCommentMentioningIgnoreAttribute_KeepsReflectionContext()
    {
        // Regression for #409 follow-up — a multi-line block comment embedded
        // inside an attribute list must not leak phantom attribute names from
        // its body. The closing comment line `[JsonIgnore] */` would otherwise
        // survive BuildSingleLineTrivia as real text, and the phantom
        // `JsonIgnore` would cancel the real `JsonPropertyName`, flipping the
        // property out of `reflection_or_config_suspect`.
        // #409 追加回帰: 属性リスト内に埋め込まれた複数行ブロックコメントの本体が
        // 擬似的な属性名を ExtractNormalizedAttributeNames に漏らしてはならない。
        // コメント閉じ行 `[JsonIgnore] */` がそのまま BuildSingleLineTrivia を通過すると、
        // 幻の `JsonIgnore` が本物の `JsonPropertyName` を打ち消し、プロパティが
        // `reflection_or_config_suspect` から外れてしまう。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_embedded_block_comment_fixture.cs",
            Lang = "csharp",
            Size = 360,
            Lines = 12,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 11,
                Content = """
                using System.Text.Json.Serialization;

                public class Target
                {
                    [
                        /* explanation
                           [JsonIgnore] */
                        JsonPropertyName("ok")
                    ]
                    public string E { get; set; } = "";
                }

                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 11,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "E",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string E { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_embedded_block_comment_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        var e = Assert.Single(unused, symbol => symbol.Name == "E");
        Assert.Equal("reflection_or_config_suspect", e.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_CommentBetweenAttributeAndProperty_IsClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_comment_fixture.cs",
            Lang = "csharp",
            Size = 260,
            Lines = 11,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    /// Bound from JSON payload.
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 7,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_comment_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        var property = Assert.Single(unused, symbol => symbol.Name == "FullName");
        Assert.Equal("reflection_or_config_suspect", property.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_QualifiedAndSuffixedAttributes_AreClassifiedCorrectly()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_qualified_fixture.cs",
            Lang = "csharp",
            Size = 420,
            Lines = 14,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 13,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [global::System.Text.Json.Serialization.JsonPropertyName("full_name")]
                    public string QualifiedName { get; set; } = string.Empty;

                    [JsonPropertyNameAttribute("display_name")]
                    public string SuffixedName { get; set; } = string.Empty;

                    [System.Text.Json.Serialization.JsonIgnoreAttribute]
                    public string IgnoredName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 12,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "QualifiedName",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public string QualifiedName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "SuffixedName",
                Line = 9,
                StartLine = 9,
                EndLine = 9,
                Signature = "public string SuffixedName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "IgnoredName",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public string IgnoredName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_qualified_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "QualifiedName").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "SuffixedName").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "IgnoredName").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_BlockCommentBetweenAttributeAndProperty_IsClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_block_comment_fixture.cs",
            Lang = "csharp",
            Size = 320,
            Lines = 11,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    /* bound from payload
                       via serializer */
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 8,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["src/*reflection_block_comment_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        var property = Assert.Single(unused, symbol => symbol.Name == "FullName");
        Assert.Equal("reflection_or_config_suspect", property.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_LargePublicLimit_IsNotCappedAtBudget()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/large_public_unused_fixture.cs",
            Lang = "csharp",
            Size = 16000,
            Lines = 2600,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "public class PublicNoise0000 { }",
            }
        ]);

        var symbols = new List<SymbolRecord>();
        for (var i = 0; i < 2500; i++)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = $"PublicNoise{i:D4}",
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
                Signature = $"public class PublicNoise{i:D4} {{ }}",
                Visibility = "public",
            });
        }
        _writer.InsertSymbols(symbols);

        var unused = _reader.GetUnusedSymbols(limit: 3000, kind: null, lang: "csharp",
            pathPatterns: ["src/*large_public_unused_fixture.cs*"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal(2500, unused.Count);
    }

    [Fact]
    public void GetUnusedSymbols_UnsupportedLanguageReturnsEmpty()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "script.txt",
            Lang = "text",
            Size = 64,
            Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "helper",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
                Signature = "helper() {",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 20, kind: null, lang: "shell",
            pathPatterns: null, excludePathPatterns: null, excludeTests: false);
        var count = _reader.CountUnusedSymbols(kind: null, lang: "shell",
            pathPatterns: null, excludePathPatterns: null, excludeTests: false);

        Assert.Empty(unused);
        Assert.Equal(0, count.Count);
        Assert.Equal(0, count.FileCount);
    }
}
