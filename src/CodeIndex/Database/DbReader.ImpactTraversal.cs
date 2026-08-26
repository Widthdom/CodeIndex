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
        bool IncludeMemberReads,
        DefinitionResult? SelectedDefinition);

    private sealed record ImpactTraversalRoot(
        string ResolvedName,
        HashSet<string> DefinitionPaths,
        bool HasResolvedIdentityGraph,
        bool IsLogicalPartialFamily,
        HashSet<long> IdentitySymbolIds,
        IReadOnlyList<long>? InitialTargetSymbolIds,
        bool IncludeAmbiguousMSource,
        bool InitiallyTruncated,
        string NodeKey,
        ImpactPathNode? PathNode);

    private sealed record ImpactIdentityRootSignal(
        bool Available,
        string? UnavailableReason,
        string EvidenceConfidence,
        bool ResolutionTruncated = false);

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
            includeMemberReads,
            SelectedDefinition: null);
        var root = ResolveImpactTraversalRoot(request);
        if (root == null)
            return ([], false, null, ImpactTerminationReasons.Completed, []);

        return new ImpactTraversalEngine(this, request, root).Run();
    }

    internal (List<ImpactResult> Results, bool Truncated, string? TruncatedReason, string TerminationReason, List<ImpactCycleResult> Cycles) GetTransitiveCallersForCandidate(
        DefinitionResult definition,
        int maxDepth,
        int limit,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool withPaths,
        int resultOffset,
        bool includeMemberReads)
    {
        var request = new ImpactTraversalRequest(
            definition.Name,
            maxDepth,
            limit,
            NormalizeQueryLanguage(lang) ?? definition.Lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            withPaths,
            DefaultImpactPathsPerResult,
            resultOffset,
            includeMemberReads,
            definition);
        var root = ResolveImpactTraversalRoot(request);
        return root == null
            ? ([], false, null, ImpactTerminationReasons.Completed, [])
            : new ImpactTraversalEngine(this, request, root).Run();
    }

    private ImpactTraversalRoot? ResolveImpactTraversalRoot(ImpactTraversalRequest request)
    {
        if (request.SelectedDefinition is { SymbolId: long selectedSymbolId } selectedDefinition)
        {
            var selectedPathNode = request.WithPaths
                ? CreateImpactRootPathNode(
                    selectedDefinition.Name,
                    selectedSymbolId,
                    selectedDefinition.Lang,
                    isLogicalPartialFamily: false,
                    [selectedDefinition])
                : null;
            return new ImpactTraversalRoot(
                selectedDefinition.Name,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selectedDefinition.Path },
                HasResolvedIdentityGraph: true,
                IsLogicalPartialFamily: false,
                IdentitySymbolIds: [selectedSymbolId],
                InitialTargetSymbolIds: [selectedSymbolId],
                IncludeAmbiguousMSource: false,
                InitiallyTruncated: false,
                NodeKey: ImpactNodeIdentity.TraversalKey(selectedSymbolId, selectedDefinition.Name),
                PathNode: selectedPathNode);
        }

        var resolvedName = ResolveSymbolName(request.SymbolName, request.Lang);
        var hasResolvedIdentityGraph = HasCurrentReferenceIdentityContractForRead();
        var canResolveCSharpIdentity = hasResolvedIdentityGraph
            && request.Lang is null or "csharp";
        var rootDefinitionLimit = canResolveCSharpIdentity
            ? DefaultImpactGraphStateEntryBudget
            : request.Limit;
        var resolution = ResolveImpactRootDefinitions(request, resolvedName, rootDefinitionLimit);
        var definitions = resolution.Definitions;
        var useCSharpIdentity = canResolveCSharpIdentity
            && (definitions.Count == 0
                || definitions.All(static definition => definition.Lang == "csharp"));
        if (useCSharpIdentity && definitions.Count == 0 && request.Lang != null)
            return null;

        var definitionPaths = definitions
            .Select(static definition => definition.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isLogicalPartialFamily = IsLogicalPartialFamilyRoot(
            hasResolvedIdentityGraph,
            resolution,
            definitions);
        var identityIds = useCSharpIdentity
            ? resolution.PhysicalSymbolIds.ToHashSet()
            : [];
        if (useCSharpIdentity && identityIds.Count == 0 && definitions.Count > 0)
            return null;

        var traversalIds = identityIds.ToHashSet();
        var dispatchIdsTruncated = false;
        if (useCSharpIdentity)
        {
            traversalIds = ExpandCSharpPolymorphicDispatchSymbolIds(
                request.SymbolName,
                traversalIds,
                request.PathPatterns,
                request.ExcludePathPatterns,
                request.ExcludeTests,
                out dispatchIdsTruncated);
        }

        // Preserve the pre-existing ambiguity boundary for cross-language and other
        // non-identity roots. Same-language C# definitions are safely unioned by ID.
        // cross-language および identity 非対応 root の従来 ambiguity 境界を維持する。
        // 同一言語の C# definition 群だけは ID の和集合として安全に辿れる。
        if (hasResolvedIdentityGraph && definitionPaths.Count > 1 && !useCSharpIdentity)
            return null;

        var ambiguousMRootId = ResolveAmbiguousMRootId(
            hasResolvedIdentityGraph,
            request.Lang,
            definitions);
        if (ambiguousMRootId is long ambiguousId)
            identityIds.Add(ambiguousId);
        var initialTargetSymbolIds = traversalIds.Count > 0
            ? traversalIds.Order().ToArray()
            : null;
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
            initialTargetSymbolIds,
            ambiguousMRootId != null,
            InitiallyTruncated: useCSharpIdentity
                && !isLogicalPartialFamily
                && (resolution.PhysicalSymbolIdsTruncated
                    || resolution.LogicalCount > definitions.Count
                    || dispatchIdsTruncated),
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

    private ImpactIdentityRootSignal ResolveImpactIdentityRootSignal(
        ImpactDefinitionResolution resolution,
        string? lang)
    {
        if (lang is not (null or "csharp"))
        {
            return new ImpactIdentityRootSignal(
                Available: true,
                UnavailableReason: null,
                EvidenceConfidence: "language_graph");
        }
        if (lang == null
            && resolution.Definitions.Count > 0
            && resolution.Definitions.All(static definition => definition.Lang != "csharp"))
        {
            return new ImpactIdentityRootSignal(
                Available: true,
                UnavailableReason: null,
                EvidenceConfidence: "language_graph");
        }
        if (!HasCurrentReferenceIdentityContractForRead())
        {
            return new ImpactIdentityRootSignal(
                Available: false,
                UnavailableReason: "reference_identity_unavailable",
                EvidenceConfidence: "name_fallback");
        }
        if (resolution.PhysicalCount == 0)
        {
            return new ImpactIdentityRootSignal(
                Available: false,
                UnavailableReason: "no_identity_backed_root",
                EvidenceConfidence: "no_identity_root");
        }
        var resolutionTruncated = resolution.PhysicalSymbolIdsTruncated;

        return new ImpactIdentityRootSignal(
            Available: true,
            UnavailableReason: null,
            EvidenceConfidence: resolutionTruncated
                ? "identity_backed_partial"
                : "identity_backed",
            ResolutionTruncated: resolutionTruncated);
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
                : request.Lang == null && HasCurrentReferenceIdentityContractForRead()
                    ? []
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
            requireAuthoritativeIdentity: request.SelectedDefinition != null,
            includeAmbiguousMSource: includeAmbiguousMSource,
            includeMemberReads: request.IncludeMemberReads);
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
