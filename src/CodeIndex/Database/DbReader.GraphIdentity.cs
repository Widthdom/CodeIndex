using CodeIndex.Models;

namespace CodeIndex.Database;

internal enum GraphSymbolSelectorStatus
{
    Success,
    Missing,
    Malformed,
    GenerationRequired,
    Stale,
    NotFound,
}

internal sealed record GraphSymbolSelectorResolution(
    GraphSymbolSelectorStatus Status,
    string? Value,
    SymbolSelector? Selector,
    DefinitionResult? Definition);

internal sealed record GraphQueryIdentityMetadata(
    bool Applies,
    bool IdentityScoped,
    string IdentityScopeReason,
    SymbolCandidateSelector? Selected,
    IReadOnlyList<SymbolCandidateSelector> Candidates,
    bool CandidatesTruncated)
{
    internal static GraphQueryIdentityMetadata None { get; } = new(
        Applies: false,
        IdentityScoped: false,
        IdentityScopeReason: string.Empty,
        Selected: null,
        Candidates: [],
        CandidatesTruncated: false);
}

public partial class DbReader
{
    private const int GraphIdentityCandidateLimit = 20;

    /// <summary>
    /// Resolve one generation-bound graph selector without applying result filters.
    /// generation に束縛された graph selector を、結果フィルター適用前に 1 件解決する。
    /// </summary>
    internal GraphSymbolSelectorResolution ResolveGraphSymbolSelector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new(GraphSymbolSelectorStatus.Missing, value, null, null);

        if (!SymbolSelector.TryParse(value, out var selector))
            return new(GraphSymbolSelectorStatus.Malformed, value, null, null);

        if (selector.GenerationFingerprint == null)
            return new(GraphSymbolSelectorStatus.GenerationRequired, value, selector, null);

        if (!IsCurrentSymbolSelector(selector))
            return new(GraphSymbolSelectorStatus.Stale, value, selector, null);

