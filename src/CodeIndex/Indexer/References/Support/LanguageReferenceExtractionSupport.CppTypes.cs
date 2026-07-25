using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private static void EmitCppTypeReferences(
        string language,
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var line = new CppTypeReferenceLineContext(
            language,
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        EmitCppHeaderConstructionAndCastReferences(line);
        if (language == "c")
            EmitCTypeReferences(line);
        EmitCppOperandConstructionAndAliasReferences(line);
        EmitCppConstraintAndDeclarationReferences(line);
    }
}
