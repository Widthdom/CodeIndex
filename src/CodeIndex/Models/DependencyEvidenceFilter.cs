namespace CodeIndex.Models;

/// <summary>Bounded, normalized selectors applied to dependency reference observations.</summary>
public sealed class DependencyEvidenceFilter
{
    public const int MaxValues = 64;
    public const int MaxValueCharacters = 64;
    public const int MaxCsvCharacters = 2048;
    public static IReadOnlyList<string> ResolutionStates { get; } =
        Array.AsReadOnly(new[] { "resolved", "resolved_group", "ambiguous", "unresolved", "unavailable" });
    public static IReadOnlyList<string> ReferenceKinds => SymbolKindCatalog.PersistedReferenceKinds;
    public static DependencyEvidenceFilter Empty { get; } = new([], []);

    public IReadOnlyList<string> Resolutions { get; }
    public IReadOnlyList<string> Kinds { get; }
    public bool IsActive => Resolutions.Count > 0 || Kinds.Count > 0;

    private DependencyEvidenceFilter(string[] resolutions, string[] kinds)
    {
        Resolutions = Array.AsReadOnly(resolutions);
        Kinds = Array.AsReadOnly(kinds);
    }

    public static DependencyEvidenceFilter Create(
        IReadOnlyList<string>? resolutions = null,
        IReadOnlyList<string>? kinds = null)
        => new(Normalize(resolutions, ResolutionStates, "resolution state"),
            Normalize(kinds, ReferenceKinds, "reference kind"));

    private static string[] Normalize(IReadOnlyList<string>? values, IReadOnlyList<string> domain, string label)
    {
        if (values == null || values.Count == 0)
            return [];
        if (values.Count > MaxValues)
            throw new ArgumentException($"Dependency {label} filters accept at most {MaxValues} values.");
        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value == null || value.Length > MaxValueCharacters)
                throw new ArgumentException($"Dependency {label} values must contain 1–{MaxValueCharacters} characters.");
            var item = value.Trim().ToLowerInvariant();
            if (!domain.Contains(item, StringComparer.Ordinal))
                throw new ArgumentException($"Unsupported dependency {label}. Supported values: {string.Join(", ", domain)}.");
            normalized.Add(item);
        }
        return normalized.ToArray();
    }
}
