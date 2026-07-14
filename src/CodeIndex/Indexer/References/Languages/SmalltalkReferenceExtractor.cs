namespace CodeIndex.Indexer;

internal static class SmalltalkReferenceExtractor
{
    public static void EmitAdditionalCallReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference,
        IReadOnlySet<string>? definitionNames)
    {
        LanguageReferenceExtractionSupport.EmitAdditionalCallReferences(
            "smalltalk",
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
