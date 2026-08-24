using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static CoreReferenceLineFlow ProcessCoreReferenceLine(
        CoreReferenceLoopContext loop,
        CoreReferenceLoopState state,
        int lineIndex)
    {
        if (!TryPrepareCoreReferenceLine(
                loop,
                state,
                lineIndex,
                out var prepared))
        {
            return CoreReferenceLineFlow.LineConsumed;
        }

        return EmitCoreOrderedReferencePhases(loop, state, in prepared);
    }

    private static bool TryPrepareCoreReferenceLine(
        CoreReferenceLoopContext loop,
        CoreReferenceLoopState state,
        int lineIndex,
        out CorePreparedReferenceLine prepared)
    {
        var request = loop.Request;
        var input = loop.Preparation;
        var language = request.Language;
        var lineNumber = lineIndex + 1;
        var originalLine = input.Lines[lineIndex];
        var languageLines = PrepareCoreLanguageLine(
            loop,
            state,
            lineIndex,
            originalLine);
        var preparedLine = languageLines.PreparedLine;
        var preparedLineIsWhiteSpace =
            string.IsNullOrWhiteSpace(preparedLine);
        var csharpAttributeRanges = loop.CSharpAttributeRanges?[lineIndex];
        var csharpAttributeTopLevelRanges =
            loop.CSharpAttributeTopLevelRanges?[lineIndex];

        if (EmitCoreDocumentationAndSpecialLineReferences(
                loop,
                state,
                lineIndex,
                originalLine,
                preparedLine,
                preparedLineIsWhiteSpace,
                csharpAttributeRanges,
                out var sourceContext))
        {
            prepared = default;
            return false;
        }

        if (preparedLineIsWhiteSpace)
        {
            if (language == "csharp"
                && (state.PendingCSharpMultiLineTypePattern.WaitingForHead
                    || state.PendingCSharpMultiLineTypePattern
                        .PendingTypeExpression != null))
            {
                prepared = default;
                return false;
            }

            if (language == "csharp")
            {
                CSharpReferenceExtractor
                    .FlushPendingMultiLineTypePatternReference(
                        ref state.PendingCSharpMultiLineTypePattern,
                        loop.CSharpQualifiedConstantPatternMemberLookup,
                        loop.CSharpUsingAliases,
                        loop.CSharpUsingStatics,
                        loop.Lookups.HasActiveSameFileCSharpTypeCandidate,
                        loop.References,
                        loop.Seen,
                        request.FileId);
            }

            prepared = default;
            return false;
        }

        if (sourceContext.Length == 0)
        {
            prepared = default;
            return false;
        }

        var definitionNames =
            loop.DefinitionNamesByLine.TryGetValue(
                lineNumber,
                out var namesOnLine)
                ? namesOnLine
                : null;
        Dictionary<string, HashSet<int>>?
            scientificDefinitionNameIndices = null;
        loop.ScientificDefinitionNameIndicesByLine?.TryGetValue(
            lineNumber,
            out scientificDefinitionNameIndices);
        List<SqlReferenceExtractor.DefinitionLeafSpan>?
            sqlDefinitionLeafSpans = null;
        if (language == "sql")
        {
            loop.SqlDefinitionLeafSpansByLine?.TryGetValue(
                lineNumber,
                out sqlDefinitionLeafSpans);
        }

        var container = loop.ContainerResolver.Find(lineNumber);
        var definitionState = new CoreLineDefinitionState(
            language,
            sourceContext,
            preparedLine,
            definitionNames,
            loop.DefinitionNamesComparer,
            scientificDefinitionNameIndices,
            sqlDefinitionLeafSpans);
        var csharpLineHasWhereClause = language == "csharp"
            && preparedLine.IndexOf("where", StringComparison.Ordinal) >= 0
            && CSharpWhereClauseRegex.IsMatch(preparedLine);

        (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex,
            int CloseBraceIndex)? javaSameLineCtor = null;
        if (language == "java")
        {
            javaSameLineCtor =
                JavaReferenceExtractor.TryBuildSameLineCtorSpan(
                    preparedLine,
                    lineNumber,
                    loop.Lookups.GetEnclosingTypeCandidates);
        }

        var containerResolver = state.LineContainerResolver;
        containerResolver.ResetForLine(
            lineIndex,
            lineNumber,
            container,
            csharpLineHasWhereClause,
            javaSameLineCtor);
        var line = new CoreReferenceLineContext(
            request.FileId,
            language,
            input.Lines,
            input.PreparedLines,
            lineIndex,
            preparedLine,
            originalLine,
            sourceContext,
            lineNumber,
            loop.References,
            loop.Seen,
            container,
            definitionNames,
            containerResolver.ResolveContainerForCall,
            state.IsIgnoredCallName);
        prepared = new CorePreparedReferenceLine(
            in line,
            languageLines.OriginalLineForLanguage,
            csharpAttributeRanges,
            csharpAttributeTopLevelRanges,
            definitionState,
            javaSameLineCtor,
            containerResolver);
        return true;
    }

    private static (
        string PreparedLine,
        string OriginalLineForLanguage) PrepareCoreLanguageLine(
        CoreReferenceLoopContext loop,
        CoreReferenceLoopState state,
        int lineIndex,
        string originalLine)
    {
        var input = loop.Preparation;
        var preparedLine = input.LuaPreparedLines?[lineIndex]
            ?? input.LispReferenceLines?[lineIndex]
            ?? input.PreparedLines[lineIndex];
        var originalLineForLanguage = originalLine;
        if (loop.Request.Language == "sass")
        {
            preparedLine = CssReferenceExtractor.MaskSassBlockCommentLine(
                preparedLine,
                state.SassPreparedCommentState!);
            originalLineForLanguage =
                CssReferenceExtractor.MaskSassBlockCommentLine(
                    originalLine,
                    state.SassOriginalCommentState!);
        }
        else if (loop.Request.Language == "stylus")
        {
            preparedLine =
                CssReferenceExtractor.MaskSassStylusBlockCommentLine(
                    preparedLine,
                    ref state.SassStylusPreparedInBlockComment);
            originalLineForLanguage =
                CssReferenceExtractor.MaskSassStylusBlockCommentLine(
                    originalLine,
                    ref state.SassStylusOriginalInBlockComment);
        }

        return (preparedLine, originalLineForLanguage);
    }

    private static bool EmitCoreDocumentationAndSpecialLineReferences(
        CoreReferenceLoopContext loop,
        CoreReferenceLoopState state,
        int lineIndex,
        string originalLine,
        string preparedLine,
        bool preparedLineIsWhiteSpace,
        List<(int start, int end)>? csharpAttributeRangesOnLine,
        out string sourceContext)
    {
        var request = loop.Request;
        var lineNumber = lineIndex + 1;
        EmitCoreLanguageDocumentationReferences(
            loop,
            state,
            lineIndex,
            lineNumber,
            originalLine,
            preparedLine,
            csharpAttributeRangesOnLine);

        if (preparedLineIsWhiteSpace
            && request.Language
                is not ("cmake"
                    or "justfile"
                    or "makefile"
                    or "msbuild"
                    or "graphql"
                    or "html"
                    or "markdown"))
        {
            sourceContext = string.Empty;
            return false;
        }

        if (request.Language
            is "cmake" or "justfile" or "makefile" or "msbuild")
        {
            sourceContext = originalLine;
            if (string.IsNullOrWhiteSpace(sourceContext))
                return false;

            BuildAutomationReferenceExtractor.EmitReferences(
                request.Language,
                originalLine,
                sourceContext,
                lineNumber,
                loop.References,
                loop.Seen,
                request.FileId,
                loop.ContainerResolver.Find(lineNumber));
            return true;
        }

        if (request.Language is not ("graphql" or "html" or "markdown"))
        {
            sourceContext = originalLine;
            return false;
        }

        sourceContext = originalLine;
        if (string.IsNullOrWhiteSpace(sourceContext))
            return false;

        MarkupSchemaReferenceExtractor.EmitReferences(
            request.Language,
            originalLine,
            sourceContext,
            lineNumber,
            loop.References,
            loop.Seen,
            request.FileId,
            loop.ContainerResolver.Find(lineNumber),
            state.MarkupSchemaState);
        return true;
    }

}
