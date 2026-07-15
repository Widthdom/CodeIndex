using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class VisualBasicReferenceExtractor
{
    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        LanguageReferenceExtractionSupport.EmitTypePositionReferences(
            "vb",
            preparedLine,
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            container: null);
    }
}
