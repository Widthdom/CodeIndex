namespace CodeIndex.Database;

public sealed record FindSemanticFilters(
    IReadOnlyList<string> Origins,
    IReadOnlyList<string> ExcludedOrigins,
    IReadOnlyList<string> ResultKinds,
    bool ExcludeComments = false,
    bool ExcludeStrings = false,
    bool ExcludeFixtures = false)
{
    internal bool Accepts(SearchMatchFacet facet)
        => (!ExcludeComments || facet.Origin != SearchMatchClassifier.Comment)
           && (!ExcludeStrings || !SearchMatchClassifier.IsStringLikeOrigin(facet.Origin))
           && (!ExcludeFixtures || !facet.TestFixture)
           && (Origins.Count == 0 || Origins.Contains(facet.Origin, StringComparer.Ordinal))
           && !ExcludedOrigins.Contains(facet.Origin, StringComparer.Ordinal)
           && (ResultKinds.Count == 0 || Kinds(facet).Any(kind => ResultKinds.Contains(kind, StringComparer.Ordinal)));

    internal static List<string> Kinds(SearchMatchFacet facet)
        => facet.Origin == SearchMatchClassifier.Code ? [facet.Origin, "identifier"] : [facet.Origin];
}

public partial class DbReader
{
    private SearchResult? CreateFindOriginContext(FindCandidateFile file, CancellationToken cancellationToken)
    {
        if (file.Lang?.ToLowerInvariant() is not ("csharp" or "python"))
            return null;
        var context = new SearchResult { Path = file.Path, Lang = file.Lang };
        AttachCSharpOriginLines([context], cancellationToken);
        AttachPythonOriginLines([context], cancellationToken);
        return context;
    }

    private static SearchMatchFacet ClassifyFindMatch(
        FindCandidateFile file,
        IndexedLine line,
        FindLineMatch match,
        SearchResult? context)
    {
        // Only expose classifiers with explicit bounded lexical context in this v1 path.
        var python = string.Equals(file.Lang, "python", StringComparison.OrdinalIgnoreCase);
        var supported = python || file.Lang?.ToLowerInvariant() is "csharp" or "shell" or "bash" or "zsh";
        var facet = supported && (python || line.Text.Length > 0 && match.Column < line.Text.Length)
            ? SearchMatchClassifier.Classify(file.Path, file.Lang, line.Number, line.Text,
                match.Column + 1, match.Length, csharpContext: context?.CSharpOrigins, pythonContext: context?.PythonOrigins)
            : new SearchMatchFacet
            {
                Origin = SearchMatchClassifier.Unknown,
                TestFile = SearchMatchClassifier.IsLikelyTestPath(file.Path),
            };
        // Regex zero-width matches are positions, not one-character occurrences.
        facet.Line = line.Number;
        facet.Column = match.Column + 1;
        facet.Length = match.Length;
        return facet;
    }
}
