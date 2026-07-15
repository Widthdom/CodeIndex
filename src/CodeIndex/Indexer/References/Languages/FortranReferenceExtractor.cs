using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class FortranReferenceExtractor
{
    public static void EmitTypePositionReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        LanguageReferenceExtractionSupport.EmitTypePositionReferences(
            "fortran",
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            container);
    }

    public static void EmitAdditionalCallReferences(string preparedLine, Action<string, int> addCallLikeReference)
    {
        LanguageReferenceExtractionSupport.EmitAdditionalCallReferences(
            "fortran",
            preparedLine,
            preparedLine,
            addCallLikeReference,
            [],
            new ReferenceDedupeSet(),
            0,
            string.Empty,
            0,
            _ => null,
            definitionNames: null);
    }
}
