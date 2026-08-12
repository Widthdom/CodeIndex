namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitCoreCSharpDocumentationReferences(
        in CoreDocumentationLineContext line,
        ref bool inDelimitedDocComment)
    {
        if (line.CSharpLinesInsideMultilineStringContent == null
            || line.CSharpLinesInsideMultilineStringContent[line.LineIndex]
            || !TryGetCSharpXmlDocCommentSpan(
                line.OriginalLine,
                inDelimitedDocComment,
                line.CSharpLinesInsideBlockComment?[line.LineIndex] ?? false,
                out var docCommentStartIndex,
                out var docCommentEndExclusive,
                out var nextDelimitedDocComment))
        {
            return;
        }

        var docCommentText = line.OriginalLine[docCommentStartIndex..docCommentEndExclusive];
        if (docCommentText.IndexOf("cref=\"", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitCoreCSharpDocCrefReferences(
                in line,
                docCommentText,
                docCommentStartIndex,
                docCommentEndExclusive,
                nextDelimitedDocComment);
        }

        inDelimitedDocComment = nextDelimitedDocComment;
    }

    private static void EmitCoreCSharpDocCrefReferences(
        in CoreDocumentationLineContext line,
        string docCommentText,
        int docCommentStartIndex,
        int docCommentEndExclusive,
        bool nextDelimitedDocComment)
    {
        var innermostContainer = line.ContainerResolver.Find(line.LineNumber);
        var sameLineDeclarationStartColumn = GetCSharpSameLineDocumentedDeclarationStartColumn(
            line.OriginalLine,
            docCommentEndExclusive,
            nextDelimitedDocComment);
        var docContainer = FindDocumentedContainer(
            line.ContainerCandidates,
            line.StructuralLines[line.LineIndex],
            line.PreparedLine,
            line.CSharpAttributeRangesOnLine,
            line.LineNumber,
            sameLineDeclarationStartColumn);
        if (docContainer == null
            || (docContainer.StartLine != line.LineNumber
                && !CanAttachCSharpXmlDocCommentToNextDeclaration(
                    innermostContainer,
                    line.Lookups.GetCSharpXmlDocAttachmentScopeCandidates(),
                    line.CSharpAttributeRanges,
                    line.PreparedLines,
                    line.LineNumber,
                    docContainer)))
        {
            return;
        }

        CSharpReferenceExtractor.EmitDocCrefReferences(
            docCommentText,
            line.References,
            line.Seen,
            line.FileId,
            docCommentStartIndex,
            docCommentText.Trim(),
            line.LineNumber,
            docContainer);
    }
}
