using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class HaskellReferenceExtractor
{
    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("::", StringComparison.Ordinal) < 0)
            return;

        LanguageReferenceExtractionSupport.EmitTypePositionReferences(
            "haskell",
            preparedLine,
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            _ => container,
            container);
    }

    public static void EmitAdditionalCallReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference,
        IReadOnlySet<string>? definitionNames)
    {
        if (!ContainsWhitespace(preparedLine))
            return;

        LanguageReferenceExtractionSupport.EmitAdditionalCallReferences(
            "haskell",
            preparedLine,
            preparedLine,
            addCallLikeReference,
            [],
            [],
            0,
            string.Empty,
            0,
            _ => null,
            definitionNames);
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
                return true;
        }

        return false;
    }
}
