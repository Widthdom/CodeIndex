using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class ObjectiveCReferenceExtractor
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
            "objc",
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
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        LanguageReferenceExtractionSupport.EmitAdditionalCallReferences(
            "objc",
            preparedLine,
            preparedLine,
            addCallLikeReference,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            definitionNames: null);
    }
}
