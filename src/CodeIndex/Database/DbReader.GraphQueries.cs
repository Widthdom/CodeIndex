using CodeIndex.Indexer;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed record CallerIdentityResolution(
        IReadOnlyList<long>? SymbolIds,
        bool? IdentityRootAvailable,
        string? IdentityRootUnavailableReason,
        string? GraphEvidenceConfidence,
        bool ResolutionTruncated = false);

    private static readonly string CSharpCommonQualifiedMemberCallNamesSql = string.Join(
        ", ",
        CSharpReferenceExtractor.CommonQualifiedMemberCallNames
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(static name => $"'{name}'"));

    private string BuildCSharpBareMemberReferenceFilter(
        string query,
        string? lang,
        string fileAlias,
        string referenceAlias,
        bool includeQualifiedCommonCalls)
    {
        if (includeQualifiedCommonCalls
            || !ShouldFilterCSharpQualifiedCommonBareMemberQuery(query, lang))
            return string.Empty;

        return BuildCSharpQualifiedCommonCallNoiseFilter(fileAlias, referenceAlias);
    }

    private string BuildCSharpQualifiedCommonCallNoiseFilter(
        string fileAlias,
        string referenceAlias)
    {
        // Legacy read-only indexes cannot run the migrations that added resolution
        // evidence. Preserve their pre-filter graph behavior instead of emitting SQL
        // against columns they do not have.
        // 読み取り専用の旧 index は resolution evidence 列を追加できないため、
        // 存在しない列を参照せず従来の graph 結果へフォールバックする。
        if (!_referenceColumns.Contains("target_qualifier")
            || !_referenceColumns.Contains("resolution_state"))
        {
            return string.Empty;
        }

        return $" AND NOT ({fileAlias}.lang = 'csharp' AND {referenceAlias}.reference_kind = 'call' AND {referenceAlias}.symbol_name IN ({CSharpCommonQualifiedMemberCallNamesSql}) AND {referenceAlias}.target_qualifier IS NOT NULL AND COALESCE({referenceAlias}.resolution_state, 'unresolved') NOT IN ('resolved', 'resolved_group'))";
    }

    private static bool ShouldFilterCSharpQualifiedCommonBareMemberQuery(string query, string? lang)
    {
        return (lang == null || string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase))
            && !SqlNameResolver.HasQualifier(query);
    }

    /// <summary>
    /// Find callers for a referenced symbol.
    /// 指定シンボルを呼び出している呼び出し元を探す。
    /// </summary>
    public List<CallerResult> GetCallers(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, ReferenceRankMode rankMode = ReferenceRankMode.Weighted, bool excludeSelfReferences = false, int offset = 0, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
        => GetCallersCore(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, rawKinds, rankMode, excludeSelfReferences, offset, includeQualifiedCommonCalls, includeMemberReads, targetSymbolId: null);

    internal List<CallerResult> GetCallersForCandidate(
        DefinitionResult definition,
        int limit,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        int offset = 0,
        string? referenceKind = null,
        bool rawKinds = false,
        ReferenceRankMode rankMode = ReferenceRankMode.Weighted,
        bool excludeSelfReferences = false,
        bool includeQualifiedCommonCalls = false,
        bool includeMemberReads = false)
        => GetCallersCore(
            definition.Name,
            limit,
            definition.Lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact: true,
            rawKinds,
            rankMode,
            excludeSelfReferences,
            offset,
            includeQualifiedCommonCalls,
            includeMemberReads,
            targetSymbolId: definition.SymbolId);

    internal QueryCountResult CountCallersForCandidate(
        DefinitionResult definition,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        string? referenceKind = null,
        bool rawKinds = false,
        bool includeQualifiedCommonCalls = false,
        bool includeMemberReads = false)
    {
        if (definition.SymbolId is not long symbolId || !HasTable("symbol_reference_candidates"))
            return new QueryCountResult(0, 0);

        return CountCallersTotalCore(
            definition.Name,
            definition.Lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact: true,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            symbolId);
    }

    private List<CallerResult> GetCallersCore(string query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, bool rawKinds, ReferenceRankMode rankMode, bool excludeSelfReferences, int offset, bool includeQualifiedCommonCalls, bool includeMemberReads, long? targetSymbolId)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return new List<CallerResult>();
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable)
            return new List<CallerResult>();
        var callerIdentity = ResolveCallerIdentity(
            query,
            lang,
            exact,
            targetSymbolId);
        if (callerIdentity.SymbolIds is { Count: 0 } && lang != null)
            return [];

        var request = CreateGraphReferenceQueryRequest(
            query,
            limit,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            identitySymbolId: targetSymbolId,
            callerIdentity.SymbolIds,
            excludeSelfReferences,
            offset);
        var plan = BuildGraphReferenceQueryPlan(
            CallerGraphReferenceDirection,
            request,
            GraphReferenceQueryShape.List,
            rankMode);
        return ExecuteGraphReferenceList(plan, ProjectCallerResult);
    }

    internal IReadOnlyList<long> GetCallerGraphIdentityCandidates(
        string query,
        string? lang,
        string? referenceKind,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool exact,
        bool rawKinds,
        bool includeQualifiedCommonCalls,
        bool includeMemberReads)
    {
        if (string.IsNullOrWhiteSpace(query)
            || IsBareVerbatimQueryToken(query)
            || !_hasReferencesTable)
        {
            return [];
        }

        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query;
        var callerIdentity = ResolveCallerIdentity(query, lang, exact, targetSymbolId: null);
        if (callerIdentity.SymbolIds is { Count: 0 } && lang != null)
            return [];

        var request = CreateGraphReferenceQueryRequest(
            query,
            GraphIdentityCandidateLimit + 1,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            callerIdentitySymbolIds: callerIdentity.SymbolIds);
        var plan = BuildGraphReferenceQueryPlan(
            CallerGraphReferenceDirection,
            request,
            GraphReferenceQueryShape.IdentityCandidates);
        return ExecuteGraphReferenceIdentityCandidates(plan);
    }

    public int CountCallers(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return 0;
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable)
            return 0;
        var callerIdentity = ResolveCallerIdentity(
            query,
            lang,
            exact,
            targetSymbolId: null);
        if (callerIdentity.SymbolIds is { Count: 0 } && lang != null)
            return 0;

        var request = CreateGraphReferenceQueryRequest(
            query,
            limit,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            callerIdentitySymbolIds: callerIdentity.SymbolIds);
        var plan = BuildGraphReferenceQueryPlan(
            CallerGraphReferenceDirection,
            request,
            GraphReferenceQueryShape.LimitedCount);
        return ExecuteGraphReferenceLimitedCount(plan);
    }

    public QueryCountResult CountCallersTotal(string query, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
        => CountCallersTotalCore(
            query,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            targetSymbolId: null);

    private QueryCountResult CountCallersTotalCore(
        string query,
        string? lang,
        string? referenceKind,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool exact,
        bool rawKinds,
        bool includeQualifiedCommonCalls,
        bool includeMemberReads,
        long? targetSymbolId)
    {
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        var callerIdentity = ResolveCallerIdentity(
            query,
            lang,
            exact,
            targetSymbolId);
        if (callerIdentity.SymbolIds is { Count: 0 } && lang != null)
        {
            return new QueryCountResult(0, 0)
            {
                IdentityRootAvailable = callerIdentity.IdentityRootAvailable,
                IdentityRootUnavailableReason = callerIdentity.IdentityRootUnavailableReason,
                GraphEvidenceConfidence = callerIdentity.GraphEvidenceConfidence,
                IdentityRootResolutionTruncated = callerIdentity.ResolutionTruncated,
            };
        }

        var request = CreateGraphReferenceQueryRequest(
            query,
            limit: 0,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            identitySymbolId: targetSymbolId,
            callerIdentitySymbolIds: callerIdentity.SymbolIds);
        var plan = BuildGraphReferenceQueryPlan(
            CallerGraphReferenceDirection,
            request,
            GraphReferenceQueryShape.TotalCount);
        var result = ExecuteGraphReferenceTotalCount(plan);
        return result with
        {
            IdentityRootAvailable = callerIdentity.IdentityRootAvailable,
            IdentityRootUnavailableReason = callerIdentity.IdentityRootUnavailableReason,
            GraphEvidenceConfidence = callerIdentity.GraphEvidenceConfidence,
            IdentityRootResolutionTruncated = callerIdentity.ResolutionTruncated,
        };
    }

    private CallerIdentityResolution ResolveCallerIdentity(
        string query,
        string? lang,
        bool exact,
        long? targetSymbolId)
    {
        if (targetSymbolId is long candidateTargetSymbolId)
        {
            return new CallerIdentityResolution(
                [candidateTargetSymbolId],
                IdentityRootAvailable: true,
                IdentityRootUnavailableReason: null,
                GraphEvidenceConfidence: "identity_backed");
        }
        if (!exact)
        {
            return new CallerIdentityResolution(
                SymbolIds: null,
                IdentityRootAvailable: null,
                IdentityRootUnavailableReason: null,
                GraphEvidenceConfidence: null);
        }
        // The persisted target-identity contract currently covers C# caller edges.
        // Other graph languages retain their existing language-specific exact matching;
        // treating their lack of C#-style candidates as an unresolved symbol would erase
        // SQL, solution/MSBuild, stylesheet, and script graph semantics.
        // 永続化された target identity 契約の対象は現時点では C# caller edge。
        // 他言語で C# 型 candidate が無いことを未解決扱いすると、SQL、solution/MSBuild、
        // stylesheet、script 固有の exact graph 意味論を消してしまうため従来経路を維持する。
        if (lang is not (null or "csharp"))
        {
            return new CallerIdentityResolution(
                SymbolIds: null,
                IdentityRootAvailable: null,
                IdentityRootUnavailableReason: null,
                GraphEvidenceConfidence: null);
        }
        if (lang == null && ComputeCssScssVariableAlias(query) != null)
        {
            return new CallerIdentityResolution(
                SymbolIds: null,
                IdentityRootAvailable: null,
                IdentityRootUnavailableReason: null,
                GraphEvidenceConfidence: null);
        }
        if (!HasCurrentReferenceIdentityContractForRead())
        {
            return new CallerIdentityResolution(
                SymbolIds: null,
                IdentityRootAvailable: false,
                IdentityRootUnavailableReason: "reference_identity_unavailable",
                GraphEvidenceConfidence: "name_fallback");
        }

        var resolution = ResolveImpactDefinitions(
            query,
            DefaultImpactGraphStateEntryBudget,
            "csharp",
            pathPatterns: null,
            excludePathPatterns: null,
            excludeTests: false);
        if (resolution.Definitions.Count == 0)
        {
            if (lang == null)
            {
                var languageResolution = ResolveImpactDefinitions(
                    query,
                    DefaultImpactGraphStateEntryBudget,
                    lang: null,
                    pathPatterns: null,
                    excludePathPatterns: null,
                    excludeTests: false);
                if (languageResolution.Definitions.Count > 0)
                {
                    return new CallerIdentityResolution(
                        SymbolIds: [],
                        IdentityRootAvailable: true,
                        IdentityRootUnavailableReason: null,
                        GraphEvidenceConfidence: "language_graph");
                }
            }
            return new CallerIdentityResolution(
                SymbolIds: [],
                IdentityRootAvailable: false,
                IdentityRootUnavailableReason: "no_identity_backed_root",
                GraphEvidenceConfidence: "no_identity_root");
        }

        var isLogicalPartialFamily = IsLogicalPartialFamilyRoot(
            hasResolvedIdentityGraph: true,
            resolution,
            resolution.Definitions);
        var resolutionTruncated = resolution.PhysicalSymbolIdsTruncated
            || (!isLogicalPartialFamily && resolution.LogicalCount > resolution.Definitions.Count);

        var symbolIds = resolution.PhysicalSymbolIds.ToHashSet();
        if (resolution.Definitions.All(static definition => definition.Lang == "csharp"))
        {
            symbolIds = ExpandCSharpPolymorphicDispatchSymbolIds(
                query,
                symbolIds,
                pathPatterns: null,
                excludePathPatterns: null,
                excludeTests: false,
                out var dispatchIdsTruncated);
            if (dispatchIdsTruncated)
                resolutionTruncated = true;
        }

        return new CallerIdentityResolution(
            symbolIds.Order().ToArray(),
            IdentityRootAvailable: true,
            IdentityRootUnavailableReason: null,
            GraphEvidenceConfidence: resolutionTruncated
                ? "identity_backed_partial"
                : "identity_backed",
            ResolutionTruncated: resolutionTruncated);
    }

    /// <summary>
    /// Find callees used by a caller/container symbol.
    /// 呼び出し元シンボルが使っている呼び出し先を探す。
    /// </summary>
    public List<CalleeResult> GetCallees(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, ReferenceRankMode rankMode = ReferenceRankMode.Weighted, int offset = 0, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
        => GetCalleesCore(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, rawKinds, rankMode, offset, includeQualifiedCommonCalls, includeMemberReads, sourceSymbolId: null);

    internal List<CalleeResult> GetCalleesForCandidate(
        DefinitionResult definition,
        int limit,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        int offset = 0,
        string? referenceKind = null,
        bool rawKinds = false,
        ReferenceRankMode rankMode = ReferenceRankMode.Weighted,
        bool includeQualifiedCommonCalls = false,
        bool includeMemberReads = false)
    {
        if (definition.SymbolId is not long symbolId || !_referenceColumns.Contains("source_symbol_id"))
            return [];

        return GetCalleesCore(
            definition.Name,
            limit,
            definition.Lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact: true,
            rawKinds,
            rankMode,
            offset,
            includeQualifiedCommonCalls,
            includeMemberReads,
            sourceSymbolId: symbolId);
    }

    internal QueryCountResult CountCalleesForCandidate(
        DefinitionResult definition,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        string? referenceKind = null,
        bool rawKinds = false,
        bool includeQualifiedCommonCalls = false,
        bool includeMemberReads = false)
    {
        if (definition.SymbolId is not long symbolId || !_referenceColumns.Contains("source_symbol_id"))
            return new QueryCountResult(0, 0);

        return CountCalleesTotalCore(
            definition.Name,
            definition.Lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact: true,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            symbolId);
    }

    private List<CalleeResult> GetCalleesCore(string query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, bool rawKinds, ReferenceRankMode rankMode, int offset, bool includeQualifiedCommonCalls, bool includeMemberReads, long? sourceSymbolId)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return new List<CalleeResult>();
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable)
            return new List<CalleeResult>();

        var request = CreateGraphReferenceQueryRequest(
            query,
            limit,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            identitySymbolId: sourceSymbolId,
            offset: offset);
        var plan = BuildGraphReferenceQueryPlan(
            CalleeGraphReferenceDirection,
            request,
            GraphReferenceQueryShape.List,
            rankMode);
        return ExecuteGraphReferenceList(plan, ProjectCalleeResult);
    }

    internal IReadOnlyList<long> GetCalleeGraphIdentityCandidates(
        string query,
        string? lang,
        string? referenceKind,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool exact,
        bool rawKinds,
        bool includeQualifiedCommonCalls,
        bool includeMemberReads)
    {
        if (string.IsNullOrWhiteSpace(query)
            || IsBareVerbatimQueryToken(query)
            || !_hasReferencesTable)
        {
            return [];
        }

        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query;
        var request = CreateGraphReferenceQueryRequest(
            query,
            GraphIdentityCandidateLimit + 1,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads);
        var plan = BuildGraphReferenceQueryPlan(
            CalleeGraphReferenceDirection,
            request,
            GraphReferenceQueryShape.IdentityCandidates);
        return ExecuteGraphReferenceIdentityCandidates(plan);
    }

    public int CountCallees(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return 0;
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable)
            return 0;

        var request = CreateGraphReferenceQueryRequest(
            query,
            limit,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads);
        var plan = BuildGraphReferenceQueryPlan(
            CalleeGraphReferenceDirection,
            request,
            GraphReferenceQueryShape.LimitedCount);
        return ExecuteGraphReferenceLimitedCount(plan);
    }

    public QueryCountResult CountCalleesTotal(string query, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, bool rawKinds = false, bool includeQualifiedCommonCalls = false, bool includeMemberReads = false)
        => CountCalleesTotalCore(
            query,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            sourceSymbolId: null);

    private QueryCountResult CountCalleesTotalCore(
        string query,
        string? lang,
        string? referenceKind,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool exact,
        bool rawKinds,
        bool includeQualifiedCommonCalls,
        bool includeMemberReads,
        long? sourceSymbolId)
    {
        lang = NormalizeQueryLanguage(lang);
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);

        var request = CreateGraphReferenceQueryRequest(
            query,
            limit: 0,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            rawKinds,
            includeQualifiedCommonCalls,
            includeMemberReads,
            identitySymbolId: sourceSymbolId);
        var plan = BuildGraphReferenceQueryPlan(
            CalleeGraphReferenceDirection,
            request,
            GraphReferenceQueryShape.TotalCount);
        return ExecuteGraphReferenceTotalCount(plan);
    }

    /// <summary>
    /// Resolve a user-provided symbol name to its actual indexed casing via definition lookup.
    /// Prefers exact-case match, then falls back to case-insensitive. Only considers
    /// graph-supported languages. Returns the original input if no match is found.
    /// ユーザ入力のシンボル名を定義検索で実際のインデックス済みケーシングに解決する。
    /// 完全一致を優先し、なければ大文字小文字無視でフォールバック。graph 対応言語のみ対象。
    /// 見つからなければ元の入力をそのまま返す。
    /// </summary>
    private string ResolveSymbolName(string symbolName, string? lang)
    {
        var normalizedSymbolName = NormalizeCSharpVerbatimQuery(symbolName, lang) ?? symbolName;
        // Exact lookup mirrors the leaf `--exact` readers: folded equality when FoldReady,
        // ASCII `COLLATE NOCASE` fallback on legacy / partial-backfill DBs.
        // No path/test filters — definitions outside caller scope must still be found.
        // Only considers graph-supported languages to avoid resolving to unsupported ones.
        // FoldReady なら folded equality、legacy DB では ASCII `COLLATE NOCASE` にフォールバック。
        var normalizedName = SqlNameResolver.NormalizeQualifiedName(normalizedSymbolName);
        var leafName = SqlNameResolver.GetLeafName(normalizedSymbolName);
        var segmentCount = SqlNameResolver.GetSegmentCount(normalizedSymbolName);
        var allowLeafFallback = !SqlNameResolver.HasQualifier(normalizedSymbolName);
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "resolveLang");
        var nameCondition = _foldReady
            ? allowLeafFallback
                ? "(s.name_folded = @nameFolded OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @segmentCount AND sql_normalize_name_folded(s.name) = @normalizedNameFolded) OR sql_leaf_name_folded(s.name) = @leafNameFolded)))"
                : "(s.name_folded = @nameFolded OR (f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name_folded(s.name) = @normalizedNameFolded))"
            : allowLeafFallback
                ? "(s.name = @name COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @segmentCount AND sql_normalize_name(s.name) = @normalizedName COLLATE NOCASE) OR sql_leaf_name(s.name) = @leafName COLLATE NOCASE)))"
                : "(s.name = @name COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name(s.name) = @normalizedName COLLATE NOCASE))";
        var csharpExplicitInterfaceClause = allowLeafFallback
            ? BuildCSharpExplicitInterfaceShortAliasMatchSql("name")
            : BuildCSharpExplicitInterfaceIdentityMatchSql("name");
        nameCondition = $"({nameCondition} OR {csharpExplicitInterfaceClause})";
        cmd.CommandText = @"SELECT s.name FROM symbols s JOIN files f ON s.file_id = f.id
                            WHERE " + nameCondition + @"
                              AND " + supportedLangFilter + @"
                            ORDER BY CASE
                                         WHEN s.name = @name THEN 0
                                         WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name(s.name) = @normalizedName THEN 1
                                         WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name_folded(s.name) = @normalizedNameFolded THEN 2
                                         WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name(s.name) = @leafName THEN 3
                                         WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @leafNameFolded THEN 4
                                         ELSE 5
                                     END LIMIT 1";
        SqliteCommandPolicy.Add(cmd, "@name", normalizedSymbolName);
        SqliteCommandPolicy.Add(cmd, "@normalizedName", normalizedName);
        SqliteCommandPolicy.Add(cmd, "@normalizedNameFolded", NameFold.Fold(normalizedName) ?? normalizedName);
        SqliteCommandPolicy.Add(cmd, "@leafName", leafName);
        SqliteCommandPolicy.Add(cmd, "@leafNameFolded", NameFold.Fold(leafName) ?? leafName);
        SqliteCommandPolicy.Add(cmd, "@nameLeaf", leafName);
        SqliteCommandPolicy.Add(cmd, "@nameLeafFolded", NameFold.Fold(leafName) ?? leafName);
        SqliteCommandPolicy.Add(cmd, "@segmentCount", segmentCount);
        SqliteCommandPolicy.Add(cmd, "@allowLeafFallback", allowLeafFallback ? 1 : 0);
        AddCSharpExplicitInterfaceIdentityQueryParameter(cmd, "name", normalizedSymbolName);
        if (_foldReady)
            SqliteCommandPolicy.Add(cmd, "@nameFolded", NameFold.Fold(normalizedSymbolName) ?? normalizedSymbolName);
        using var reader = cmd.ExecuteTrackedReader();
        return reader.TrackedRead() ? reader.GetString(0) : symbolName;
    }

    /// <summary>
    /// Find exact-match callers for BFS traversal. Uses per-row case sensitivity
    /// and filters to graph-supported languages only (preventing stale edges from
    /// unsupported languages leaking into results on pre-upgrade databases). The
    /// SQL query applies the requested LIMIT/OFFSET so callers do not materialize
    /// a larger intermediate page than they asked for.
    /// BFS 走査用の完全一致 caller 検索。行ごとの case sensitivity 判定、
    /// かつ graph 対応言語のみにフィルタ（アップグレード前 DB の古いエッジ漏れを防止）。
    /// SQL 側で要求された LIMIT/OFFSET を適用し、呼び出し側が要求以上の中間ページを
    /// materialize しないようにする。
    /// </summary>
    private List<CallerResult> GetCallersExactCore(string symbolName, int limit, int offset, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<long>? targetSymbolIds, bool includeAmbiguousMSource, bool includeMemberReads)
    {
        if (!_hasReferencesTable) return new List<CallerResult>();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var selfReferenceSql = _referenceColumns.Contains("is_self_reference") ? "r.is_self_reference" : "0";
        var mutualRecursionSql = _referenceColumns.Contains("is_mutual_recursion") ? "r.is_mutual_recursion" : "0";
        var sourceSymbolIdSql = _referenceColumns.Contains("source_symbol_id") ? "r.source_symbol_id" : "NULL";
        var referenceSpanLengthSql = _referenceColumns.Contains("span_length") ? "r.span_length" : "NULL";
        var hasIdentityTargetContract = targetSymbolIds != null
                                        && _referenceColumns.Contains("target_symbol_id")
                                        && _referenceColumns.Contains("resolution_state")
                                        && HasTable("symbol_reference_candidates");
        var hasIdentityTargetScope = hasIdentityTargetContract
                                     && targetSymbolIds is { Count: > 0 };
        const string targetSymbolIdsSql = "SELECT CAST(value AS INTEGER) FROM json_each(@targetSymbolIdsJson)";
        var targetSymbolIdSql = hasIdentityTargetScope
            ? $@"CASE
                    WHEN r.resolution_state = 'resolved'
                         AND EXISTS (
                             SELECT 1
                             FROM symbol_reference_candidates projected_identity_candidate
                             WHERE projected_identity_candidate.reference_id = r.id
                               AND projected_identity_candidate.symbol_id IN ({targetSymbolIdsSql})
                         )
                    THEN r.target_symbol_id
                    ELSE NULL
                END"
            : _referenceColumns.Contains("target_symbol_id") ? "r.target_symbol_id" : "NULL";

        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "callerLang");

        // Exact caller matching mirrors the leaf `--exact` readers: folded equality when
        // FoldReady, ASCII `COLLATE NOCASE` fallback on legacy / partial-backfill DBs.
        // ResolveSymbolName() already normalizes the root symbol first, so this catches
        // caller rows whose stored callee casing differs from the resolved definition.
        // caller 側も leaf `--exact` と同じく FoldReady なら folded equality、legacy DB では
        // `COLLATE NOCASE` fallback。definition と caller 行の casing 差もここで吸収する。
        var allowSqlLeafFallback = !SqlNameResolver.HasQualifier(symbolName);
        var allowCSharpQualifiedContextMatch = SqlNameResolver.HasQualifier(symbolName)
            && !HasQualifiedSymbolDefinition(symbolName, lang, pathPatterns, excludePathPatterns, excludeTests);
        var allowQualifiedLeafFallback = HasSingleQualifiedSymbolDefinition(symbolName, lang, pathPatterns, excludePathPatterns, excludeTests);
        var polymorphicCSharpSymbolNames = lang is null or "csharp"
            ? GetCSharpPolymorphicDispatchSymbolNames(symbolName)
            : [];
        var polymorphicNameCondition = polymorphicCSharpSymbolNames.Count == 0
            ? string.Empty
            : _foldReady
                ? " OR (f.lang = 'csharp' AND r.symbol_name_folded IN (" + string.Join(", ", polymorphicCSharpSymbolNames.Select((_, i) => $"@polymorphicSymbolNameFolded{i}")) + "))"
                : " OR (f.lang = 'csharp' AND r.symbol_name COLLATE NOCASE IN (" + string.Join(", ", polymorphicCSharpSymbolNames.Select((_, i) => $"@polymorphicSymbolName{i}")) + "))";
        var namePredicate = _foldReady
            ? allowSqlLeafFallback
                ? "(" + BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@symbolNameFolded") + " OR (f.lang = 'sql' AND r.symbol_name_folded = @symbolNameLeafFolded)" + polymorphicNameCondition + " OR (f.lang = 'solution' AND r.reference_kind = 'project_reference' AND r.container_name = @symbolName COLLATE NOCASE))"
                : "(((f.lang = 'sql') AND sql_context_has_name_folded_at(" + contextSql + @", @symbolName, r.column_number) = 1) OR ((f.lang != 'sql') AND " + BuildPersistedFoldedNameMatchSql("r.symbol_name_folded", "@symbolNameFolded") + ") OR " + BuildCSharpQualifiedContextFallbackSql(BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: true, like: false)) + " OR " + BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: true) + polymorphicNameCondition + " OR (f.lang = 'solution' AND r.reference_kind = 'project_reference' AND r.container_name = @symbolName COLLATE NOCASE))"
            : allowSqlLeafFallback
                ? "(r.symbol_name = @symbolName COLLATE NOCASE OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@symbolName) COLLATE NOCASE)" + polymorphicNameCondition + " OR (f.lang = 'solution' AND r.reference_kind = 'project_reference' AND r.container_name = @symbolName COLLATE NOCASE))"
                : "(((f.lang = 'sql') AND sql_context_has_name_at(" + contextSql + @", @symbolName, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @symbolName COLLATE NOCASE) OR " + BuildCSharpQualifiedContextFallbackSql(BuildQualifiedContextMatchSql(contextSql, "r.column_number", folded: false, like: false)) + " OR " + BuildQualifiedLeafFallbackSql("r.symbol_name", "r.symbol_name_folded", folded: false) + polymorphicNameCondition + " OR (f.lang = 'solution' AND r.reference_kind = 'project_reference' AND r.container_name = @symbolName COLLATE NOCASE))";
        var nameCondition = "\n              AND " + namePredicate;
        // Identity-scoped traversal admits only references whose candidate set contains the
        // requested canonical target. Unresolved/ambiguous same-leaf rows remain available to
        // broad reference discovery, but they are not confirmed call-graph edges.
        // identity scope の traversal は candidate set が要求 target を含む参照だけを採用する。
        // unresolved/ambiguous な同名 leaf は広い reference 探索には残すが、確定 call graph edge
        // としては扱わない。
        var identityTargetPredicate = $@"EXISTS (
                  SELECT 1
                  FROM symbol_reference_candidates identity_candidate
                  WHERE identity_candidate.reference_id = r.id
                    AND identity_candidate.symbol_id IN ({targetSymbolIdsSql})
                    AND r.resolution_state IN ('resolved', 'resolved_group')
              )";
        var targetCondition = !hasIdentityTargetContract
            ? nameCondition
            : targetSymbolIds!.Count == 0
                ? lang == null
                    ? "\n              AND f.lang != 'csharp'" + nameCondition
                    : "\n              AND 1 = 0"
                : lang == null
                    ? $@"
              AND ((f.lang = 'csharp' AND {identityTargetPredicate})
                   OR (f.lang != 'csharp' AND {namePredicate}))"
                    : $@"
              AND {identityTargetPredicate}";
        // impact BFS must share the call-graph contract with `callers`/`callees`/`hotspots`,
        // so event subscriptions (`Click += OnClick`) also participate in the transitive
        // caller chain. Metadata edges (`attribute`, `annotation`) stay excluded.
        // impact の BFS は `callers`/`callees`/`hotspots` と同じ call-graph 契約を共有し、
        // `subscribe` エッジ（`Click += OnClick` 等）も推移 caller に含める。`attribute` /
        // `annotation` のような metadata エッジは引き続き除外する。
        var callerContainerPredicate = BuildCallerContainerPredicate("f", "r");
        var sql = $@"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.reference_kind, r.line, r.column_number,
                       {sourceSymbolIdSql} AS source_symbol_id,
                       {targetSymbolIdSql} AS target_symbol_id,
                       MIN(CASE WHEN {referenceSpanLengthSql} > 0 THEN {referenceSpanLengthSql} ELSE NULL END) AS span_length,
                       MAX({selfReferenceSql}) AS is_self_reference,
                       MAX({mutualRecursionSql}) AS is_mutual_recursion
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id{referenceLineJoin}
                WHERE {callerContainerPredicate}
                  AND (r.reference_kind IN {CallGraphReferenceKindsSql}{(includeMemberReads ? " OR r.reference_kind = 'member_read'" : string.Empty)})
                  AND {supportedLangFilter}
                  {targetCondition}";
        if (lang != null)
        {
            sql += includeAmbiguousMSource
                ? " AND (f.lang = @lang OR f.lang = 'ambiguous_m')"
                : " AND f.lang = @lang";
        }
        sql += BuildCSharpBareMemberReferenceFilter(
            symbolName,
            lang,
            "f",
            "r",
            includeQualifiedCommonCalls: false);
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += @"
                GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.reference_kind, r.file_id, r.line, r.column_number, source_symbol_id, target_symbol_id
            ),
            ranked_references AS (
                SELECT r.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY path, lang, " + BuildCallerKindProjectionSql("r") + @",
                                        CASE WHEN lang = 'solution' AND reference_kind = 'project_reference' THEN path
                                             ELSE " + BuildCallerNameProjectionSql("r") + @" END,
                                        symbol_name, reference_kind, source_symbol_id
                           ORDER BY line,
                                    CASE WHEN column_number IS NULL THEN 1 ELSE 0 END,
                                    column_number,
                                    CASE WHEN span_length IS NULL OR span_length <= 0 THEN 1 ELSE 0 END,
                                    COALESCE(span_length, 0),
                                    COALESCE(target_symbol_id, -1)
                       ) AS location_rank
                FROM logical_references r
            )
            SELECT path, lang, " + BuildCallerKindProjectionSql("r") + @" AS container_kind,
                   CASE WHEN lang = 'solution' AND reference_kind = 'project_reference' THEN path
                        ELSE " + BuildCallerNameProjectionSql("r") + @" END AS container_name,
                   symbol_name,
                   reference_kind,
                   MAX(CASE WHEN location_rank = 1 THEN line END) AS first_line,
                   COALESCE(MAX(CASE WHEN location_rank = 1 THEN column_number END), 0) AS first_column,
                   MAX(CASE WHEN location_rank = 1 THEN span_length END) AS first_length,
                   COUNT(*) AS reference_count,
                   MAX(is_self_reference) AS is_self_reference,
                   MAX(is_mutual_recursion) AS is_mutual_recursion,
                   source_symbol_id,
                   CASE
                       WHEN COUNT(DISTINCT COALESCE(target_symbol_id, -1)) = 1
                       THEN MIN(target_symbol_id)
                       ELSE NULL
                   END AS target_symbol_id,
                   GROUP_CONCAT(DISTINCT target_symbol_id) AS target_symbol_ids
            FROM ranked_references r
            GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind, source_symbol_id";
        sql += $" ORDER BY {GetPathBucketOrderSql("r.path")}, reference_count DESC, r.path, COALESCE(r.container_name, ''), COALESCE(r.container_kind, ''), r.symbol_name, reference_kind, first_line, COALESCE(source_symbol_id, -1) LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@symbolName", symbolName);
        SqliteCommandPolicy.Add(cmd, "@aliasQuery", symbolName);
        AddQualifiedGraphQueryParameters(cmd, symbolName, allowQualifiedLeafFallback, allowCSharpQualifiedContextMatch);
        SqliteCommandPolicy.Add(cmd, "@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(symbolName)) ?? SqlNameResolver.GetLeafName(symbolName));
        SqliteCommandPolicy.Add(cmd, "@symbolNameLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(symbolName)) ?? SqlNameResolver.GetLeafName(symbolName));
        if (_foldReady)
            AddPersistedFoldedNameQueryParameters(cmd, "@symbolNameFolded", symbolName, lang);
        for (var i = 0; i < polymorphicCSharpSymbolNames.Count; i++)
        {
            if (_foldReady)
                SqliteCommandPolicy.Add(cmd, $"@polymorphicSymbolNameFolded{i}", NameFold.Fold(polymorphicCSharpSymbolNames[i]) ?? polymorphicCSharpSymbolNames[i]);
            else
                SqliteCommandPolicy.Add(cmd, $"@polymorphicSymbolName{i}", polymorphicCSharpSymbolNames[i]);
        }
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        if (hasIdentityTargetScope)
        {
            var targetSymbolIdValues = targetSymbolIds!
                .Select(static symbolId => symbolId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
            SqliteCommandPolicy.Add(cmd, "@targetSymbolIdsJson", JsonStringListCodec.Serialize(targetSymbolIdValues));
        }
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        SqliteCommandPolicy.Add(cmd, "@offset", offset);

        var results = new List<CallerResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new CallerResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                CallerKind = GetNullableString(reader, 2),
                CallerName = GetNullableString(reader, 3),
                CalleeName = reader.GetString(4),
                ReferenceKind = reader.GetString(5),
                ReferenceKinds = [reader.GetString(5)],
                ReferenceKindCounts = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [reader.GetString(5)] = reader.GetInt32(9),
                },
                FirstLine = reader.GetInt32(6),
                FirstColumn = reader.GetInt32(7),
                FirstLength = GetNullableInt32(reader, 8),
                ReferenceCount = reader.GetInt32(9),
                HasSelfReference = reader.GetInt32(10) != 0,
                HasMutualRecursion = reader.GetInt32(11) != 0,
                CallerSymbolId = reader.IsDBNull(12) ? null : reader.GetInt64(12),
                CalleeSymbolId = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                CalleeSymbolIds = reader.IsDBNull(14)
                    ? Array.Empty<long>()
                    : reader.GetString(14)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(long.Parse)
                        .Order()
                        .ToArray(),
            });
        }
        return results;
    }

    /// <summary>
    /// Analyze impact for a query by combining transitive callers with symbol-resolution
    /// metadata and a class-like file-dependency fallback when symbol-level callers are absent.
    /// The <paramref name="maxDepth"/> bound is inclusive (callers at depth 1..N are returned);
    /// <c>maxDepth: 0</c> short-circuits to symbol resolution only.
    /// impact 用に caller BFS と解決メタデータを束ね、class 系で caller 不在なら
    /// file dependency をフォールバックとして返す。<paramref name="maxDepth"/> は inclusive で
    /// N 指定時は depth 1〜N の caller を返し、<c>maxDepth: 0</c> は symbol 解決のみで終了する。
    /// </summary>
    public ImpactAnalysisResult AnalyzeImpact(string symbolName, int maxDepth = 5, int limit = 50, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool withPaths = false, int offset = 0, string? responseCollection = null, bool includeMemberReads = false, DefinitionResult? selectedDefinition = null)
    {
        lang = NormalizeQueryLanguage(lang);
        var resolvedName = selectedDefinition?.Name ?? ResolveSymbolName(symbolName, lang);
        var definitionOffset = string.Equals(responseCollection, "definitions", StringComparison.Ordinal) ? offset : 0;
        var definitionResolution = selectedDefinition != null
            ? ResolveSelectedImpactDefinition(selectedDefinition)
            : ResolveImpactDefinitions(symbolName, limit, lang, pathPatterns, excludePathPatterns, excludeTests, definitionOffset);
        if (selectedDefinition == null
            && definitionResolution.Definitions.Count == 0
            && !string.Equals(symbolName, resolvedName, StringComparison.Ordinal))
        {
            definitionResolution = ResolveImpactDefinitions(resolvedName, limit, lang, pathPatterns, excludePathPatterns, excludeTests, definitionOffset);
        }
        var definitions = definitionResolution.Definitions;
        var indexedPathComparer = GetIndexedPathComparer();
        var definitionPaths = definitions
            .Select(d => d.Path)
            .Distinct(indexedPathComparer)
            .ToList();
        var hasMultipleDefinitions = definitionResolution.PhysicalCount > 1;
        var fallbackDefinitions = definitionResolution.SinglePreciseDefinition != null
            ? [definitionResolution.SinglePreciseDefinition]
            : definitions.Where(d => IsPreciseImpactFallbackKind(d.Kind)).ToList();
        var fallbackDefinitionPaths = fallbackDefinitions
            .Select(d => d.Path)
            .Distinct(indexedPathComparer)
            .ToList();
        // Logical partial-family collapse is only safe when reference identity is current.
        // A stale graph cannot union every physical family path, so retain the physical
        // ambiguity guard instead of producing hints from only the representative file.
        // 論理 partial-family の集約は reference identity が current の場合だけ安全。
        // stale graph では全物理 family path を統合できないため、代表 file だけの hint を
        // 返さず、物理 definition の ambiguity guard を維持する。
        var hasMultipleFallbackDefinitions = _referenceIdentityContractCurrent
            ? definitionResolution.PreciseLogicalDefinitionCount > 1
            : definitionResolution.PreciseDefinitionCount > 1;
        var hasMultipleFallbackDefinitionFiles = definitionResolution.PreciseDefinitionFileCount > 1;
        var hasClassLikeDefinitions = definitionResolution.PreciseDefinitionCount > 0;
        var logicalPartialFamilyDefinition = _referenceIdentityContractCurrent
                                             && definitionResolution.LogicalCount == 1
                                             && definitions.Count == 1
                                             && definitions[0].Lang == "csharp"
                                             && definitions[0].PartialFamilyId != null
            ? definitions[0]
            : null;
        var identityRootSignal = ResolveImpactIdentityRootSignal(definitionResolution, lang);
        var traversalRootScope = identityRootSignal.UnavailableReason == "no_identity_backed_root"
            ? "name_only"
            : logicalPartialFamilyDefinition != null
            ? "logical_partial_family"
            : "symbol";
        var partialFamilyMemberCount = logicalPartialFamilyDefinition != null
            ? definitionResolution.PhysicalCount
            : (int?)null;
        var partialFamilyMemberRootCount = logicalPartialFamilyDefinition != null
            ? definitionResolution.PhysicalSymbolIds.Count
            : (int?)null;
        var partialFamilyMemberRootLimit = logicalPartialFamilyDefinition != null
            ? Math.Max(1, ImpactPartialFamilyMemberBudget)
            : (int?)null;
        var partialFamilyMemberRootTruncated = logicalPartialFamilyDefinition != null
            ? definitionResolution.PhysicalSymbolIdsTruncated
            : (bool?)null;
        var partialFamilyMemberRootOmitted = logicalPartialFamilyDefinition != null
            ? Math.Max(0, definitionResolution.PhysicalCount - definitionResolution.PhysicalSymbolIds.Count)
            : (int?)null;
        var referenceGraphComplete = IsReferenceGraphComplete(GetReferenceExtractionCapHits());

        if (maxDepth <= 0)
        {
            return new ImpactAnalysisResult
            {
                Query = symbolName,
                ResolvedName = resolvedName,
                ImpactMode = "none",
                Heuristic = !identityRootSignal.Available,
                MaxDepth = maxDepth,
                DefinitionCount = definitionResolution.PhysicalCount,
                DefinitionFileCount = definitionResolution.PhysicalFileCount,
                LogicalDefinitionCount = definitionResolution.LogicalCount,
                HintCount = 0,
                HasClassLikeDefinitions = hasClassLikeDefinitions,
                HasMultipleDefinitions = hasMultipleDefinitions,
                HasMultipleDefinitionFiles = definitionResolution.PhysicalFileCount > 1,
                TraversalRootScope = traversalRootScope,
                IdentityRootAvailable = identityRootSignal.Available,
                IdentityRootUnavailableReason = identityRootSignal.UnavailableReason,
                GraphEvidenceConfidence = identityRootSignal.EvidenceConfidence,
                IdentityRootResolutionTruncated = identityRootSignal.ResolutionTruncated,
                TraversalPartialFamilyId = logicalPartialFamilyDefinition?.PartialFamilyId,
                PartialFamilyMemberCount = partialFamilyMemberCount,
                PartialFamilyMemberRootCount = partialFamilyMemberRootCount,
                PartialFamilyMemberRootLimit = partialFamilyMemberRootLimit,
                PartialFamilyMemberRootTruncated = partialFamilyMemberRootTruncated,
                PartialFamilyMemberRootOmitted = partialFamilyMemberRootOmitted,
                Definitions = definitions,
                Callers = [],
                FileImpacts = [],
                Truncated = false,
                TruncatedReason = null,
                TerminationReason = ImpactTerminationReasons.Completed,
                CycleDetected = false,
                Cycles = null,
                GraphTableAvailable = _hasReferencesTable,
                ReferenceGraphComplete = referenceGraphComplete,
                ZeroResultReason = definitionResolution.PhysicalCount == 0 ? "no_matching_definition" : "depth_requested_zero",
                ImpactFailureChain = definitionResolution.PhysicalCount == 0
                    ? identityRootSignal.UnavailableReason == "no_identity_backed_root"
                        ? ["no_identity_backed_root", "definition_not_found", "depth_requested_zero"]
                        : ["definition_not_found", "depth_requested_zero"]
                    : ["depth_requested_zero"],
                SuggestionType = definitionResolution.PhysicalCount == 0 ? "resolution" : "precondition",
                Suggestion = definitionResolution.PhysicalCount == 0
                    ? "Try `cdidx definition <symbol>` to confirm the indexed name."
                    : "Use `cdidx impact <symbol> --max-hops 1` or higher to traverse callers.",
            };
        }

        var callerOffset = responseCollection is null || string.Equals(responseCollection, "callers", StringComparison.Ordinal)
            ? offset
            : 0;
        var (callers, truncated, truncatedReason, terminationReason, cycles) = selectedDefinition != null
            ? GetTransitiveCallersForCandidate(
                selectedDefinition,
                maxDepth,
                limit,
                lang,
                pathPatterns,
                excludePathPatterns,
                excludeTests,
                withPaths,
                callerOffset,
                includeMemberReads)
            : GetTransitiveCallers(symbolName, maxDepth, limit, lang, pathPatterns, excludePathPatterns, excludeTests, withPaths, resultOffset: callerOffset, includeMemberReads: includeMemberReads);
        var callerExistsBeforeOffset = false;
        if (callers.Count == 0 && callerOffset > 0)
        {
            var callerProbe = selectedDefinition != null
                ? GetTransitiveCallersForCandidate(
                    selectedDefinition,
                    maxDepth,
                    1,
                    lang,
                    pathPatterns,
                    excludePathPatterns,
                    excludeTests,
                    withPaths: false,
                    resultOffset: 0,
                    includeMemberReads)
                : GetTransitiveCallers(symbolName, maxDepth, 1, lang, pathPatterns, excludePathPatterns, excludeTests, withPaths: false, resultOffset: 0, includeMemberReads: includeMemberReads);
            callerExistsBeforeOffset = callerProbe.Results.Count > 0;
        }

        var impactMode = "callers";
        var fileImpacts = new List<FileDependencyResult>();
        string? zeroResultReason = null;
        List<string>? impactFailureChain = null;
        string? suggestionType = null;
        string? suggestion = null;
        var heuristic = !identityRootSignal.Available;

        if (callers.Count == 0 && !callerExistsBeforeOffset)
        {
            impactMode = "none";
            impactFailureChain = [];
            if (identityRootSignal.UnavailableReason == "no_identity_backed_root")
            {
                impactFailureChain.Add(identityRootSignal.UnavailableReason);
            }

            if (!_hasReferencesTable)
            {
                zeroResultReason = "graph_unavailable";
                impactFailureChain.Add("graph_unavailable");
                suggestionType = "precondition";
                suggestion = "Re-index with the current `cdidx` so symbol reference graph data is available.";
            }
            else
            {
                if (definitionResolution.PhysicalCount > 0
                    && definitionResolution.NonCallableDefinitionCount == definitionResolution.PhysicalCount)
                {
                    zeroResultReason = "non_callable_symbol_kind";
                    impactFailureChain.Add("callable_filter_fails");
                    suggestionType = "resolution";
                    suggestion = "Try `cdidx definition <symbol>` and then run `impact` on a specific callable member instead.";
                }
                else if (hasMultipleFallbackDefinitions)
                {
                    zeroResultReason = hasMultipleFallbackDefinitionFiles ? "multiple_definition_files" : "multiple_definitions";
                    impactFailureChain.Add(zeroResultReason);
                    suggestionType = "resolution";
                    suggestion = BuildImpactSuggestion(fallbackDefinitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: true, hasMultipleDefinitionFiles: hasMultipleFallbackDefinitionFiles, lang);
                }
                else if (fallbackDefinitions.Count == 1)
                {
                    var fallbackNames = ResolveImpactFallbackNames(
                        fallbackDefinitions[0],
                        logicalPartialFamilyDefinition != null
                            ? definitionResolution.PhysicalDefinitionPaths
                            : null);
                    var fileImpactOffset = responseCollection is null || string.Equals(responseCollection, "file_impacts", StringComparison.Ordinal)
                        ? offset
                        : 0;
                    var (hintResults, hintTruncated) = GetFileDependencyHintsToResolvedType(
                        fallbackDefinitions[0],
                        fallbackNames,
                        limit,
                        lang,
                        pathPatterns,
                        excludePathPatterns,
                        excludeTests,
                        fileImpactOffset,
                        logicalPartialFamilyDefinition != null
                            ? definitionResolution.PhysicalDefinitionPaths
                            : null);
                    fileImpacts = hintResults;
                    var hintExistsBeforeOffset = false;
                    if (fileImpacts.Count == 0 && fileImpactOffset > 0)
                    {
                        var hintProbe = GetFileDependencyHintsToResolvedType(
                            fallbackDefinitions[0],
                            fallbackNames,
                            1,
                            lang,
                            pathPatterns,
                            excludePathPatterns,
                            excludeTests,
                            0,
                            logicalPartialFamilyDefinition != null
                                ? definitionResolution.PhysicalDefinitionPaths
                                : null);
                        hintExistsBeforeOffset = hintProbe.Results.Count > 0;
                    }
                    if (hintTruncated)
                    {
                        truncated = true;
                        // Heuristic hints can only be capped by the user-supplied --limit, so this
                        // path never escalates to safety_cap. Leave any pre-existing reason
                        // (e.g. safety_cap propagated from the caller BFS above) intact since it
                        // is the stronger signal. Issue #1533.
                        // ヒント側の truncation は --limit による cap のみ。caller BFS で
                        // safety_cap が立っていればそちらを優先する (#1533)。
                        truncatedReason ??= ImpactTruncatedReasons.UserLimit;
                    }
                    if (fileImpacts.Count > 0 || hintExistsBeforeOffset)
                    {
                        impactMode = "file_dependency_hints";
                        heuristic = true;
                        suggestion = "These file-level dependents are heuristic only; confirm with `cdidx deps --path <definition-path> --reverse` and a member-level `impact` query.";
                    }
                    else
                    {
                        zeroResultReason = "class_symbol_no_symbol_callers";
                        impactFailureChain.Add("no_callers");
                        suggestionType = "traversal";
                        suggestion = BuildImpactSuggestion(definitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: false, hasMultipleDefinitionFiles: false, lang);
                    }
                }
                else if (hasMultipleDefinitions && logicalPartialFamilyDefinition == null)
                {
                    zeroResultReason = definitionResolution.PhysicalFileCount > 1 ? "multiple_definition_files" : "multiple_definitions";
                    impactFailureChain.Add(zeroResultReason);
                    suggestionType = "resolution";
                    suggestion = BuildImpactSuggestion(definitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: true, hasMultipleDefinitionFiles: definitionResolution.PhysicalFileCount > 1, lang);
                }
                else if (definitionResolution.PhysicalCount == 0)
                {
                    zeroResultReason = "no_matching_definition";
                    impactFailureChain.Add("definition_not_found");
                    suggestionType = "resolution";
                    suggestion = "Try `cdidx definition <symbol>` to confirm the indexed name.";
                }
                else
                {
                    impactFailureChain.Add("no_callers");
                    suggestionType = "traversal";
                }
            }
        }

        return new ImpactAnalysisResult
        {
            Query = symbolName,
            ResolvedName = resolvedName,
            ImpactMode = impactMode,
            Heuristic = heuristic,
            MaxDepth = maxDepth,
            DefinitionCount = definitionResolution.PhysicalCount,
            DefinitionFileCount = definitionResolution.PhysicalFileCount,
            LogicalDefinitionCount = definitionResolution.LogicalCount,
            HintCount = fileImpacts.Count,
            HasClassLikeDefinitions = hasClassLikeDefinitions,
            HasMultipleDefinitions = hasMultipleDefinitions,
            HasMultipleDefinitionFiles = definitionResolution.PhysicalFileCount > 1,
            TraversalRootScope = traversalRootScope,
            IdentityRootAvailable = identityRootSignal.Available,
            IdentityRootUnavailableReason = identityRootSignal.UnavailableReason,
            GraphEvidenceConfidence = identityRootSignal.EvidenceConfidence,
            IdentityRootResolutionTruncated = identityRootSignal.ResolutionTruncated,
            TraversalPartialFamilyId = logicalPartialFamilyDefinition?.PartialFamilyId,
            PartialFamilyMemberCount = partialFamilyMemberCount,
            PartialFamilyMemberRootCount = partialFamilyMemberRootCount,
            PartialFamilyMemberRootLimit = partialFamilyMemberRootLimit,
            PartialFamilyMemberRootTruncated = partialFamilyMemberRootTruncated,
            PartialFamilyMemberRootOmitted = partialFamilyMemberRootOmitted,
            Definitions = definitions,
            Callers = callers,
            FileImpacts = fileImpacts,
            Truncated = truncated,
            TruncatedReason = truncated ? truncatedReason : null,
            TerminationReason = terminationReason,
            CycleDetected = cycles.Count > 0,
            Cycles = cycles.Count > 0 ? cycles : null,
            GraphTableAvailable = _hasReferencesTable,
            ReferenceGraphComplete = referenceGraphComplete,
            ZeroResultReason = zeroResultReason,
            ImpactFailureChain = impactFailureChain is { Count: > 0 } ? impactFailureChain : null,
            SuggestionType = suggestionType,
            Suggestion = suggestion,
        };
    }

    // C# convention: a class `FooAttribute` is used in source as `[Foo]`, so the reference
    // site is stored with `symbol_name = "Foo"`. When a user queries with the class name
    // (`references FooAttribute`, `inspect FooAttribute`, `analyze_symbol("FooAttribute")`),
    // return the suffix-stripped form as an alias so the query still reaches the idiomatic
    // use site. Only applies for C# scope — other languages do not share the convention.
    // C# の規約: クラス `FooAttribute` はソース中で `[Foo]` として使われるため、参照サイトは
    // `symbol_name = "Foo"` で保存される。ユーザーがクラス名で問い合わせたとき
    // (`references FooAttribute` 等) でも慣用的な利用サイトに到達できるよう、
    // suffix を外した別名を返す。C# 以外の言語ではこの規約を持たないので適用しない。
    private static string? ComputeCSharpAttributeSuffixAlias(string? query, string? lang, string? referenceKind)
    {
        if (string.IsNullOrEmpty(query)) return null;
        if (lang != null && !lang.Equals("csharp", StringComparison.OrdinalIgnoreCase)) return null;
        // Only metadata lookups should apply the suffix alias: ordinary call-graph
        // queries (`--kind call` / `instantiate` / `subscribe`) must not match `Foo()`
        // call rows when the user typed `FooAttribute`. When `referenceKind` is null,
        // the SQL side additionally constrains the alias clause to attribute rows only.
        // metadata 参照の問い合わせ時だけ alias を適用する: `--kind call` などの call-graph
        // クエリは `FooAttribute` と入力されたときに `Foo()` の call 行に一致してはならない。
        // referenceKind が null のときは SQL 側でも alias 節を attribute 行に限定する。
        if (referenceKind != null && !referenceKind.Equals("attribute", StringComparison.OrdinalIgnoreCase))
            return null;
        const string suffix = "Attribute";
        // Case-insensitive suffix detection so `references myauditattribute` and
        // `inspect MyAuditATTRIBUTE` still produce the `MyAudit` alias, matching the
        // NOCASE / folded contract of the surrounding exact/substring query paths.
        // 大文字小文字を無視して suffix を検出することで、`myauditattribute` や
        // `MyAuditATTRIBUTE` のような形でも alias を生成できる。
        if (!query!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        if (query.Length <= suffix.Length) return null;
        return query.Substring(0, query.Length - suffix.Length);
    }

    // CSS/SCSS convention: Sass variables are stored without the leading `$`, so queries
    // that keep the sigil should still reach the canonical symbol/reference rows.
    // CSS/SCSS の規約: Sass 変数は先頭の `$` を外した形で保存されるため、sigil 付きの
    // クエリでも canonical な symbol/reference 行に到達できるようにする。
    private static string? ComputeCssScssVariableAlias(string? query)
    {
        if (string.IsNullOrEmpty(query) || query[0] != '$')
            return null;
        if (query.Length <= 1)
            return null;
        return query[1..];
    }

    private List<string> ResolveImpactFallbackNames(
        SymbolResult definition,
        IReadOnlySet<string>? physicalDefinitionPaths = null)
    {
        if (string.IsNullOrWhiteSpace(definition.Path) || string.IsNullOrWhiteSpace(definition.Name))
            return new List<string>();

        var definitionPaths = physicalDefinitionPaths is { Count: > 0 }
            ? physicalDefinitionPaths.Order(StringComparer.Ordinal).ToList()
            : [definition.Path];
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "impactSafeNameLang");
        cmd.CommandText = @"
            SELECT DISTINCT s.name
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path IN (SELECT value FROM json_each(@targetPathsJson))
              AND " + supportedLangFilter + @"
              AND (
                    (s.name = @containerName AND s.kind = @containerKind)
                    OR s.container_name = @containerName
                  )
            ORDER BY s.name";
        SqliteCommandPolicy.Add(cmd, "@targetPathsJson", JsonStringListCodec.Serialize(definitionPaths));
        SqliteCommandPolicy.Add(cmd, "@containerName", definition.Name);
        SqliteCommandPolicy.Add(cmd, "@containerKind", definition.Kind);

        var results = new List<string>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            results.Add(reader.GetString(0));

        // C# attribute naming convention: a class `FooAttribute` is used as `[Foo]` in source,
        // so reference sites are stored with symbol_name `Foo`. Add the suffix-stripped alias
        // for the resolved definition itself so impact on `FooAttribute` can find metadata-only
        // usage sites. Only the resolved definition's own name gets the alias — applying the
        // strip to every same-file fallback name (e.g. a nested `BarAttribute` inside the file
        // that defines `FooAttribute`) would let `impact FooAttribute` falsely report `[Bar]`
        // usages as part of `FooAttribute`'s blast radius.
        // C# の属性命名規約: クラス `FooAttribute` はソースで `[Foo]` として使われ、参照サイトは
        // symbol_name `Foo` で保存される。`FooAttribute` への impact でも metadata 参照サイトを
        // 見つけられるよう、*解決済み定義自身* にのみサフィックスを外した別名を追加する。
        // same-file fallback 名全体（例: `FooAttribute` と同一ファイルに nested で存在する
        // `BarAttribute`）にまで strip を適用すると、`impact FooAttribute` が `[Bar]` 利用を
        // 誤って `FooAttribute` の影響範囲として報告してしまうため、定義自身だけに限定する。
        if (string.Equals(definition.Lang, "csharp", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(definition.Name) &&
            definition.Name.Length > "Attribute".Length &&
            definition.Name.EndsWith("Attribute", StringComparison.Ordinal))
        {
            var stripped = definition.Name.Substring(0, definition.Name.Length - "Attribute".Length);
            if (stripped.Length > 0 && !results.Contains(stripped))
                results.Add(stripped);
        }

        return results;
    }

    private (List<FileDependencyResult> Results, bool Truncated) GetFileDependencyHintsToResolvedType(
        SymbolResult definition,
        IReadOnlyList<string> fallbackNames,
        int limit,
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        int offset = 0,
        IReadOnlySet<string>? physicalDefinitionPaths = null)
    {
        if (!_hasReferencesTable || string.IsNullOrWhiteSpace(definition.Path) || fallbackNames.Count == 0)
            return (new List<FileDependencyResult>(), false);

        var definitionPaths = physicalDefinitionPaths is { Count: > 0 }
            ? physicalDefinitionPaths.Order(StringComparer.Ordinal).ToList()
            : [definition.Path];
        using var cmd = _conn.CreateCommand();
        var innerSql = @"
                SELECT src.id AS source_file_id, src.path AS source_path, @impactTargetPath AS target_path,
                       r.symbol_name AS symbol_name,
                       r.line,
                       r.column_number,
                       " + GetLogicalReferenceKindSql("r.reference_kind") + @" AS logical_reference_kind
                FROM symbol_references r
                JOIN files src ON r.file_id = src.id
                WHERE src.path NOT IN (SELECT value FROM json_each(@impactTargetPathsJson))";
        // `impact` heuristic file hints intentionally include metadata-only reference
        // kinds (`attribute` / `annotation`). A rename or removal of `User` breaks
        // `[JsonConverter(typeof(User))]` / `@Inject(User.class)` at compile time just
        // as surely as it breaks `new User()`, so file-level blast-radius analysis
        // must surface those sites as real dependencies. `callers` / `callees` still
        // reject metadata kinds at the CLI / MCP boundary because those commands model
        // the dynamic call graph, not the dependency graph.
        // `impact` の heuristic file hint は metadata-only な参照 (`attribute` /
        // `annotation`) も意図的に含める。`User` を rename / 削除すると
        // `[JsonConverter(typeof(User))]` / `@Inject(User.class)` も compile-time で
        // 壊れるため、ファイル単位の blast-radius 分析ではそれらも本物の依存として
        // 出す必要がある。`callers` / `callees` は call graph を扱うので、metadata 種別
        // の拒否は引き続き CLI / MCP boundary 側で行う。
        innerSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "src", "impactDepsLang")}";
        if (lang != null)
            innerSql += " AND src.lang = @lang";
        innerSql += " AND r.symbol_name IN (SELECT value FROM json_each(@impactFallbackNamesJson))";

        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add(BuildPathFilterPredicate("src", "pathPattern", i, pathPatterns[i]));
            innerSql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                innerSql += $" AND NOT {BuildPathFilterPredicate("src", "excludePathPattern", i, excludePathPatterns[i])}";
        }
        if (excludeTests)
            innerSql += $" AND NOT {TestPathCondition.Replace("f.path", "src.path")}";
        innerSql = "SELECT DISTINCT * FROM (" + innerSql + ")";

        cmd.CommandText = $@"
            SELECT source_file_id, source_path, target_path,
                   COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT symbol_name) AS symbols,
                   MAX(CASE WHEN logical_reference_kind IN ('attribute','annotation') THEN 1 ELSE 0 END) AS has_metadata_ref
            FROM ({innerSql}) edges
            GROUP BY source_file_id, source_path, target_path
            ORDER BY reference_count DESC, source_path, target_path";
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        SqliteCommandPolicy.Add(cmd, "@impactTargetPath", definition.Path);
        SqliteCommandPolicy.Add(cmd, "@impactTargetPathsJson", JsonStringListCodec.Serialize(definitionPaths));
        SqliteCommandPolicy.Add(cmd, "@impactFallbackNamesJson", JsonStringListCodec.Serialize(fallbackNames));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var candidates = new List<(long SourceFileId, bool HasMetadataRef, FileDependencyResult Edge)>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            candidates.Add((
                reader.GetInt64(0),
                reader.GetInt32(5) == 1,
                new FileDependencyResult
                {
                    ResultKind = ImpactResultKinds.FileHeuristic,
                    SourcePath = reader.GetString(1),
                    TargetPath = reader.GetString(2),
                    ReferenceCount = reader.GetInt32(3),
                    Symbols = reader.GetString(4),
                }));
        }

        // Metadata references only carry the short use-site name (`Foo` for `[Foo]`,
        // `@Foo`). If multiple class-like definitions share the same unqualified name
        // across namespaces / packages (e.g. `A.MyAuditAttribute` and
        // `B.MyAuditAttribute`), we cannot uniquely attribute a `[MyAudit]` site to
        // either target. Skip the metadata evidence bypass in that ambiguous case so
        // `impact` does not over-report the blast radius of a rename / removal.
        // metadata 参照は use-site 側の短縮名 (`[Foo]` / `@Foo` の `Foo`) しか持た
        // ないため、namespace / package を跨いで同名の class-like 定義が複数存在
        // する場合、`[MyAudit]` 参照をどちらの target にも一意に紐付けられない。
        // そのような曖昧なケースでは metadata の evidence bypass を行わず、
        // `impact` が rename / 削除の影響範囲を過大報告しないようにする。
        var metadataBypassSafe = IsMetadataTargetUnambiguous(definition, lang, pathPatterns, excludePathPatterns, excludeTests);
        var evidenceCache = new Dictionary<long, bool>();
        var filtered = new List<FileDependencyResult>();
        foreach (var candidate in candidates)
        {
            // Evidence anchoring precedes the metadata bypass: an in-file `call` /
            // `instantiate` reference to `definition.Name` (or structured type evidence
            // such as a parameter or return-type token) pins the source/target pair
            // unambiguously, so the looser metadata widening is unnecessary. Falling
            // through to the bypass only when no such anchor exists keeps pure
            // attribute / annotation consumers visible without over-attributing edges
            // that the call graph already proves.
            // evidence anchoring を metadata bypass より先に評価する。`definition.Name`
            // への `call` / `instantiate` 参照、または structured type evidence
            // (引数 / return 型での出現) がファイル内にあれば source→target の関係は
            // 一意に固定されるので、より緩い metadata widening は不要。anchor が無い
            // ときだけ bypass にフォールスルーすることで、純粋な attribute / annotation
            // consumer の表示を維持しつつ、call graph で既に確定しているエッジを
            // 過剰に metadata 経由で広げないようにする。
            if (!evidenceCache.TryGetValue(candidate.SourceFileId, out var hasEvidence))
            {
                hasEvidence = SourceFileHasAnchorReferenceTo(candidate.SourceFileId, definition.Name)
                              || SourceFileHasStructuredTypeEvidence(candidate.SourceFileId, definition.Name);
                evidenceCache[candidate.SourceFileId] = hasEvidence;
            }
            if (hasEvidence)
            {
                filtered.Add(candidate.Edge);
                continue;
            }
            // Pure metadata-only consumers (`[MyAudit]` / `@Inject(User.class)`) legitimately
            // lack any anchor in the source file beyond the attribute / annotation use itself.
            // For those, bypass the evidence guard only when the class-like target is
            // unambiguous so deps/impact can still surface them without over-attributing
            // same-named targets in the ambiguous case.
            // anchor が一つも無い純粋な metadata consumer (`[MyAudit]` / `@Inject(User.class)`)
            // のみ、class-like target が一意な場合に限り evidence guard を skip して
            // 拾い上げる。曖昧なときは引き続き edge を落とし、同名 target への誤帰属を
            // 防ぐ。
            if (candidate.HasMetadataRef && metadataBypassSafe)
            {
                filtered.Add(candidate.Edge);
            }
        }

        offset = Math.Max(0, offset);
        var truncated = filtered.Count > checked(offset + limit);
        filtered = filtered.Skip(offset).Take(limit).ToList();

        return (filtered, truncated);
    }

    // Returns true when the metadata target name resolves to at most one class-like
    // symbol across the graph-supported languages. Ambiguous names (same unqualified
    // name under different namespaces / packages) must not trigger the metadata
    // evidence bypass because attribute / annotation reference rows only keep the
    // short name and cannot disambiguate between them.
    // graph 対応言語の中で class-like シンボルが高々 1 件しか存在しないときに true。
    // namespace / package を跨いで同名の class-like 定義が複数ある曖昧なケースでは
    // attribute / annotation 参照行が短縮名しか持たず区別できないため、metadata の
    // evidence bypass を許可しない。
    private bool IsMetadataTargetUnambiguous(
        SymbolResult definition,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            return false;
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "metadataAmbigLang");
        // Count at symbol-identity level (path + line + name) rather than at path
        // level, so two same-named class-like definitions in the same source file
        // (e.g. `namespace A { class MyAuditAttribute { } } namespace B { class
        // MyAuditAttribute { } }` both in one .cs file) still register as ambiguous.
        // DISTINCT f.path alone would collapse them to 1 and falsely trigger the
        // metadata bypass.
        // 曖昧性は path 単位ではなく symbol identity 単位 (path + line + name) で数える。
        // 同じ .cs ファイル内に別名前空間で同名の class-like が 2 つあるケース
        // (例: `namespace A { class MyAuditAttribute { } } namespace B { class
        // MyAuditAttribute { } }`) でも ambiguity を 2 として検出できる。DISTINCT
        // f.path のままだと 1 に潰れ、metadata bypass が誤って有効化される。
        // For C# specifically, only count class-like definitions that are
        // plausible attribute metadata targets. We don't resolve base types
        // transitively at SQL time, so the best portable approximation is
        // "has an inheritance clause": any class declared with `: ...` is a
        // potential attribute type (direct `: Attribute`, indirect
        // `: BaseAudit` where BaseAudit itself derives from Attribute, or
        // any other `: Base` chain). A plain `class MyAuditAttribute { }`
        // with no `:` clause is not a valid `[MyAudit]` target at compile
        // time, so excluding it prevents the metadata bypass from being
        // falsely suppressed. We deliberately over-accept non-attribute
        // derived classes rather than under-accept indirectly-derived
        // attribute classes, because an invalid `[MyFoo]` against a
        // non-attribute class would fail to compile and therefore not
        // appear as a real reference. Other languages keep the broad
        // class-like candidate set because their metadata-target markers
        // don't match this signature shape.
        // C# は SQL 時点で基底型を遡れないため、「何かを継承している
        // class-like」を attribute 候補の近似として扱う。`: Attribute` の
        // 直接継承も、`: BaseAudit` のような中間基底経由の間接継承も、
        // 何らかの `: Base` があれば候補に含める。継承節の無い plain
        // `class MyAuditAttribute { }` だけを除外することで metadata
        // bypass の誤抑止を防ぐ。非 attribute を過剰に含めるが、無効な
        // `[MyFoo]` はコンパイルできないので実参照にはならず実害が無い。
        // 署名列が無い legacy DB では degrade して class 限定のみ使う。
        var metadataTargetKindExprF = BuildMetadataTargetKindExpr("f");
        var sql = $@"
            SELECT COUNT(*) FROM (
                SELECT DISTINCT f.path, s.line, s.name
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE s.name = @metadataAmbigName COLLATE NOCASE
                  AND {metadataTargetKindExprF}
                  AND {supportedLangFilter}";
        if (lang != null)
        {
            sql += " AND f.lang = @metadataAmbigLangFilter";
            SqliteCommandPolicy.Add(cmd, "@metadataAmbigLangFilter", lang);
        }
        // Path / exclude-path parameters share the same anchored path filter
        // predicate as the rest of the reader: plain values match an exact path
        // or subtree, while `*` / `?` keep glob-style LIKE matching.
        // path / exclude-path は reader 全体で共通の anchored path filter 条件を
        // 使う。ワイルドカードを含まない値は完全一致または配下に一致し、
        // `*` / `?` は glob 風の LIKE matching として扱う。
        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add(BuildPathFilterPredicate("f", "metadataAmbigPath", i, pathPatterns[i]));
            sql += " AND (" + string.Join(" OR ", ors) + ")";
            AddPathFilterParameterSet(cmd, "metadataAmbigPath", pathPatterns);
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND NOT {BuildPathFilterPredicate("f", "metadataAmbigExcludePath", i, excludePathPatterns[i])}";
            AddPathFilterParameterSet(cmd, "metadataAmbigExcludePath", excludePathPatterns);
        }
        if (excludeTests)
            sql += $" AND NOT {TestPathCondition}";
        sql += ")";
        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@metadataAmbigName", definition.Name);
        var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        // Require exactly one authoritative metadata target named `definition.Name`.
        // `count == 0` is also unsafe for the bypass — if no class-like symbol with
        // that name is a valid metadata target, then a `[Foo]` reference cannot
        // resolve to the passed-in definition either. `count <= 1` would let the
        // bypass fire with zero candidates and falsely attribute `[Foo]` sites to a
        // non-attribute definition (e.g. `class FooAttribute : BaseService` post
        // #435 iter 4 scope-aware resolver). Issue #435 codex review iter 4.
        // 1 件厳密一致のみ unambiguous とみなす。count=0 はメタデータターゲットが
        // 一つも無い状態であり、`[Foo]` が passed-in 定義へ解決する根拠も無いため
        // bypass は発動させない。`<= 1` だと #435 iter 4 のスコープ対応で非属性
        // 派生になったクラスに `[Foo]` 参照を誤帰属させる。
        return count == 1;
    }

    private bool SourceFileHasStructuredTypeEvidence(long fileId, string typeName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.name,
                   " + GetSymbolColumnSql("signature") + @" AS signature,
                   " + GetSymbolColumnSql("return_type") + @" AS return_type
            FROM symbols s
            WHERE s.file_id = @fileId";
        SqliteCommandPolicy.Add(cmd, "@fileId", fileId);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var symbolName = reader.GetString(0);
            var signature = !reader.IsDBNull(1) ? reader.GetString(1) : null;
            var returnType = !reader.IsDBNull(2) ? reader.GetString(2) : null;
            if (SymbolProvidesStructuredTypeEvidence(symbolName, signature, returnType, typeName))
                return true;
        }

        return false;
    }

    // A `call`, `instantiate`, `subscribe`, or `unsubscribe` reference to `typeName` inside
    // the source file is a stronger anchor than structured type evidence (signature /
    // return-type tokens). When such a reference exists, the source/target relationship
    // is pinned by the call graph itself, so `GetFileDependencyHintsToResolvedType` does
    // not need to widen via the looser metadata bypass. Symbol-name match is
    // intentionally exact (no suffix-strip alias) because callable references already
    // carry the authoritative name — applying the C# `[Foo]` → `FooAttribute` alias here
    // would let an unrelated `Foo()` method call anchor `impact FooAttribute` and
    // over-report blast radius (issue #1881).
    // `typeName` への `call` / `instantiate` / `subscribe` / `unsubscribe` 参照は signature /
    // return 型のトークンより強い anchor で、call graph 自体が source/target の関係を確定するため metadata bypass を
    // 経由した widening は不要になる。比較は厳密一致のみで行う：callable な参照は
    // 既に authoritative な名前を保持しているため、C# の `[Foo]` → `FooAttribute` のような
    // suffix alias を適用すると、無関係な `Foo()` 呼び出しが `impact FooAttribute` を
    // 不当に anchor してしまい blast radius を過大報告する (issue #1881)。
    private bool SourceFileHasAnchorReferenceTo(long fileId, string typeName)
    {
        if (!_hasReferencesTable || string.IsNullOrWhiteSpace(typeName))
            return false;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT 1
            FROM symbol_references r
            WHERE r.file_id = @fileId
              AND r.symbol_name = @typeName
              AND r.reference_kind IN {ImpactAnchorReferenceKindsSql}
            LIMIT 1";
        SqliteCommandPolicy.Add(cmd, "@fileId", fileId);
        SqliteCommandPolicy.Add(cmd, "@typeName", typeName);
        return cmd.ExecuteScalar() != null;
    }

    private static bool SymbolProvidesStructuredTypeEvidence(string symbolName, string? signature, string? returnType, string typeName)
    {
        if (FoldedImpactNameEquals(returnType, typeName))
            return true;
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        foreach (Match match in ImpactSignatureIdentifierRegex.Matches(signature))
        {
            var token = match.Value;
            if (FoldedImpactNameEquals(token, symbolName))
                continue;
            if (FoldedImpactNameEquals(token, typeName))
                return true;
        }

        return false;
    }

    private static bool FoldedImpactNameEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var leftFolded = NameFold.Fold(left) ?? left;
        var rightFolded = NameFold.Fold(right) ?? right;
        return string.Equals(leftFolded, rightFolded, StringComparison.Ordinal);
    }

    private static bool IsPreciseImpactFallbackKind(string? kind)
    {
        return kind is "class" or "struct" or "interface";
    }

    private static string BuildImpactSuggestion(IReadOnlyList<string> definitionPaths, bool hasClassLikeDefinitions, bool hasMultipleDefinitions, bool hasMultipleDefinitionFiles, string? lang)
    {
        var langHint = lang == null
            ? " Use `--lang <lang>` if the same name exists in multiple languages."
            : string.Empty;

        if (hasClassLikeDefinitions)
        {
            if (hasMultipleDefinitionFiles)
                return "Try `cdidx deps --path <definition-path> --reverse` for each definition file or query a member symbol instead." + langHint;
            if (hasMultipleDefinitions)
                return "Try a fully qualified or member symbol query, or inspect the overlapping definitions with `cdidx definition <symbol> --body`." + langHint;
            if (definitionPaths.Count > 0)
                return $"Try `cdidx deps --path {definitionPaths[0]} --reverse` or query a member symbol instead.";
        }

        if (hasMultipleDefinitions)
            return "Try a more specific symbol name or inspect each definition file with `cdidx definition <symbol> --body`." + langHint;

        return "Try `cdidx definition <symbol>` to confirm the indexed symbol and then query a more specific callable member.";
    }

    private static string BuildGraphSupportReason(string? graphLanguage, bool? graphSupported)
    {
        return ReferenceExtractor.BuildGraphSupportReason(graphLanguage, graphSupported)
            ?? "Call-graph support could not be determined because no language filter or matching definition was available.";
    }
}
