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
        List<(int start, int end)>?[]? CSharpAttributeRanges);

    private struct CoreDocumentationState
    {
        internal bool CSharpInDelimitedDocComment;
        internal bool JvmInDelimitedDocComment;
        internal PhpDocumentationState Php;
    }

    private static void EmitCoreLanguageDocumentationReferences(
        CoreReferenceLoopContext loop,
        CoreReferenceLoopState state,
        int lineIndex,
        int lineNumber,
        string originalLine,
        string preparedLine,
        List<(int start, int end)>? csharpAttributeRangesOnLine)
    {
        var request = loop.Request;
        if (request.Language == "php")
        {
            var phpLine = new CorePhpDocumentationLineContext(
                request.FileId,
                originalLine,
                lineNumber,
                loop.References,
                loop.Seen,
                loop.ContainerResolver);
            EmitPhpDocumentationReferences(
                in phpLine,
                ref state.Documentation.Php);
            return;
        }

        if (request.Language is not ("csharp" or "java" or "kotlin" or "r"))
            return;

        var input = loop.Preparation;
        var documentationLine = new CoreDocumentationLineContext(
            request.FileId,
            request.Language,
            input.Lines,
            input.PreparedLines,
            input.StructuralLines,
            lineIndex,
            lineNumber,
            originalLine,
            preparedLine,
            loop.References,
            loop.Seen,
            loop.ContainerCandidates,
            loop.ContainerResolver,
            loop.Lookups,
            input.CSharpLinesInsideMultilineStringContent,
            input.CSharpLinesInsideBlockComment,
            csharpAttributeRangesOnLine,
            loop.CSharpAttributeRanges);
        EmitCoreDocumentationReferences(
            in documentationLine,
            ref state.Documentation);
    }

    private static void EmitCoreDocumentationReferences(
        in CoreDocumentationLineContext line,
        ref CoreDocumentationState state)
    {
        if (line.Language == "csharp")
            EmitCoreCSharpDocumentationReferences(in line, ref state.CSharpInDelimitedDocComment);
        else if (line.Language is "java" or "kotlin")
            EmitCoreJvmDocumentationReferences(in line, ref state.JvmInDelimitedDocComment);
        else if (line.Language == "r")
            EmitRDocumentationReferences(in line);
    }
}
