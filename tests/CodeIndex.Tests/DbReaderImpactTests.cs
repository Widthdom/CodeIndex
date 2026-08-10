using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public void AnalyzeImpact_DefinitiveMatlabTargetTraversesAmbiguousMCallers_Issue4738()
    {
        InsertIndexedFile(
            "src/target.m",
            "matlab",
            """
            function Target()
            end
            """);
        InsertIndexedFile(
            "src/caller.m",
            "ambiguous_m",
            """
            function Bridge()
              Target();
            end
            function Outer()
              Bridge();
            end
            """);

        var analysis = _reader.AnalyzeImpact(
            "Target",
            maxDepth: 2,
            limit: 10,
            lang: "matlab",
            pathPatterns: ["src/*.m"]);

        Assert.Equal("callers", analysis.ImpactMode);
        Assert.Collection(
            analysis.Callers.OrderBy(caller => caller.Depth),
            caller =>
            {
                Assert.Equal("ambiguous_m", caller.Lang);
                Assert.Equal("Bridge", caller.CallerName);
                Assert.Equal(1, caller.Depth);
            },
            caller =>
            {
                Assert.Equal("ambiguous_m", caller.Lang);
                Assert.Equal("Outer", caller.CallerName);
                Assert.Equal(2, caller.Depth);
            });
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

        var analysis = _reader.AnalyzeImpact("C", maxDepth: 5, limit: 20, lang: "csharp", pathPatterns: ["src/*impact_cycle*"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.CycleDetected, analysis.TerminationReason);
        Assert.True(analysis.CycleDetected);
        var cycle = Assert.Single(analysis.Cycles!);
        Assert.Equal(new[] { "A", "B", "C" }, cycle.Members);
        Assert.Equal(3, cycle.MemberIdentities!.Count);
        Assert.All(cycle.MemberIdentities, member => Assert.NotNull(member.SymbolId));
        Assert.Equal(3, cycle.MemberIdentities.Select(member => member.SymbolId).Distinct().Count());
    }

    [Fact]
    public void AnalyzeImpact_SameDisplayNameOnRealEdgeDoesNotReportSingletonCycle_Issue4847()
    {
        // Issue #4847: display names are presentation data. Two consecutive real edges
        // through distinct canonical symbols named Run must not become a zero-hop cycle.
        // Issue #4847: 表示名は提示用データであり、Run という別々の正規シンボルを
        // 連続して辿る実エッジをゼロホップの cycle として扱ってはならない。
        InsertIndexedFile("src/ImpactTarget.cs", "csharp",
            """
            namespace ImpactIdentity.Target;

            public static class TargetWorker
            {
                public static void RunAsync() { }
            }
            """);
        InsertIndexedFile("src/ImpactMiddle.cs", "csharp",
            """
            namespace ImpactIdentity.Middle;

            public static class MiddleWorker
            {
                public static void Run()
                {
                    ImpactIdentity.Target.TargetWorker.RunAsync();
                }
            }
            """);
        InsertIndexedFile("src/ImpactOuter.cs", "csharp",
            """
            namespace ImpactIdentity.Outer;

            public static class OuterWorker
            {
                public static void Run()
                {
                    ImpactIdentity.Middle.MiddleWorker.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact(
            "ImpactIdentity.Target.TargetWorker.RunAsync",
            maxDepth: 2,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/Impact*.cs"],
            withPaths: true);

        Assert.False(analysis.CycleDetected);
        Assert.Null(analysis.Cycles);
        Assert.Equal(ImpactTerminationReasons.Completed, analysis.TerminationReason);
        Assert.True(
            analysis.Callers.Count == 2,
            string.Join(
                " | ",
                analysis.Callers.Select(caller =>
                    $"{caller.Path}:{caller.CallerName}->{caller.CalleeName}:{caller.CallerSymbolId}->{caller.CalleeSymbolId}:depth={caller.Depth}")));
        Assert.All(analysis.Callers, caller => Assert.Equal("Run", caller.CallerName));
        Assert.All(analysis.Callers, caller => Assert.NotNull(caller.CallerSymbolId));
        Assert.Equal(2, analysis.Callers.Select(caller => caller.CallerSymbolId).Distinct().Count());
        var outerEdge = Assert.Single(analysis.Callers.Where(caller => caller.Depth == 2));
        Assert.NotEqual(outerEdge.CallerSymbolId, outerEdge.CalleeSymbolId);
        Assert.Equal([new List<string> { "RunAsync", "Run", "Run" }], outerEdge.Paths);
        var pathDetails = Assert.Single(outerEdge.PathDetails!);
        Assert.Equal(3, pathDetails.Select(node => node.SymbolId).Distinct().Count());
        var structured = JsonNode.Parse(JsonSerializer.Serialize(
            analysis,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }))!;
        var structuredOuter = structured["callers"]!.AsArray()
            .Single(node => node!["depth"]!.GetValue<int>() == 2)!;
        Assert.Equal(outerEdge.CallerSymbolId, structuredOuter["caller_symbol_id"]!.GetValue<long>());
        Assert.Equal(outerEdge.CalleeSymbolId, structuredOuter["callee_symbol_id"]!.GetValue<long>());
        Assert.Equal(
            pathDetails[^1].SymbolId,
            structuredOuter["path_details"]![0]![2]!["symbol_id"]!.GetValue<long>());
    }

    [Fact]
    public void AnalyzeImpact_SameNameOverloadsRemainDistinctBySourceIdentity_Issue4847()
    {
        InsertIndexedFile("src/ImpactOverloads.cs", "csharp",
            """
            public static class ImpactOverloads
            {
                public static void Anchor() { }

                public static void Run()
                {
                    Anchor();
                }

                public static void Run(int value)
                {
                    Anchor();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact(
            "ImpactOverloads.Anchor",
            maxDepth: 1,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/ImpactOverloads.cs"]);

        Assert.False(analysis.CycleDetected);
        Assert.Equal(2, analysis.Callers.Count);
        Assert.All(analysis.Callers, caller => Assert.Equal("Run", caller.CallerName));
        Assert.All(analysis.Callers, caller => Assert.NotNull(caller.CallerSymbolId));
        Assert.Equal(2, analysis.Callers.Select(caller => caller.CallerSymbolId).Distinct().Count());
        Assert.Single(analysis.Callers.Select(caller => caller.CalleeSymbolId).Distinct());
        Assert.Equal(
            analysis.Callers.Select(caller => caller.FirstLine).OrderBy(line => line),
            analysis.Callers.Select(caller => caller.FirstLine));
    }

    [Fact]
    public void AnalyzeImpact_ResolvedGroupOverloadChainDoesNotReportSingletonCycle_Issue4847()
    {
        // A resolved_group candidate is a possible overload target, not proof of an edge to
        // every candidate. Revisiting the caller overload must therefore stay out of the
        // canonical cycle graph while conservative traversal remains available.
        // resolved_group の候補は overload の可能性であり、全候補への実辺を証明しない。
        // caller overload への再到達は保守的に探索しても、正規 cycle graph には入れない。
        InsertIndexedFile("src/ImpactOverloadChain.cs", "csharp",
            """
            public static class ImpactOverloadChain
            {
                public static void Leaf() { }

                public static void Run()
                {
                    Leaf();
                }

                public static void Run(int value)
                {
                    Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact(
            "ImpactOverloadChain.Leaf",
            maxDepth: 4,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/ImpactOverloadChain.cs"],
            withPaths: true);

        Assert.False(analysis.Truncated);
        Assert.False(analysis.CycleDetected);
        Assert.Null(analysis.Cycles);
        Assert.Equal(ImpactTerminationReasons.Completed, analysis.TerminationReason);
        Assert.Equal(2, analysis.Callers.Count);

        var overloadCaller = Assert.Single(analysis.Callers.Where(caller => caller.Depth == 2));
        Assert.Equal("Run", overloadCaller.CallerName);
        Assert.Equal("Run", overloadCaller.CalleeName);
        Assert.NotNull(overloadCaller.CallerSymbolId);
        Assert.Null(overloadCaller.CalleeSymbolId);
        Assert.Equal([new List<string> { "Leaf", "Run", "Run" }], overloadCaller.Paths);
        Assert.Equal(
            3,
            Assert.Single(overloadCaller.PathDetails!)
                .Select(node => node.SymbolId)
                .Distinct()
                .Count());
    }

    [Fact]
    public void AnalyzeImpact_UnresolvedUpstreamCallerRemainsVisibleButCannotCreateCanonicalCycle_Issue4847()
    {
        InsertIndexedFile("src/ImpactUnresolvedHop.cs", "csharp",
            """
            namespace ImpactIdentity.Unresolved;

            public static class Root
            {
                public static void Leaf() { }
            }

            public static class Middle
            {
                public static void Mid()
                {
                    Root.Leaf();
                }
            }

            public static class Outer
            {
                public static void Top(dynamic receiver)
                {
                    receiver.Mid();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact(
            "ImpactIdentity.Unresolved.Root.Leaf",
            maxDepth: 2,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/ImpactUnresolvedHop.cs"],
            withPaths: true);

        Assert.False(analysis.CycleDetected);
        Assert.Equal(ImpactTerminationReasons.Completed, analysis.TerminationReason);
        Assert.Equal(2, analysis.Callers.Count);
        var unresolvedCaller = Assert.Single(analysis.Callers.Where(caller => caller.Depth == 2));
        Assert.Equal("Top", unresolvedCaller.CallerName);
        Assert.NotNull(unresolvedCaller.CallerSymbolId);
        Assert.Null(unresolvedCaller.CalleeSymbolId);
        Assert.Equal([new List<string> { "Leaf", "Mid", "Top" }], unresolvedCaller.Paths);
        Assert.All(Assert.Single(unresolvedCaller.PathDetails!), node => Assert.NotNull(node.SymbolId));
    }

    [Fact]
    public void AnalyzeImpact_MixedTargetIdentitiesAggregateCallerWithoutGuessingPathRootIdentity_Issue4847()
    {
        InsertIndexedFile("src/ImpactMixedTargets.cs", "csharp",
            """
            namespace ImpactIdentity.Mixed;

            public static class First
            {
                public static void Target() { }
            }

            public static class Second
            {
                public static void Target() { }
            }

            public static class Source
            {
                public static void Caller()
                {
                    First.Target();
                    Second.Target();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact(
            "Target",
            maxDepth: 1,
            limit: 1,
            lang: "csharp",
            pathPatterns: ["src/ImpactMixedTargets.cs"],
            withPaths: true);

        Assert.False(analysis.Truncated);
        Assert.False(analysis.CycleDetected);
        var caller = Assert.Single(analysis.Callers);
        Assert.Equal("Caller", caller.CallerName);
        Assert.Equal(2, caller.ReferenceCount);
        Assert.Equal(2, caller.ReferenceKindCounts["call"]);
        Assert.NotNull(caller.CallerSymbolId);
        Assert.Null(caller.CalleeSymbolId);
        var pathDetails = Assert.Single(caller.PathDetails!);
        Assert.Null(pathDetails[0].SymbolId);
        Assert.Equal(caller.CallerSymbolId, pathDetails[1].SymbolId);

        var structured = JsonNode.Parse(JsonSerializer.Serialize(
            analysis,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }))!;
        var structuredCaller = structured["callers"]![0]!;
        Assert.Null(structuredCaller["callee_symbol_id"]);
        Assert.Null(structuredCaller["path_details"]![0]![0]!["symbol_id"]);
    }

    [Fact]
    public void AnalyzeImpact_DirectRecursionReportsRealSingletonCycle_Issue4847()
    {
        InsertIndexedFile("src/ImpactDirectRecursion.cs", "csharp",
            """
            public static class ImpactDirectRecursion
            {
                public static void Run()
                {
                    Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact(
            "ImpactDirectRecursion.Run",
            maxDepth: 2,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/ImpactDirectRecursion.cs"]);

        Assert.False(analysis.Truncated);
        Assert.True(analysis.CycleDetected);
        Assert.Equal(ImpactTerminationReasons.CycleDetected, analysis.TerminationReason);
        Assert.Empty(analysis.Callers);
        var cycle = Assert.Single(analysis.Cycles!);
        Assert.Equal(["Run"], cycle.Members);
        var member = Assert.Single(cycle.MemberIdentities!);
        Assert.NotNull(member.SymbolId);
        var structured = JsonNode.Parse(JsonSerializer.Serialize(
            analysis,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }))!;
        Assert.Equal(
            member.SymbolId,
            structured["cycles"]![0]!["member_identities"]![0]!["symbol_id"]!.GetValue<long>());
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

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 5, limit: 20, lang: "csharp", pathPatterns: ["src/*impact_direct_cycle*"]);

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

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["src/*impact_boundary_root_cycle*"]);

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

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["src/*impact_depth_reason*"]);

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
            "Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["src/*impact_analyze_depth_chain*"]);
        var depth2 = _reader.AnalyzeImpact(
            "Leaf", maxDepth: 2, limit: 20, lang: "csharp", pathPatterns: ["src/*impact_analyze_depth_chain*"]);

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

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["src/*impact_depth_completed*"]);

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

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 0, limit: 20, lang: "csharp", pathPatterns: ["src/*impact_depth_zero*"]);

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

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 50, lang: "csharp", pathPatterns: ["src/*impact_no_truncate*"]);

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
    public void AnalyzeImpact_PartialClassWithoutReverseEdges_UsesSingleLogicalRoot()
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

        var reader = CreateReferenceIdentityReadyImpactReader();
        var analysis = reader.AnalyzeImpact("Worker", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.True(analysis.HasClassLikeDefinitions);
        Assert.True(analysis.HasMultipleDefinitions);
        Assert.True(analysis.HasMultipleDefinitionFiles);
        Assert.Equal("logical_partial_family", analysis.TraversalRootScope);
        Assert.NotNull(analysis.TraversalPartialFamilyId);
        Assert.Equal(2, analysis.PartialFamilyMemberCount);
        Assert.Equal(2, analysis.PartialFamilyMemberRootCount);
        Assert.False(analysis.PartialFamilyMemberRootTruncated);
        Assert.Equal(0, analysis.PartialFamilyMemberRootOmitted);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
        Assert.Contains("deps --path", analysis.Suggestion);
        Assert.Contains("--reverse", analysis.Suggestion);
    }

    [Fact]
    public void AnalyzeImpact_StaleReferenceIdentityDoesNotClaimLogicalPartialTraversal()
    {
        InsertIndexedFile("src/Stale.Part1.cs", "csharp", "public partial class Stale { public void Start() { } }");
        InsertIndexedFile("src/Stale.Part2.cs", "csharp", "public partial class Stale { public void Stop() { } }");
        InsertIndexedFile(
            "src/StaleConsumer.cs",
            "csharp",
            "public class StaleConsumer { public void Run(Stale value) { value.Start(); value.Stop(); } }");
        _writer.ClearReferenceIdentityContractReady();
        var reader = new DbReader(_db.Connection);

        var analysis = reader.AnalyzeImpact("Stale", maxDepth: 2, limit: 10);

        Assert.Equal("symbol", analysis.TraversalRootScope);
        Assert.Null(analysis.TraversalPartialFamilyId);
        Assert.Null(analysis.PartialFamilyMemberCount);
        Assert.Null(analysis.PartialFamilyMemberRootCount);
        Assert.Null(analysis.PartialFamilyMemberRootTruncated);
        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal("multiple_definition_files", analysis.ZeroResultReason);
        Assert.Equal(["multiple_definition_files"], analysis.ImpactFailureChain);
    }

    [Fact]
    public void AnalyzeImpact_PartialClassFileHintsUnionAllMemberFilesAndDeduplicateConsumers()
    {
        InsertIndexedFile("src/Worker.Start.cs", "csharp",
            """
            namespace Demo;
            public partial class Worker
            {
                public void Start() { }
            }
            """);
        InsertIndexedFile("src/Worker.Stop.cs", "csharp",
            """
            namespace Demo;
            public partial class Worker
            {
                public void Stop() { }
            }
            """);
        InsertIndexedFile("src/StartConsumer.cs", "csharp",
            """
            namespace Demo;
            public class StartConsumer
            {
                public void Run(Worker worker) => worker.Start();
            }
            """);
        InsertIndexedFile("src/StopConsumer.cs", "csharp",
            """
            namespace Demo;
            public class StopConsumer
            {
                public void Run(Worker worker) => worker.Stop();
            }
            """);
        InsertIndexedFile("src/BothConsumer.cs", "csharp",
            """
            namespace Demo;
            public class BothConsumer
            {
                public void Run(Worker worker)
                {
                    worker.Start();
                    worker.Stop();
                }
            }
            """);

        var reader = CreateReferenceIdentityReadyImpactReader();
        var analysis = reader.AnalyzeImpact("Demo.Worker", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.Empty(analysis.Callers);
        Assert.Equal(3, analysis.FileImpacts.Count);
        Assert.Equal(
            ["src/BothConsumer.cs", "src/StartConsumer.cs", "src/StopConsumer.cs"],
            analysis.FileImpacts.Select(impact => impact.SourcePath).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(analysis.FileImpacts, impact => impact.SourcePath.StartsWith("src/Worker.", StringComparison.Ordinal));
        Assert.Equal(analysis.FileImpacts.Count, analysis.FileImpacts.Select(impact => impact.SourcePath).Distinct().Count());
    }

    [Fact]
    public void AnalyzeImpact_PartialMethodFamilyTraversesUnionDeduplicatesAndDetectsLogicalCycle()
    {
        InsertIndexedFile("src/Worker.Sync.Declaration.cs", "csharp",
            """
            namespace Demo;
            public partial class Worker
            {
                partial void Sync();
                public void Start() => Sync();
            }
            """);
        InsertIndexedFile("src/Worker.Sync.Implementation.cs", "csharp",
            """
            namespace Demo;
            public partial class Worker
            {
                partial void Sync() => Finish();
                public void Finish() => Sync();
            }
            """);
        InsertIndexedFile("src/Entry.cs", "csharp",
            """
            namespace Demo;
            public class Entry
            {
                public void Run(Worker worker) => worker.Start();
            }
            """);
        InsertIndexedFile("src/Other.Sync.cs", "csharp",
            """
            namespace Other;
            public class Worker
            {
                public void Sync() { }
            }
            """);

        var reader = CreateReferenceIdentityReadyImpactReader();
        var bounded = reader.AnalyzeImpact("Demo.Worker.Sync", maxDepth: 1, limit: 10, withPaths: true);
        var expanded = reader.AnalyzeImpact("Demo.Worker.Sync", maxDepth: 2, limit: 10, withPaths: true);

        Assert.Equal(2, bounded.DefinitionCount);
        Assert.Equal(1, bounded.LogicalDefinitionCount);
        Assert.Equal("logical_partial_family", bounded.TraversalRootScope);
        Assert.Equal(2, bounded.PartialFamilyMemberRootCount);
        Assert.Equal(["Finish", "Start"], bounded.Callers.Select(caller => caller.CallerName).Order(StringComparer.Ordinal));
        Assert.All(bounded.Callers, caller => Assert.Equal(1, caller.Depth));
        Assert.Equal(bounded.Callers.Count, bounded.Callers.Select(caller => caller.CallerSymbolId).Distinct().Count());
        Assert.True(bounded.CycleDetected);
        Assert.Contains(bounded.Cycles!, cycle => cycle.Members.Any(member => member.EndsWith(".Sync", StringComparison.Ordinal)) && cycle.Members.Contains("Finish"));
        Assert.All(
            bounded.Callers.SelectMany(caller => caller.PathDetails ?? []),
            path =>
            {
                Assert.EndsWith(".Sync", path[0].Name, StringComparison.Ordinal);
                Assert.StartsWith("partial:", path[0].PartialFamilyId);
                Assert.StartsWith("src/Worker.Sync.", path[0].DefinitionPath);
            });

        Assert.Contains(expanded.Callers, caller => caller.CallerName == "Run" && caller.Depth == 2);
        Assert.DoesNotContain(expanded.Callers, caller => caller.Path == "src/Other.Sync.cs");
        Assert.Equal(expanded.Callers.Count, expanded.Callers.Select(caller => caller.CallerSymbolId).Distinct().Count());
        Assert.True(expanded.CycleDetected);
        Assert.Contains(expanded.Cycles!, cycle => cycle.Members.Any(member => member.EndsWith(".Sync", StringComparison.Ordinal)) && cycle.Members.Contains("Finish"));
    }

    [Fact]
    public void AnalyzeImpact_CallerFreePartialMethodIsOneLogicalNoCallersResult()
    {
        InsertIndexedFile("src/Worker.Sync.Declaration.cs", "csharp",
            """
            namespace Demo;
            public partial class Worker
            {
                partial void Sync();
            }
            """);
        InsertIndexedFile("src/Worker.Sync.Implementation.cs", "csharp",
            """
            namespace Demo;
            public partial class Worker
            {
                partial void Sync() { }
            }
            """);

        var reader = CreateReferenceIdentityReadyImpactReader();
        var analysis = reader.AnalyzeImpact("Demo.Worker.Sync", maxDepth: 2, limit: 10);

        Assert.Equal(2, analysis.DefinitionCount);
        Assert.Equal(1, analysis.LogicalDefinitionCount);
        Assert.Equal("logical_partial_family", analysis.TraversalRootScope);
        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.Callers);
        Assert.Null(analysis.ZeroResultReason);
        Assert.Equal(["no_callers"], analysis.ImpactFailureChain);
    }

    [Fact]
    public void AnalyzeImpact_UnrelatedSameNameTypeRemainsAmbiguousBesidePartialFamily()
    {
        InsertIndexedFile("src/A.Worker.One.cs", "csharp",
            """
            namespace A;
            public partial class Worker { }
            """);
        InsertIndexedFile("src/A.Worker.Two.cs", "csharp",
            """
            namespace A;
            public partial class Worker { }
            """);
        InsertIndexedFile("src/B.Worker.cs", "csharp",
            """
            namespace B;
            public class Worker { }
            """);

        var analysis = _reader.AnalyzeImpact("Worker", maxDepth: 3, limit: 10);

        Assert.Equal(3, analysis.DefinitionCount);
        Assert.Equal(2, analysis.LogicalDefinitionCount);
        Assert.Equal("symbol", analysis.TraversalRootScope);
        Assert.Null(analysis.TraversalPartialFamilyId);
        Assert.Equal("multiple_definition_files", analysis.ZeroResultReason);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
    }

    [Fact]
    public void AnalyzeImpact_PartialFamilyBudgetIsIndependentFromTraversalTruncation()
    {
        InsertIndexedFile("src/Budgeted.One.cs", "csharp", "public partial class Budgeted { }");
        InsertIndexedFile("src/Budgeted.Two.cs", "csharp", "public partial class Budgeted { }");
        InsertIndexedFile("src/Budgeted.Three.cs", "csharp", "public partial class Budgeted { }");
        var reader = CreateReferenceIdentityReadyImpactReader();
        reader.ImpactPartialFamilyMemberBudget = 2;

        var analysis = reader.AnalyzeImpact("Budgeted", maxDepth: 2, limit: 10);

        Assert.Equal("logical_partial_family", analysis.TraversalRootScope);
        Assert.Equal(3, analysis.PartialFamilyMemberCount);
        Assert.Equal(2, analysis.PartialFamilyMemberRootCount);
        Assert.Equal(2, analysis.PartialFamilyMemberRootLimit);
        Assert.True(analysis.PartialFamilyMemberRootTruncated);
        Assert.Equal(1, analysis.PartialFamilyMemberRootOmitted);
        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.False(analysis.CountIsAuthoritative);
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

    [Fact]
    public void AnalyzeImpact_CaseSensitiveWorkspaceTreatsCaseVariantDefinitionFilesAsDistinct()
    {
        StampWorkspacePathCaseSensitive(true);
        InsertIndexedFile("src/CaseVariantTarget.cs", "csharp",
            """
            public class CaseVariantTarget
            {
                public void Start() { }
            }
            """);
        InsertIndexedFile("src/casevarianttarget.cs", "csharp",
            """
            public class CaseVariantTarget
            {
                public void Stop() { }
            }
            """);

        var analysis = _reader.AnalyzeImpact("CaseVariantTarget", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.True(analysis.HasMultipleDefinitions);
        Assert.True(analysis.HasMultipleDefinitionFiles);
        Assert.Equal("multiple_definition_files", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_CaseInsensitiveWorkspaceCollapsesCaseVariantDefinitionFiles()
    {
        StampWorkspacePathCaseSensitive(false);
        InsertIndexedFile("src/CaseVariantTarget.cs", "csharp",
            """
            public class CaseVariantTarget
            {
                public void Start() { }
            }
            """);
        InsertIndexedFile("src/casevarianttarget.cs", "csharp",
            """
            public class CaseVariantTarget
            {
                public void Stop() { }
            }
            """);

        var analysis = _reader.AnalyzeImpact("CaseVariantTarget", maxDepth: 3, limit: 10);

        Assert.True(analysis.HasMultipleDefinitions);
        Assert.False(analysis.HasMultipleDefinitionFiles);
    }

    private DbReader CreateReferenceIdentityReadyImpactReader()
    {
        _writer.RefreshMutualRecursionFlags(stampReferenceIdentityContractReady: true);
        return new DbReader(_db.Connection);
    }
}
