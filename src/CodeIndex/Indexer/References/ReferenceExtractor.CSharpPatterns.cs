using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static void EmitCSharpTypePositionReferences(
        string preparedLine,
        string originalLine,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container,
        CSharpWhereConstraintState pendingWhereConstraint,
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern)
    {
        var csharpGenericParameterNames = CollectCSharpGenericParameterNamesForDeclaration(preparedLine);
        TryEmitCSharpBaseListReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, csharpGenericParameterNames);
        EmitCSharpWhereConstraintReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            csharpGenericParameterNames,
            pendingWhereConstraint);
        EmitDeclarationTypeReferences("csharp", preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, csharpGenericParameterNames);

        foreach (Match match in CSharpIsAsTypeTestRegex.Matches(preparedLine))
        {
            var typeGroup = match.Groups["type"];
            int continuationIndex = SkipWhitespace(preparedLine, typeGroup.Index + typeGroup.Length);
            if (TryEmitCSharpLogicalTypePatternHeads(
                    preparedLine,
                    typeGroup.Value,
                    typeGroup.Index,
                    continuationIndex,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate,
                    (logicalTypeExpression, logicalTypeIndex) => AddTypeExpressionSegments(
                        references,
                        seen,
                        fileId,
                        logicalTypeExpression,
                        logicalTypeIndex,
                        context,
                        lineNumber,
                        resolveContainerForColumn(logicalTypeIndex),
                        "csharp",
                        csharpGenericParameterNames)))
            {
                continue;
            }

            if (IsCSharpNonTypePatternExpression(typeGroup.Value)
                || IsCSharpConstantPatternMemberHead(
                    typeGroup.Value,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate)
                || IsCSharpLogicalConstantPatternAtCursor(
                    preparedLine,
                    typeGroup.Value,
                    continuationIndex,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate))
            {
                continue;
            }

            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                typeGroup.Value,
                typeGroup.Index,
                context,
                lineNumber,
                resolveContainerForColumn(typeGroup.Index),
                "csharp",
                csharpGenericParameterNames);
        }

        EmitCSharpCaseTypePatternReferences(
            preparedLine,
            originalLine,
            csharpQualifiedConstantPatternMemberLookup,
            csharpQualifiedTypePatternLookup,
            csharpUsingAliases,
            csharpUsingStatics,
            hasActiveSameFileCSharpTypeCandidate,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            ref pendingCSharpMultiLineTypePattern);
    }

    internal static void AdvanceCSharpMultiLineTypePatternState(
        string preparedLine,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        ref CSharpMultiLineTypePatternState state)
    {
        if (!state.WaitingForHead && state.PendingTypeExpression == null)
            return;

        var cursor = SkipWhitespace(preparedLine, 0);
        if (state.WaitingForHead)
        {
            if (!TryConsumeCSharpMultiLineTypePatternHead(
                    preparedLine,
                    context,
                    lineNumber,
                    resolveContainerForColumn,
                    ref cursor,
                    ref state))
            {
                if (IsStandaloneCSharpMultiLinePatternNegation(preparedLine))
                    return;

                state = default;
                return;
            }
        }
        else if (!TryConsumeCSharpLogicalPatternKeyword(preparedLine, cursor, out cursor))
        {
            FlushPendingCSharpMultiLineTypePatternReference(
                ref state,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);
            return;
        }
        else
        {
            FlushPendingCSharpMultiLineTypePatternReference(
                ref state,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);
            if (!TryConsumeCSharpMultiLineTypePatternHead(
                    preparedLine,
                    context,
                    lineNumber,
                    resolveContainerForColumn,
                    ref cursor,
                    ref state))
            {
                state = state with { WaitingForHead = true };
                return;
            }
        }

        while (TryConsumeCSharpLogicalPatternKeyword(
            preparedLine,
            SkipWhitespace(preparedLine, state.PendingTypeIndex + state.PendingTypeExpression!.Length),
            out cursor))
        {
            FlushPendingCSharpMultiLineTypePatternReference(
                ref state,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);
            if (!TryConsumeCSharpMultiLineTypePatternHead(
                    preparedLine,
                    context,
                    lineNumber,
                    resolveContainerForColumn,
                    ref cursor,
                    ref state))
            {
                state = state with { WaitingForHead = true };
                return;
            }
        }
    }

    private static bool TryConsumeCSharpMultiLineTypePatternHead(
        string preparedLine,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        ref int cursor,
        ref CSharpMultiLineTypePatternState state)
    {
        cursor = SkipWhitespace(preparedLine, cursor);
        if (TryConsumeCSharpPatternKeyword(preparedLine, ref cursor, "not"))
            cursor = SkipWhitespace(preparedLine, cursor);

        var match = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, cursor);
        if (!match.Success)
            return false;

        var typeGroup = match.Groups["type"];
        state = new CSharpMultiLineTypePatternState(
            WaitingForHead: false,
            PendingTypeExpression: typeGroup.Value,
            PendingTypeIndex: typeGroup.Index,
            PendingTypeLineNumber: lineNumber,
            PendingContext: context,
            PendingContainer: resolveContainerForColumn(typeGroup.Index));
        cursor = SkipWhitespace(preparedLine, typeGroup.Index + typeGroup.Length);
        return true;
    }

    internal static void FlushPendingCSharpMultiLineTypePatternReference(
        ref CSharpMultiLineTypePatternState state,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId)
    {
        if (state.PendingTypeExpression == null || state.PendingContext == null)
        {
            state = default;
            return;
        }

        if (!IsCSharpNonTypePatternExpression(state.PendingTypeExpression)
            && !IsCSharpConstantPatternMemberHead(
                state.PendingTypeExpression,
                state.PendingTypeLineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate))
        {
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                state.PendingTypeExpression,
                state.PendingTypeIndex,
                state.PendingContext,
                state.PendingTypeLineNumber,
                state.PendingContainer,
                "csharp");
        }

        state = default;
    }

    private static bool IsStandaloneCSharpMultiLinePatternNegation(string preparedLine)
    {
        var cursor = SkipWhitespace(preparedLine, 0);
        if (!TryConsumeCSharpPatternKeyword(preparedLine, ref cursor, "not"))
            return false;

        return SkipWhitespace(preparedLine, cursor) >= preparedLine.Length;
    }

    internal static void StartWaitingForCSharpMultiLineTypePatternHead(ref CSharpMultiLineTypePatternState state)
    {
        state = new CSharpMultiLineTypePatternState(
            WaitingForHead: true,
            PendingTypeExpression: null,
            PendingTypeIndex: 0,
            PendingTypeLineNumber: 0,
            PendingContext: null,
            PendingContainer: null);
    }

    private static void EmitCSharpCaseTypePatternReferences(
        string preparedLine,
        string originalLine,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern)
    {
        foreach (Match caseMatch in CSharpCaseLabelRegex.Matches(preparedLine))
        {
            int cursor = SkipWhitespace(preparedLine, caseMatch.Index + caseMatch.Length);
            bool hadLeadingNot = TryConsumeCSharpPatternKeyword(preparedLine, ref cursor, "not");
            if (hadLeadingNot)
                cursor = SkipWhitespace(preparedLine, cursor);

            var typeMatch = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, cursor);
            if (!typeMatch.Success)
            {
                var rawCaseCursor = SkipCSharpTriviaForward(originalLine, caseMatch.Index + caseMatch.Length);
                if (TryConsumeLeadingCSharpPatternKeyword(originalLine, ref rawCaseCursor, "not"))
                    rawCaseCursor = SkipCSharpTriviaForward(originalLine, rawCaseCursor);

                if (HasOnlyTrailingCSharpTrivia(originalLine, rawCaseCursor))
                    StartWaitingForCSharpMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
                continue;
            }

            var typeGroup = typeMatch.Groups["type"];
            var currentTypeExpression = typeGroup.Value;
            var currentTypeIndex = typeGroup.Index;
            var currentContinuationIndex = SkipWhitespace(preparedLine, typeGroup.Index + typeGroup.Length);
            var sawLogicalKeyword = false;
            var waitingForNextHead = false;

            while (TryConsumeCSharpLogicalPatternKeyword(preparedLine, currentContinuationIndex, out var nextHeadCursor))
            {
                sawLogicalKeyword = true;
                if (!IsCSharpLogicalConstantPatternHead(
                        preparedLine,
                        currentTypeExpression,
                        nextHeadCursor,
                        lineNumber,
                        csharpQualifiedConstantPatternMemberLookup,
                        csharpQualifiedTypePatternLookup,
                        csharpUsingAliases,
                        csharpUsingStatics,
                        hasActiveSameFileCSharpTypeCandidate))
                {
                    AddTypeExpressionSegments(
                        references,
                        seen,
                        fileId,
                        currentTypeExpression,
                        currentTypeIndex,
                        context,
                        lineNumber,
                        resolveContainerForColumn(currentTypeIndex),
                        "csharp");
                }

                int nextTypeCursor = nextHeadCursor;
                if (TryConsumeCSharpPatternKeyword(preparedLine, ref nextTypeCursor, "not"))
                    nextTypeCursor = SkipWhitespace(preparedLine, nextTypeCursor);

                var nextMatch = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, nextTypeCursor);
                if (!nextMatch.Success)
                {
                    var rawNextTypeCursor = SkipCSharpTriviaForward(originalLine, nextHeadCursor);
                    if (TryConsumeLeadingCSharpPatternKeyword(originalLine, ref rawNextTypeCursor, "not"))
                        rawNextTypeCursor = SkipCSharpTriviaForward(originalLine, rawNextTypeCursor);

                    if (HasOnlyTrailingCSharpTrivia(originalLine, rawNextTypeCursor))
                    {
                        StartWaitingForCSharpMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
                        waitingForNextHead = true;
                    }
                    break;
                }

                var nextTypeGroup = nextMatch.Groups["type"];
                currentTypeExpression = nextTypeGroup.Value;
                currentTypeIndex = nextTypeGroup.Index;
                currentContinuationIndex = SkipWhitespace(preparedLine, currentTypeIndex + currentTypeExpression.Length);
            }

            if (waitingForNextHead)
                continue;

            if (sawLogicalKeyword)
            {
                if (!IsCSharpNonTypePatternExpression(currentTypeExpression)
                    && !IsCSharpConstantPatternMemberHead(
                        currentTypeExpression,
                        lineNumber,
                        csharpQualifiedConstantPatternMemberLookup,
                        csharpUsingAliases,
                        csharpUsingStatics,
                        hasActiveSameFileCSharpTypeCandidate))
                {
                    AddTypeExpressionSegments(
                        references,
                        seen,
                        fileId,
                        currentTypeExpression,
                        currentTypeIndex,
                        context,
                        lineNumber,
                        resolveContainerForColumn(currentTypeIndex),
                        "csharp");
                }

                continue;
            }

            if (!IsCSharpCaseTypePatternContinuation(
                    preparedLine,
                    currentTypeExpression,
                    currentContinuationIndex,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate,
                    lineNumber))
            {
                continue;
            }

            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                currentTypeExpression,
                currentTypeIndex,
                context,
                lineNumber,
                resolveContainerForColumn(currentTypeIndex),
                "csharp");
        }
    }

    private static bool HasOnlyTrailingCSharpTrivia(string text, int cursor)
    {
        while (cursor < text.Length)
        {
            if (char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
                continue;
            }

            if (cursor + 1 < text.Length
                && text[cursor] == '/'
                && text[cursor + 1] == '/')
            {
                return true;
            }

            if (cursor + 1 < text.Length
                && text[cursor] == '/'
                && text[cursor + 1] == '*')
            {
                var commentEnd = text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return true;

                cursor = commentEnd + 2;
                continue;
            }

            return false;
        }

        return true;
    }

    private static int SkipCSharpTriviaForward(string text, int cursor)
    {
        while (cursor < text.Length)
        {
            if (char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
                continue;
            }

            if (cursor + 1 < text.Length
                && text[cursor] == '/'
                && text[cursor + 1] == '/')
            {
                return text.Length;
            }

            if (cursor + 1 < text.Length
                && text[cursor] == '/'
                && text[cursor + 1] == '*')
            {
                var commentEnd = text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return text.Length;

                cursor = commentEnd + 2;
                continue;
            }

            break;
        }

        return cursor;
    }

    private static bool TryConsumeLeadingCSharpPatternKeyword(string text, ref int cursor, string keyword)
    {
        if (string.IsNullOrEmpty(keyword))
            return false;

        cursor = SkipCSharpTriviaForward(text, cursor);
        if (cursor + keyword.Length > text.Length
            || !text.AsSpan(cursor, keyword.Length).Equals(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        var nextIndex = cursor + keyword.Length;
        if (nextIndex < text.Length
            && (char.IsLetterOrDigit(text[nextIndex]) || text[nextIndex] == '_'))
        {
            return false;
        }

        cursor = nextIndex;
        return true;
    }

}
