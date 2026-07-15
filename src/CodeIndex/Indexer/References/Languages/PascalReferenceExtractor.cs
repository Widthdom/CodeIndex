using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class PascalReferenceExtractor
{
    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        LanguageReferenceExtractionSupport.EmitTypePositionReferences(
            "pascal",
            preparedLine,
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            container);
    }

    public static void EmitAdditionalCallReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference,
        IReadOnlySet<string>? definitionNames)
    {
        LanguageReferenceExtractionSupport.EmitAdditionalCallReferences(
            "pascal",
            preparedLine,
            preparedLine,
            addCallLikeReference,
            [],
            new ReferenceDedupeSet(),
            0,
            string.Empty,
            0,
            _ => null,
            definitionNames);
    }
}
