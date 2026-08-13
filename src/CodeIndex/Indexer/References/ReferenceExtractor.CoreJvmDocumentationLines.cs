namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitCoreJvmDocumentationReferences(
        in CoreDocumentationLineContext line,
        ref bool inDelimitedDocComment)
    {
        if (!TryGetJvmDocCommentSpan(
                line.OriginalLine,
                inDelimitedDocComment,
                out var docCommentStartIndex,
                out var docCommentEndExclusive,
                out var sameLineDeclarationStartColumn,
                out var nextDelimitedDocComment))
        {
            return;
        }

        if (docCommentEndExclusive > docCommentStartIndex)
        {
            EmitCoreJvmDocLinkReferences(
                in line,
                docCommentStartIndex,
                docCommentEndExclusive,
                sameLineDeclarationStartColumn);
        }

        inDelimitedDocComment = nextDelimitedDocComment;
    }

    private static void EmitCoreJvmDocLinkReferences(
        in CoreDocumentationLineContext line,
        int docCommentStartIndex,
        int docCommentEndExclusive,
        int sameLineDeclarationStartColumn)
    {
        var docContainer = FindJvmDocumentedContainer(
            line.ContainerCandidates,
            line.Lines,
            line.StructuralLines[line.LineIndex],
            line.LineNumber,
            sameLineDeclarationStartColumn);
        if (docContainer == null)
            return;

        var docText = line.OriginalLine[docCommentStartIndex..docCommentEndExclusive];
        EmitJvmDocLinkReferences(
            line.Language,
            docText,
            line.References,
            line.Seen,
            line.FileId,
            docCommentStartIndex,
            docText.Trim(),
            line.LineNumber,
            docContainer);
    }
}
