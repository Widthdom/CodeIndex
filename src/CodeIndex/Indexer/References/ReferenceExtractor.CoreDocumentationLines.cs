using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CoreDocumentationLineContext(
        long FileId,
        string Language,
        string[] Lines,
        string[] PreparedLines,
        string[] StructuralLines,
        int LineIndex,
        int LineNumber,
        string OriginalLine,
        string PreparedLine,
        List<ReferenceRecord> References,
        ReferenceDedupeSet Seen,
        IReadOnlyList<SymbolRecord> ContainerCandidates,
        InnermostContainerResolver ContainerResolver,
        CoreExtractionLookups Lookups,
        bool[]? CSharpLinesInsideMultilineStringContent,
        bool[]? CSharpLinesInsideBlockComment,
        List<(int start, int end)>? CSharpAttributeRangesOnLine,
        List<(int start, int end)>?[]? CSharpAttributeRanges,
        Func<SymbolRecord?>? GetPhpLineContainer);

    private static void EmitCoreDocumentationReferences(
        in CoreDocumentationLineContext line,
        ref bool csharpInDelimitedDocComment,
        ref bool jvmInDelimitedDocComment,
        ref bool phpInDocblock,
        ref SymbolRecord? phpDocblockContainer,
        ref HashSet<string>? phpDocblockPropertyNames)
    {
        if (line.Language == "csharp"
            && line.CSharpLinesInsideMultilineStringContent != null
            && !(line.CSharpLinesInsideMultilineStringContent?[line.LineIndex] ?? false)
            && TryGetCSharpXmlDocCommentSpan(
                line.OriginalLine,
                csharpInDelimitedDocComment,
                line.CSharpLinesInsideBlockComment?[line.LineIndex] ?? false,
                out var csharpDocCommentStartIndex,
                out var csharpDocCommentEndExclusive,
                out var nextCsharpDelimitedDocComment))
        {
            var csharpDocCommentText = line.OriginalLine[csharpDocCommentStartIndex..csharpDocCommentEndExclusive];
            if (csharpDocCommentText.IndexOf("cref=\"", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var innermostContainer = line.ContainerResolver.Find(line.LineNumber);
                var sameLineDeclarationStartColumn = GetCSharpSameLineDocumentedDeclarationStartColumn(
                    line.OriginalLine,
                    csharpDocCommentEndExclusive,
                    nextCsharpDelimitedDocComment);
                var docContainer = FindDocumentedContainer(
                    line.ContainerCandidates,
                    line.StructuralLines[line.LineIndex],
                    line.PreparedLine,
                    line.CSharpAttributeRangesOnLine,
                    line.LineNumber,
                    sameLineDeclarationStartColumn);
                if (docContainer != null
                    && (docContainer.StartLine == line.LineNumber
                        || CanAttachCSharpXmlDocCommentToNextDeclaration(
                            innermostContainer,
                            line.Lookups.GetCSharpXmlDocAttachmentScopeCandidates(),
                            line.CSharpAttributeRanges,
                            line.PreparedLines,
                            line.LineNumber,
                            docContainer)))
                {
                    CSharpReferenceExtractor.EmitDocCrefReferences(
                        csharpDocCommentText,
                        line.References,
                        line.Seen,
                        line.FileId,
                        csharpDocCommentStartIndex,
                        csharpDocCommentText.Trim(),
                        line.LineNumber,
                        docContainer);
                }
            }
            csharpInDelimitedDocComment = nextCsharpDelimitedDocComment;
        }
        else if (line.Language is "java" or "kotlin"
                 && TryGetJvmDocCommentSpan(
                     line.OriginalLine,
                     jvmInDelimitedDocComment,
                     out var jvmDocCommentStartIndex,
                     out var jvmDocCommentEndExclusive,
                     out var jvmSameLineDeclarationStartColumn,
                     out var nextJvmDelimitedDocComment))
        {
            if (jvmDocCommentEndExclusive > jvmDocCommentStartIndex)
            {
                var docContainer = FindJvmDocumentedContainer(
                    line.ContainerCandidates,
                    line.Lines,
                    line.StructuralLines[line.LineIndex],
                    line.LineNumber,
                    jvmSameLineDeclarationStartColumn);
                if (docContainer != null)
                {
                    var docText = line.OriginalLine[jvmDocCommentStartIndex..jvmDocCommentEndExclusive];
                    EmitJvmDocLinkReferences(
                        line.Language,
                        docText,
                        line.References,
                        line.Seen,
                        line.FileId,
                        jvmDocCommentStartIndex,
                        docText.Trim(),
                        line.LineNumber,
                        docContainer);
                }
            }

            jvmInDelimitedDocComment = nextJvmDelimitedDocComment;
        }

        if (line.Language == "r")
        {
            var roxygenContext = line.OriginalLine.Trim();
            if (roxygenContext.Length > 0)
            {
                RReferenceExtractor.EmitRoxygenImportFromReferences(
                    line.OriginalLine,
                    line.References,
                    line.Seen,
                    line.FileId,
                    roxygenContext,
                    line.LineNumber,
                    container: null);
                RReferenceExtractor.EmitRoxygenImportReferences(
                    line.OriginalLine,
                    line.References,
                    line.Seen,
                    line.FileId,
                    roxygenContext,
                    line.LineNumber,
                    container: null);
                RReferenceExtractor.EmitRoxygenMethodReferences(
                    line.OriginalLine,
                    line.References,
                    line.Seen,
                    line.FileId,
                    roxygenContext,
                    line.LineNumber,
                    container: null);
            }
        }

        if (line.Language == "php")
        {
            EmitPhpLinePreambleReferences(
                line.OriginalLine,
                line.References,
                line.Seen,
                line.FileId,
                line.LineNumber,
                line.GetPhpLineContainer!,
                ref phpInDocblock,
                ref phpDocblockContainer,
                ref phpDocblockPropertyNames);
        }
    }
}
