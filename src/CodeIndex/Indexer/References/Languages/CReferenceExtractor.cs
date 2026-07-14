using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class CReferenceExtractor
{
    public static void EmitTypePositionReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        LanguageReferenceExtractionSupport.EmitTypePositionReferences(
            "c",
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            container: null);
    }
}
