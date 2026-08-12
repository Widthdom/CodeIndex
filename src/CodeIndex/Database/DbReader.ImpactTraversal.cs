using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbReader
{
    // Per-result cap on the number of distinct shortest paths surfaced by impact --with-paths.
    // Each call chain row may carry multiple converging paths from the resolved root through
    // distinct intermediates; the cap keeps JSON output bounded for diamond-heavy graphs and
    // is signaled by ImpactResult.PathsTruncated when exceeded.
    // impact --with-paths が 1 caller につき保持する経路数の上限。ダイヤモンド型で多経路が
    // 収束する場合に JSON 膨張を抑える役割があり、超過時は PathsTruncated で通知する。
    private const int DefaultImpactPathsPerResult = 10;
    internal const int DefaultImpactGraphStateEntryBudget = 10_000;
    internal const int DefaultImpactPartialFamilyMemberBudget = 10_000;
    internal int ImpactPartialFamilyMemberBudget { get; set; } = DefaultImpactPartialFamilyMemberBudget;
    internal const int ImpactBoundaryCallerProbeBudget = 512;
    private const int ImpactBoundaryCallerProbePageSize = 64;

    internal int? ImpactGraphStateEntryBudgetForTesting { get; set; }
    internal int? ImpactBoundaryCallerProbeBudgetForTesting { get; set; }

    private sealed record ImpactTraversalRequest(
        string SymbolName,
        int MaxDepth,
        int Limit,
        string? Lang,
        IReadOnlyList<string>? PathPatterns,
        IReadOnlyList<string>? ExcludePathPatterns,
        bool ExcludeTests,
        bool WithPaths,
        int MaxPathsPerResult,
        int ResultOffset,
        bool IncludeMemberReads);

    private sealed record ImpactTraversalRoot(
        string ResolvedName,
        HashSet<string> DefinitionPaths,
        bool HasResolvedIdentityGraph,
        bool IsLogicalPartialFamily,
        HashSet<long> IdentitySymbolIds,
        bool IncludeAmbiguousMSource,
        bool InitiallyTruncated,
        string NodeKey,
        ImpactPathNode? PathNode);

    private readonly record struct ImpactTraversalFrontierNode(
        string Symbol,
        long? SymbolId,
        IReadOnlyList<long>? TargetSymbolIds,
        string NodeKey,
        int Depth);

    private readonly record struct ImpactBoundaryInspection(
        bool HasUnvisitedCaller,
        bool ProbeBudgetHit);

    /// <summary>
    /// Compute transitive callers of a symbol using BFS with exact matching.
    /// Returns each unique caller in the call chain with its depth from the root symbol.
    /// The <paramref name="maxDepth"/> bound is inclusive: when <paramref name="maxDepth"/> is N,
    /// callers at depth 1 through N are returned (so a chain A→B→C→D queried against D with
    /// <c>maxDepth: 2</c> yields C at depth 1 and B at depth 2). Truncation is signaled via the
    /// Truncated property in results. When Truncated is true, TruncatedReason distinguishes
    /// user_limit (raise <c>--limit</c>) from safety_cap (pathological graph). See Issue #1533.
    /// When <paramref name="withPaths"/> is true, each ImpactResult is populated with the
    /// distinct shortest call paths from the resolved root through any intermediates to that
    /// caller (issue #1536); converging diamond chains surface every shortest route up to
    /// <paramref name="maxPathsPerResult"/>.
    /// 完全一致の BFS でシンボルの推移的呼び出し元を算出。各呼び出し元とルートシンボルからの深さを返す。
    /// <paramref name="maxDepth"/> は inclusive で、N を指定すると depth 1〜N の caller を返す
    /// (例: A→B→C→D のチェーンで D を <c>maxDepth: 2</c> 検索すると C(depth=1) と B(depth=2) を返す)。
    /// 結果が切り詰められた場合は Truncated フラグで通知し、TruncatedReason で
    /// user_limit (--limit 到達、緩和で増える) と safety_cap (病的グラフ、--limit 緩和では解消しない) を区別する (#1533)。
    /// <paramref name="withPaths"/> を true にすると、各 caller に対してルートからの推移経路
    /// （ダイヤモンド収束時は複数）を <paramref name="maxPathsPerResult"/> 件まで付与する（issue #1536）。
    /// </summary>
    public (List<ImpactResult> Results, bool Truncated, string? TruncatedReason, string TerminationReason, List<ImpactCycleResult> Cycles) GetTransitiveCallers(
        string symbolName,
        int maxDepth = 5,
        int limit = 50,
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        bool withPaths = false,
        int maxPathsPerResult = DefaultImpactPathsPerResult,
        int resultOffset = 0,
        bool includeMemberReads = false)
    {
        var request = new ImpactTraversalRequest(
            symbolName,
            maxDepth,
            limit,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            withPaths,
            maxPathsPerResult,
            resultOffset,
            includeMemberReads);
        var root = ResolveImpactTraversalRoot(request);
        if (root == null)
            return ([], false, null, ImpactTerminationReasons.Completed, []);

        return new ImpactTraversalEngine(this, request, root).Run();
    }

    private ImpactTraversalRoot? ResolveImpactTraversalRoot(ImpactTraversalRequest request)
    {
        var resolvedName = ResolveSymbolName(request.SymbolName, request.Lang);
        var hasResolvedIdentityGraph = _referenceIdentityContractCurrent;
        var canResolveQualifiedCSharpIdentity =
            hasResolvedIdentityGraph
            && SqlNameResolver.HasQualifier(request.SymbolName)
            && request.Lang is null or "csharp";
        var rootDefinitionLimit = canResolveQualifiedCSharpIdentity
            ? DefaultImpactGraphStateEntryBudget
            : request.Limit;
        var resolution = ResolveImpactRootDefinitions(request, resolvedName, rootDefinitionLimit);
        var definitions = resolution.Definitions;
        var definitionPaths = definitions
            .Select(static definition => definition.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isLogicalPartialFamily = IsLogicalPartialFamilyRoot(
            hasResolvedIdentityGraph,
            resolution,
            definitions);
        var qualifiedCSharpIds = ResolveQualifiedCSharpRootIds(
            canResolveQualifiedCSharpIdentity,
            isLogicalPartialFamily,
            resolution,
            definitions);

        if (hasResolvedIdentityGraph && definitionPaths.Count > 1 && qualifiedCSharpIds.Count == 0)
            return null;

        var ambiguousMRootId = ResolveAmbiguousMRootId(
            hasResolvedIdentityGraph,
            request.Lang,
            definitions);
        var identityIds = qualifiedCSharpIds.Count > 0
            ? qualifiedCSharpIds
            : ambiguousMRootId is long ambiguousId
                ? new HashSet<long> { ambiguousId }
                : [];
        var singleIdentityId = identityIds.Count == 1 ? identityIds.Single() : (long?)null;
        var rootNodeKey = identityIds.Count > 1
            ? $"identity:{NameFold.Fold(request.SymbolName) ?? request.SymbolName}"
            : ImpactNodeIdentity.TraversalKey(singleIdentityId, resolvedName);
        var pathNode = request.WithPaths
            ? CreateImpactRootPathNode(
                resolvedName,
                singleIdentityId,
                request.Lang,
                isLogicalPartialFamily,
                definitions)
            : null;

        return new ImpactTraversalRoot(
            resolvedName,
            definitionPaths,
            hasResolvedIdentityGraph,
            isLogicalPartialFamily,
            identityIds,
            ambiguousMRootId != null,
            !isLogicalPartialFamily
                && qualifiedCSharpIds.Count > 0
                && resolution.PhysicalSymbolIdsTruncated,
            rootNodeKey,
            pathNode);
    }

    private ImpactDefinitionResolution ResolveImpactRootDefinitions(
        ImpactTraversalRequest request,
        string resolvedName,
        int definitionLimit)
    {
        var resolution = ResolveImpactDefinitions(
            request.SymbolName,
            definitionLimit,
            request.Lang,
            request.PathPatterns,
            request.ExcludePathPatterns,
            request.ExcludeTests);
        if (resolution.Definitions.Count == 0
            && !string.Equals(request.SymbolName, resolvedName, StringComparison.Ordinal))
        {
            resolution = ResolveImpactDefinitions(
                resolvedName,
                definitionLimit,
                request.Lang,
                request.PathPatterns,
                request.ExcludePathPatterns,
                request.ExcludeTests);
        }
        return resolution;
    }

    private static bool IsLogicalPartialFamilyRoot(
        bool hasResolvedIdentityGraph,
        ImpactDefinitionResolution resolution,
        IReadOnlyList<SymbolResult> definitions)
        => hasResolvedIdentityGraph
           && resolution.LogicalCount == 1
           && definitions.Count == 1
           && definitions[0].Lang == "csharp"
           && definitions[0].PartialFamilyId != null
           && resolution.PhysicalSymbolIds.Count > 0;

    private static HashSet<long> ResolveQualifiedCSharpRootIds(
        bool canResolveQualifiedCSharpIdentity,
        bool isLogicalPartialFamily,
        ImpactDefinitionResolution resolution,
        IReadOnlyList<SymbolResult> definitions)
        => (canResolveQualifiedCSharpIdentity || isLogicalPartialFamily)
           && definitions.Count > 0
           && definitions.All(static definition => definition.Lang == "csharp")
           && definitions.All(static definition => definition.SymbolId != null)
           && (isLogicalPartialFamily || resolution.LogicalCount == definitions.Count)
            ? resolution.PhysicalSymbolIds.ToHashSet()
            : [];

    private static long? ResolveAmbiguousMRootId(
        bool hasResolvedIdentityGraph,
        string? lang,
        IReadOnlyList<SymbolResult> definitions)
        => hasResolvedIdentityGraph
           && definitions.Count == 1
           && lang is "matlab" or "objc"
           && string.Equals(definitions[0].Lang, lang, StringComparison.Ordinal)
            ? definitions[0].SymbolId
            : null;

    private ImpactPathNode CreateImpactRootPathNode(
        string resolvedName,
        long? symbolId,
        string? lang,
        bool isLogicalPartialFamily,
        IReadOnlyList<SymbolResult> definitions)
    {
        var node = ResolveImpactPathNode(
            resolvedName,
            symbolId,
            kind: null,
            lang,
            referencePath: null,
            referenceLine: null);
        if (!isLogicalPartialFamily)
            return node;

        var representative = definitions[0];
        node.SymbolId = null;
        node.Name = resolvedName;
        node.Kind = representative.Kind;
        node.Lang = representative.Lang;
        node.DefinitionPath = representative.Path;
        node.DefinitionLine = representative.Line;
        node.Container = representative.ContainerQualifiedName ?? representative.ContainerName;
        node.PartialFamilyId = representative.PartialFamilyId;
        node.LogicalTargetKey = $"partial|{representative.PartialFamilyId}";
        return node;
    }

    private List<CallerResult> ReadImpactCallerPage(
        in ImpactTraversalFrontierNode node,
        ImpactTraversalRequest request,
        int pageSize,
        int pageOffset,
        bool includeAmbiguousMSource)
    {
        IReadOnlyList<long>? targetIds = node.TargetSymbolIds is { Count: > 0 }
            ? node.TargetSymbolIds
            : node.SymbolId is long symbolId
                ? [symbolId]
                : null;
        return GetCallersExactCore(
            node.Symbol,
            pageSize,
            pageOffset,
            request.Lang,
            request.PathPatterns,
            request.ExcludePathPatterns,
            request.ExcludeTests,
            targetIds,
            includeAmbiguousMSource,
            request.IncludeMemberReads);
    }

    private int GetImpactGraphStateEntryBudget(int limit)
    {
        if (ImpactGraphStateEntryBudgetForTesting is int testBudget)
            return testBudget;
        var limitScaled = Math.Max(1, limit) * 200;
        return Math.Max(1024, Math.Min(DefaultImpactGraphStateEntryBudget, limitScaled));
    }

    private int GetImpactBoundaryCallerProbeBudget()
        => ImpactBoundaryCallerProbeBudgetForTesting ?? ImpactBoundaryCallerProbeBudget;
}
