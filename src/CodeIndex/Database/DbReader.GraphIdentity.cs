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

    /// <summary>
    /// Describe whether a direct graph query is identity-scoped. Selected queries expose
    /// one exact selector; ambiguous name queries expose every bounded candidate instead
    /// of implying that their union is one identity.
    /// direct graph query の identity scope を記述する。選択済み query は 1 件の正確な
    /// selector を、曖昧な名前 query は和集合を単一 identity と見せず候補一覧を返す。
    /// </summary>
    internal GraphQueryIdentityMetadata GetGraphQueryIdentityMetadata(
        string query,
        DefinitionResult? selectedDefinition,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        if (selectedDefinition != null)
        {
            return new(
                Applies: true,
                IdentityScoped: true,
                IdentityScopeReason: "selected_symbol_id",
                Selected: BuildSymbolCandidateSelector(selectedDefinition),
                Candidates: [],
                CandidatesTruncated: false);
        }

        var definitions = SearchSymbols(
            query,
            GraphIdentityCandidateLimit + 1,
            kind: null,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact: true)
            .Where(static definition => definition.SymbolId != null)
            .DistinctBy(static definition => definition.SymbolId)
            .ToList();
        if (definitions.Count <= 1)
            return GraphQueryIdentityMetadata.None;

        var truncated = definitions.Count > GraphIdentityCandidateLimit;
        if (truncated)
            definitions.RemoveRange(GraphIdentityCandidateLimit, definitions.Count - GraphIdentityCandidateLimit);

        return new(
            Applies: true,
            IdentityScoped: false,
            IdentityScopeReason: "ambiguous_name_union",
            Selected: null,
            Candidates: definitions.Select(BuildSymbolCandidateSelector).ToArray(),
            CandidatesTruncated: truncated);
    }

    internal static bool GraphSelectorMatchesLanguageFilter(DefinitionResult definition, string? lang)
    {
        var normalizedLanguage = NormalizeQueryLanguage(lang);
        return normalizedLanguage == null
               || string.Equals(definition.Lang, normalizedLanguage, StringComparison.Ordinal);
    }
}
