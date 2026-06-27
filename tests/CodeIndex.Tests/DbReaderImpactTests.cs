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
    public void AnalyzeImpact_MaxDepthZero_ReturnsResolvedDefinitionsWithoutCallers()
    {
        InsertIndexedFile("src/depth_zero.cs", "csharp",
            """
            public class App
            {
                public void Run()
                {
                    Leaf();
                }

                public void Leaf() { }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 0, limit: 10, lang: "csharp");

        Assert.Equal("Leaf", analysis.Query);
        Assert.Equal("Leaf", analysis.ResolvedName);
        Assert.Equal(1, analysis.DefinitionCount);
        Assert.Single(analysis.Definitions);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal("none", analysis.ImpactMode);
        Assert.Equal("depth_requested_zero", analysis.ZeroResultReason);
        Assert.Equal(["depth_requested_zero"], analysis.ImpactFailureChain);
        Assert.Equal("precondition", analysis.SuggestionType);
        Assert.Contains("--max-hops 1", analysis.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeImpact_CSharpVerbatimQueryKeepsOriginalInputOnMiss()
    {
        // issue #960: verbatim C# queries should normalize for lookup when a match
        // exists, but a miss must keep the original spelling in the resolved name
        // so impact output does not claim a canonical name the user never asked for.
        // issue #960: C# の verbatim クエリは一致時のみ lookup 用に正規化し、miss
        // したときは resolved name に元の spelling を残して、ユーザーが指定していない
        // canonical 名を `impact` 出力に出さないこと。
        InsertIndexedFile("src/Verbatim.cs", "csharp",
            """
            public class @class
            {
            }
            """);

        var hit = _reader.AnalyzeImpact("@class", maxDepth: 1, limit: 10, lang: "csharp");
        Assert.Equal("class", hit.ResolvedName);
        Assert.Equal(1, hit.DefinitionCount);

        var miss = _reader.AnalyzeImpact("@missing", maxDepth: 1, limit: 10, lang: "csharp");
        Assert.Equal("@missing", miss.ResolvedName);
        Assert.Equal(0, miss.DefinitionCount);
        Assert.Equal("no_matching_definition", miss.ZeroResultReason);
        Assert.Equal(["definition_not_found"], miss.ImpactFailureChain);
        Assert.Equal("resolution", miss.SuggestionType);
    }

    [Fact]
    public void AnalyzeImpact_CycleReportsTerminationReasonAndMembers()
    {
        // Issue #1883: a caller cycle must be explicit in the impact metadata so consumers
        // can distinguish a natural end from a traversal stopped by the visited guard.
        // #1883: caller cycle は impact metadata に明示し、自然終了と visited guard による停止を区別する。
        InsertIndexedFile("src/impact_cycle.cs", "csharp",
            """
            public static class ImpactCycle
            {
                public static void A() { B(); }
                public static void B() { C(); }
                public static void C() { A(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("C", maxDepth: 5, limit: 20, lang: "csharp", pathPatterns: ["impact_cycle"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.CycleDetected, analysis.TerminationReason);
        Assert.True(analysis.CycleDetected);
        var cycle = Assert.Single(analysis.Cycles!);
        Assert.Equal(new[] { "A", "B", "C" }, cycle.Members);
    }

    [Fact]
    public void AnalyzeImpact_CycleBetweenAlreadyVisitedDirectCallersIsReported()
    {
        InsertIndexedFile("src/impact_direct_cycle.cs", "csharp",
            """
            public static class ImpactDirectCycle
            {
                public static void Leaf() { }
                public static void A() { Leaf(); B(); }
                public static void B() { Leaf(); A(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 5, limit: 20, lang: "csharp", pathPatterns: ["impact_direct_cycle"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.CycleDetected, analysis.TerminationReason);
        Assert.True(analysis.CycleDetected);
        var cycle = Assert.Single(analysis.Cycles!);
        Assert.Equal(new[] { "A", "B" }, cycle.Members);
    }

    [Fact]
    public void AnalyzeImpact_BoundaryRootCycleReportsCycleNotMaxDepth()
    {
        InsertIndexedFile("src/impact_boundary_root_cycle.cs", "csharp",
            """
            public static class ImpactBoundaryRootCycle
            {
                public static void Leaf() { A(); }
                public static void A() { Leaf(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["impact_boundary_root_cycle"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.CycleDetected, analysis.TerminationReason);
        Assert.True(analysis.CycleDetected);
        var cycle = Assert.Single(analysis.Cycles!);
        Assert.Equal(new[] { "A", "Leaf" }, cycle.Members);
    }

    [Fact]
    public void AnalyzeImpact_MaxDepthReportsTerminationReason()
    {
        InsertIndexedFile("src/impact_depth_reason.cs", "csharp",
            """
            public static class ImpactDepthReason
            {
                public static void Leaf() { }
                public static void Mid() { Leaf(); }
                public static void Top() { Mid(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["impact_depth_reason"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.MaxDepthReached, analysis.TerminationReason);
        Assert.False(analysis.CycleDetected);
        Assert.Null(analysis.Cycles);
    }

    [Fact]
    public void AnalyzeImpact_MaxDepthIsInclusiveAcrossChain()
    {
        // Issue #2121: AnalyzeImpact forwards maxDepth into the caller BFS, so it must keep
        // the same inclusive contract pinned by GetTransitiveCallers: maxDepth=N returns
        // callers at depths 1..N, not just 1..N-1.
        // issue #2121: AnalyzeImpact は maxDepth を caller BFS に渡すため、
        // GetTransitiveCallers と同じ inclusive 契約 (depth 1..N を返す) を維持する。
        InsertIndexedFile("src/impact_analyze_depth_chain.cs", "csharp",
            """
            public static class ImpactAnalyzeDepthChain
            {
                public static void Leaf() { }
                public static void Mid() { Leaf(); }
                public static void Top() { Mid(); }
            }
            """);

        var depth1 = _reader.AnalyzeImpact(
            "Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["impact_analyze_depth_chain"]);
        var depth2 = _reader.AnalyzeImpact(
            "Leaf", maxDepth: 2, limit: 20, lang: "csharp", pathPatterns: ["impact_analyze_depth_chain"]);

        Assert.Equal(new (string?, int)[] { ("Mid", 1) }, depth1.Callers.Select(r => (r.CallerName, r.Depth)).ToArray());
        Assert.Equal(ImpactTerminationReasons.MaxDepthReached, depth1.TerminationReason);

        var depth2Pairs = depth2.Callers
            .Select(r => (r.CallerName, r.Depth))
            .OrderBy(p => p.Depth)
            .ToArray();
        Assert.Equal(new (string?, int)[] { ("Mid", 1), ("Top", 2) }, depth2Pairs);
        Assert.Equal(ImpactTerminationReasons.Completed, depth2.TerminationReason);
    }

    [Fact]
    public void AnalyzeImpact_MaxDepthBoundaryWithoutSkippedCallerReportsCompleted()
    {
        InsertIndexedFile("src/impact_depth_completed.cs", "csharp",
            """
            public static class ImpactDepthCompleted
            {
                public static void Leaf() { }
                public static void OnlyCaller() { Leaf(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["impact_depth_completed"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.Completed, analysis.TerminationReason);
        Assert.False(analysis.CycleDetected);
        Assert.Null(analysis.Cycles);
    }

    [Fact]
    public void AnalyzeImpact_DepthZeroReportsCompletedForResolvedSymbol()
    {
        InsertIndexedFile("src/impact_depth_zero.cs", "csharp",
            """
            public static class ImpactDepthZero
            {
                public static void Leaf() { }
                public static void Caller() { Leaf(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 0, limit: 20, lang: "csharp", pathPatterns: ["impact_depth_zero"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.Completed, analysis.TerminationReason);
        Assert.Equal("depth_requested_zero", analysis.ZeroResultReason);
        Assert.Equal(["depth_requested_zero"], analysis.ImpactFailureChain);
        Assert.Equal("precondition", analysis.SuggestionType);
        Assert.False(analysis.CycleDetected);
        Assert.Null(analysis.Cycles);
    }

    [Fact]
    public void AnalyzeImpact_UserLimitTruncation_PropagatesTruncatedReason()
    {
        // #1533: AnalyzeImpact must surface the truncated_reason returned by
        // GetTransitiveCallers so the CLI/MCP layer can give actionable retry advice.
        // #1533: AnalyzeImpact は GetTransitiveCallers の truncated_reason を
        // そのまま伝搬して CLI/MCP 側で適切な再試行ガイダンスを出せるようにする。
        const int callerCount = 6;
        for (int i = 0; i < callerCount; i++)
        {
            var callerFileId = _writer.UpsertFile(new FileRecord
            {
                Path = $"src/impact_limit_caller_{i:D2}.py",
                Lang = "python",
                Size = 96,
                Lines = 2,
                Modified = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            });
            _writer.InsertChunks([new ChunkRecord
            {
                FileId = callerFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 2,
                Content = $"def impact_caller_{i:D2}():\n    return widget_op()\n",
            }]);
            _writer.InsertReferences([
                new ReferenceRecord
                {
                    FileId = callerFileId,
                    SymbolName = "widget_op",
                    ReferenceKind = "call",
                    Line = 2,
                    Column = 12,
                    Context = "return widget_op()",
                    ContainerKind = "function",
                    ContainerName = $"impact_caller_{i:D2}",
                },
            ]);
        }

        var analysis = _reader.AnalyzeImpact("widget_op", maxDepth: 1, limit: 2);

        Assert.True(analysis.Truncated);
        Assert.Equal(ImpactTruncatedReasons.UserLimit, analysis.TruncatedReason);
        Assert.Equal(2, analysis.Callers.Count);
    }

    [Fact]
    public void AnalyzeImpact_NotTruncated_LeavesTruncatedReasonNull()
    {
        // #1533: truncated_reason must be omitted (null) when truncated is false so
        // downstream consumers do not need to ignore stale reason strings.
        // #1533: truncated が false のときは truncated_reason を null にして、
        // 利用側が古い理由文字列を無視する必要がないようにする。
        InsertIndexedFile("src/impact_no_truncate.cs", "csharp",
            """
            public static class NoTruncateChain
            {
                public static void Leaf() { }
                public static void Caller() { Leaf(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 50, lang: "csharp", pathPatterns: ["impact_no_truncate"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        var caller = Assert.Single(analysis.Callers);
        Assert.Equal(ImpactResultKinds.Graph, caller.ResultKind);
    }

    [Fact]
    public void AnalyzeImpact_ClassSymbolReturnsHeuristicFileDependencyHints()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Run(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FolderDiffService", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.Empty(analysis.Callers);
        Assert.True(analysis.HasClassLikeDefinitions);
        Assert.False(analysis.HasMultipleDefinitions);
        Assert.False(analysis.HasMultipleDefinitionFiles);
        Assert.Equal(1, analysis.HintCount);
        var edge = Assert.Single(analysis.FileImpacts);
        Assert.Equal(ImpactResultKinds.FileHeuristic, edge.ResultKind);
        Assert.Equal("src/App.cs", edge.SourcePath);
        Assert.Equal("src/FolderDiffService.cs", edge.TargetPath);
        Assert.Contains("ExecuteFolderDiffAsync", edge.Symbols);
    }

    [Fact]
    public void AnalyzeImpact_ClassAndNamespaceWithSameName_StillReturnsHeuristicHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            namespace FooService;

            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.True(analysis.HasMultipleDefinitions);
        Assert.False(analysis.HasMultipleDefinitionFiles);
        Assert.Equal(2, analysis.DefinitionCount);
        Assert.Equal(1, analysis.HintCount);
        Assert.Equal("src/App.cs", Assert.Single(analysis.FileImpacts).SourcePath);
    }

    [Fact]
    public void AnalyzeImpact_FoldEquivalentClassDefinitions_ReportAmbiguity()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/FullwidthFooService.cs", "csharp",
            """
            public class ＦｏｏＳｅｒｖｉｃｅ
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(2, analysis.DefinitionCount);
        Assert.True(analysis.HasMultipleDefinitions);
        Assert.Equal("multiple_definition_files", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_PartialClassWithoutReverseEdges_ExplainsMultipleDefinitions()
    {
        InsertIndexedFile("src/Worker.Part1.cs", "csharp",
            """
            public partial class Worker
            {
                public void Start() { }
            }
            """);
        InsertIndexedFile("src/Worker.Part2.cs", "csharp",
            """
            public partial class Worker
            {
                public void Stop() { }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Worker", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.True(analysis.HasClassLikeDefinitions);
        Assert.True(analysis.HasMultipleDefinitions);
        Assert.True(analysis.HasMultipleDefinitionFiles);
        Assert.Equal("multiple_definition_files", analysis.ZeroResultReason);
        Assert.Contains("deps --path <definition-path> --reverse", analysis.Suggestion);
    }

    [Fact]
    public void AnalyzeImpact_DuplicateDefinitionsInOneFile_ExplainsAmbiguity()
    {
        InsertIndexedFile("src/Services.cs", "csharp",
            """
            namespace A
            {
                public class FooService
                {
                    public void Run() { }
                }
            }

            namespace B
            {
                public class FooService
                {
                    public void Run() { }
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(2, analysis.DefinitionCount);
        Assert.Equal(1, analysis.DefinitionFileCount);
        Assert.True(analysis.HasMultipleDefinitions);
        Assert.False(analysis.HasMultipleDefinitionFiles);
        Assert.Equal("multiple_definitions", analysis.ZeroResultReason);
        Assert.Contains("fully qualified or member symbol query", analysis.Suggestion);
    }

    [Fact]
    public void AnalyzeImpact_ClassCollisionWithoutTypeEvidenceReturnsNoHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/BarService.cs", "csharp",
            """
            public class BarService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(BarService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(0, analysis.HintCount);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_NamespaceDoesNotFallbackToFileDependencies()
    {
        InsertIndexedFile("src/Services.cs", "csharp",
            """
            namespace Acme;

            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            namespace Acme;

            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Acme", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal("non_callable_symbol_kind", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_ImportOnlyQueryReportsNonCallableSymbolKind()
    {
        InsertIndexedFile("src/app.py", "python",
            """
            import requests
            """);

        var analysis = _reader.AnalyzeImpact("requests", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(1, analysis.DefinitionCount);
        Assert.Equal("import", Assert.Single(analysis.Definitions).Kind);
        Assert.Equal("non_callable_symbol_kind", analysis.ZeroResultReason);
        Assert.Contains("definition <symbol>", analysis.Suggestion);
    }

    [Fact]
    public void AnalyzeImpact_UnresolvedExternalCallOnlyWithoutTypeEvidenceReturnsNoHints()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/ExternalConsumer.cs", "csharp",
            """
            public class ExternalConsumer
            {
                public void Boot()
                {
                    ExecuteFolderDiffAsync();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FolderDiffService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.Callers);
        Assert.Equal(0, analysis.HintCount);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_UnicodeTypeEvidenceStillEnablesHeuristicHints()
    {
        InsertIndexedFile("src/ＦｏｏＳｅｒｖｉｃｅ.cs", "csharp",
            """
            public class ＦｏｏＳｅｒｖｉｃｅ
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(ＦｏｏＳｅｒｖｉｃｅ service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("ＦｏｏＳｅｒｖｉｃｅ", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.Equal(1, analysis.HintCount);
        Assert.Equal("src/App.cs", Assert.Single(analysis.FileImpacts).SourcePath);
    }

    [Fact]
    public void AnalyzeImpact_CommentOnlyTypeMentionDoesNotCountAsTypeEvidence()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/OtherService.cs", "csharp",
            """
            public class OtherService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(OtherService service)
                {
                    service.Run(); // TODO: maybe replace with FooService later
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(0, analysis.HintCount);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_StringLiteralTypeMentionDoesNotCountAsTypeEvidence()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Execute() { }
            }
            """);
        InsertIndexedFile("src/Worker.cs", "csharp",
            """
            public class Worker
            {
                public void Execute() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(Worker worker)
                {
                    var label = "FooService";
                    worker.Execute();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(0, analysis.HintCount);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_ExcludeTestsIgnoresOutOfScopeDuplicateDefinitions()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("tests/FooServiceTests.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10, excludeTests: true);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.False(analysis.HasMultipleDefinitionFiles);
        Assert.Equal(1, analysis.DefinitionFileCount);
        Assert.Equal(1, analysis.HintCount);
        Assert.Equal("src/FooService.cs", Assert.Single(analysis.Definitions).Path);
        Assert.Equal("src/App.cs", Assert.Single(analysis.FileImpacts).SourcePath);
    }

    [Fact]
    public void AnalyzeImpact_IgnoresUnsupportedLanguageDuplicates()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/tools.txt", "text",
            """
            FooService() {
              :
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.False(analysis.HasMultipleDefinitions);
        Assert.False(analysis.HasMultipleDefinitionFiles);
        Assert.Equal(1, analysis.DefinitionFileCount);
        Assert.Equal("src/FooService.cs", Assert.Single(analysis.Definitions).Path);
        Assert.Equal("src/App.cs", Assert.Single(analysis.FileImpacts).SourcePath);
    }

    [Fact]
    public void AnalyzeImpact_ExactDefinitionResolutionSkipsUnsupportedMatchesBeforeLimit()
    {
        for (int i = 0; i < 60; i++)
        {
            InsertIndexedFile($"scripts/Foo{i:D2}.txt", "text",
                """
                Foo() {
                  :
                }
                """);
        }

        InsertIndexedFile("src/Foo.cs", "csharp",
            """
            public class Foo
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(Foo service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Foo", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.Equal(1, analysis.DefinitionCount);
        Assert.Equal("src/Foo.cs", Assert.Single(analysis.Definitions).Path);
        Assert.Equal("src/App.cs", Assert.Single(analysis.FileImpacts).SourcePath);
    }

    [Fact]
    public void AnalyzeImpact_SubstringTypeEvidenceDoesNotCountAsStructuredEvidence()
    {
        InsertIndexedFile("src/Foo.cs", "csharp",
            """
            public class Foo
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Handle(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Foo", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(0, analysis.HintCount);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_HeuristicHintsSetTruncatedWhenLimitReached()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App1.cs", "csharp",
            """
            public class App1
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);
        InsertIndexedFile("src/App2.cs", "csharp",
            """
            public class App2
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FolderDiffService", maxDepth: 3, limit: 1);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.True(analysis.Truncated);
        Assert.Single(analysis.FileImpacts);
        Assert.Equal(1, analysis.HintCount);
    }

    [Fact]
    public void AnalyzeImpact_HeuristicHintsKeepActualReferenceCount()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                    service.ExecuteFolderDiffAsync();
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FolderDiffService", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        var edge = Assert.Single(analysis.FileImpacts);
        Assert.Equal(4, edge.ReferenceCount);
        Assert.Equal("ExecuteFolderDiffAsync,FolderDiffService", edge.Symbols);
    }
}