        var definition = GetDefinitionBySelector(selector);
        return definition == null
            ? new(GraphSymbolSelectorStatus.NotFound, value, selector, null)
            : new(GraphSymbolSelectorStatus.Success, value, selector, definition);
    }

    internal GraphQueryIdentityMetadata GetReferenceGraphQueryIdentityMetadata(
        string query,
        DefinitionResult? selectedDefinition,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        string? referenceKind,
        bool exact,
        bool includeQualifiedCommonCalls)
        => BuildGraphQueryIdentityMetadata(
            selectedDefinition,
            selectedDefinition == null
                ? GetReferenceGraphIdentityCandidates(
                    query,
                    lang,
                    referenceKind,
                    pathPatterns,
                    excludePathPatterns,
                    excludeTests,
                    exact,
                    includeQualifiedCommonCalls)
                : []);

    internal GraphQueryIdentityMetadata GetCallerGraphQueryIdentityMetadata(
        string query,
        DefinitionResult? selectedDefinition,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        string? referenceKind,
        bool exact,
        bool rawKinds,
        bool includeQualifiedCommonCalls,
        bool includeMemberReads)
        => BuildGraphQueryIdentityMetadata(
            selectedDefinition,
            selectedDefinition == null
                ? GetCallerGraphIdentityCandidates(
                    query,
                    lang,
                    referenceKind,
                    pathPatterns,
                    excludePathPatterns,
                    excludeTests,
                    exact,
                    rawKinds,
                    includeQualifiedCommonCalls,
                    includeMemberReads)
                : []);

    internal GraphQueryIdentityMetadata GetCalleeGraphQueryIdentityMetadata(
        string query,
        DefinitionResult? selectedDefinition,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        string? referenceKind,
        bool exact,
        bool rawKinds,
        bool includeQualifiedCommonCalls,
        bool includeMemberReads)
        => BuildGraphQueryIdentityMetadata(
            selectedDefinition,
            selectedDefinition == null
                ? GetCalleeGraphIdentityCandidates(
                    query,
                    lang,
                    referenceKind,
                    pathPatterns,
                    excludePathPatterns,
                    excludeTests,
                    exact,
                    rawKinds,
                    includeQualifiedCommonCalls,
                    includeMemberReads)
                : []);

    internal GraphQueryIdentityMetadata GetImpactGraphQueryIdentityMetadata(
        string query,
        DefinitionResult? selectedDefinition,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        if (selectedDefinition != null)
            return BuildSelectedGraphQueryIdentityMetadata(selectedDefinition);

        var resolution = ResolveImpactDefinitions(
            query,
            GraphIdentityCandidateLimit + 1,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests);
        return BuildGraphQueryIdentityMetadata(
            resolution.Definitions,
            resolution.PhysicalSymbolIdsTruncated
            || resolution.LogicalCount > resolution.Definitions.Count);
    }

    /// <summary>
    /// Describe whether a direct graph query is identity-scoped. Selected queries expose
    /// one exact selector; ambiguous name queries expose only identities that contribute
    /// rows after the command's graph filters have been applied.
    /// direct graph query の identity scope を記述する。選択済み query は 1 件の正確な
    /// selector を返し、曖昧な名前 query は command の graph filter 適用後に結果へ
    /// 寄与する identity だけを候補として返す。
    /// </summary>
    private GraphQueryIdentityMetadata BuildGraphQueryIdentityMetadata(
        DefinitionResult? selectedDefinition,
        IReadOnlyList<long> candidateSymbolIds)
    {
        if (selectedDefinition != null)
            return BuildSelectedGraphQueryIdentityMetadata(selectedDefinition);

        var boundedSymbolIds = candidateSymbolIds
            .Distinct()
            .Take(GraphIdentityCandidateLimit + 1)
            .ToList();
        var definitions = boundedSymbolIds
            .Select(symbolId => GetDefinitionBySelector(new SymbolSelector(symbolId)))
            .OfType<DefinitionResult>()
            .ToList();
        return BuildGraphQueryIdentityMetadata(
            definitions,
            boundedSymbolIds.Count > GraphIdentityCandidateLimit);
    }

    private GraphQueryIdentityMetadata BuildGraphQueryIdentityMetadata(
        IEnumerable<SymbolResult> candidateDefinitions,
        bool candidatesTruncated)
    {
        var definitions = candidateDefinitions
            .Where(static definition => definition.SymbolId != null)
            .DistinctBy(static definition => definition.SymbolId)
            .Take(GraphIdentityCandidateLimit + 1)
            .ToList();
        if (definitions.Count <= 1)
            return GraphQueryIdentityMetadata.None;

        var truncated = candidatesTruncated || definitions.Count > GraphIdentityCandidateLimit;
        if (definitions.Count > GraphIdentityCandidateLimit)
            definitions.RemoveRange(GraphIdentityCandidateLimit, definitions.Count - GraphIdentityCandidateLimit);

        return new(
            Applies: true,
            IdentityScoped: false,
            IdentityScopeReason: "ambiguous_name_union",
            Selected: null,
            Candidates: definitions.Select(BuildSymbolCandidateSelector).ToArray(),
            CandidatesTruncated: truncated);
    }

    private string BuildReferenceRootSymbolIdsSql(string referenceAlias)
    {
        if (!_referenceIdentityContractCurrent)
            return "NULL";

        var targetSymbolIdSql = $"CAST({referenceAlias}.target_symbol_id AS TEXT)";
        if (!HasTable("symbol_reference_candidates"))
            return targetSymbolIdSql;

        return $@"COALESCE((
                    SELECT GROUP_CONCAT(identity_candidate.symbol_id)
                    FROM symbol_reference_candidates AS identity_candidate
                    WHERE identity_candidate.reference_id = {referenceAlias}.id
                ), {targetSymbolIdSql})";
    }

    private GraphQueryIdentityMetadata BuildSelectedGraphQueryIdentityMetadata(
        DefinitionResult selectedDefinition)
        => new(
            Applies: true,
            IdentityScoped: true,
            IdentityScopeReason: "selected_symbol_id",
            Selected: BuildSymbolCandidateSelector(selectedDefinition),
            Candidates: [],
            CandidatesTruncated: false);

    internal static bool GraphSelectorMatchesLanguageFilter(DefinitionResult definition, string? lang)
    {
        var normalizedLanguage = NormalizeQueryLanguage(lang);
        return normalizedLanguage == null
               || string.Equals(definition.Lang, normalizedLanguage, StringComparison.Ordinal);
    }
}
