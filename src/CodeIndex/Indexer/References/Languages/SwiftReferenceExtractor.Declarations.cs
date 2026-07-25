using CodeIndex.Models;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class SwiftReferenceExtractor
{
    private static void EmitExtensionTargetReference(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var extensionIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "extension");
        if (extensionIndex < 0)
            return;

        var targetStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, extensionIndex + "extension".Length);
        var targetEnd = FindSwiftExtensionTargetEnd(preparedLine, targetStart);
        if (targetEnd <= targetStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(targetStart, targetEnd - targetStart),
            targetStart,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(targetStart));
    }

    private static int FindSwiftExtensionTargetEnd(string preparedLine, int targetStart)
    {
        var expressionEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, targetStart, stopAtComma: false);
        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', targetStart);
        if (colonIndex >= 0 && colonIndex < expressionEnd)
            return colonIndex;

        return expressionEnd;
    }

    private static void EmitGenericBoundReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var genericOpenIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '<');
        if (genericOpenIndex >= 0)
        {
            TypedLanguageReferenceExtractor.EmitGenericColonBoundReferences(
                preparedLine,
                genericOpenIndex,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }

        TypedLanguageReferenceExtractor.EmitWhereClauseTypeReferences(
            preparedLine,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        EmitWhereClauseSameTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitCallableSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var funcIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "func");
        if (funcIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', funcIndex + "func".Length);
        if (openParen <= funcIndex)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        EmitTypedThrowsReferences(preparedLine, closeParen + 1, references, seen, fileId, context, lineNumber, resolveContainerForColumn);

        var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(preparedLine, "->", closeParen + 1);
        if (arrowIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, arrowIndex + 2);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static void EmitClosureSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var braceIndex = preparedLine.IndexOf('{', StringComparison.Ordinal);
        if (braceIndex < 0)
            return;

        var openParen = preparedLine.IndexOf('(', braceIndex + 1);
        if (openParen < 0)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        var inIndex = FindSwiftClosureInKeyword(preparedLine, closeParen + 1);
        if (inIndex < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(preparedLine, "->", closeParen + 1);
        if (arrowIndex < 0 || arrowIndex >= inIndex)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, arrowIndex + 2);
        if (typeStart >= inIndex)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, inIndex - typeStart),
            typeStart,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static int FindSwiftClosureInKeyword(string preparedLine, int startIndex)
    {
        foreach (var inIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "in", startIndex))
            return inIndex;

        return -1;
    }

    private static void EmitWhereClauseSameTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var whereIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "where"))
        {
            var clauseStart = whereIndex + "where".Length;
            var clauseEnd = FindSwiftWhereClauseEnd(preparedLine, clauseStart);

            var clause = preparedLine.Substring(clauseStart, clauseEnd - clauseStart);
            foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
            {
                var fragment = clause.Substring(segmentStart, segmentLength);
                var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(fragment, "==");
                if (equalsIndex < 0)
                    continue;

                var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, equalsIndex + 2);
                var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart, stopAtComma: false, stopAtArrow: false);
                if (typeEnd <= typeStart)
                    continue;

                var absoluteStart = clauseStart + segmentStart + typeStart;
                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    fragment.Substring(typeStart, typeEnd - typeStart),
                    absoluteStart,
                    "swift",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(absoluteStart));
            }
        }
    }

    private static int FindSwiftWhereClauseEnd(string preparedLine, int clauseStart)
    {
        var clauseEnd = preparedLine.Length;
        var braceIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '{', clauseStart);
        if (braceIndex >= 0)
            clauseEnd = Math.Min(clauseEnd, braceIndex);

        var semicolonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ';', clauseStart);
        if (semicolonIndex >= 0)
            clauseEnd = Math.Min(clauseEnd, semicolonIndex);

        return Math.Max(clauseStart, clauseEnd);
    }

    private static void EmitTypealiasRhsTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var typealiasIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "typealias");
        if (typealiasIndex < 0)
            return;

        var equalsIndex = FindTopLevelAssignmentEquals(preparedLine, typealiasIndex + "typealias".Length);
        if (equalsIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, equalsIndex + 1);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtComma: false, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static void EmitAssociatedTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var associatedTypeIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "associatedtype");
        if (associatedTypeIndex < 0)
            return;

        var declarationStart = associatedTypeIndex + "associatedtype".Length;
        var equalsIndex = FindTopLevelAssignmentEquals(preparedLine, declarationStart);
        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', declarationStart);

        if (colonIndex >= 0 && (equalsIndex < 0 || colonIndex < equalsIndex))
        {
            var constraintStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
            var constraintEnd = equalsIndex >= 0
                ? equalsIndex
                : TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, constraintStart, stopAtComma: false);
            if (constraintEnd > constraintStart)
            {
                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    preparedLine.Substring(constraintStart, constraintEnd - constraintStart),
                    constraintStart,
                    "swift",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(constraintStart));
            }
        }

        if (equalsIndex < 0)
            return;

        var defaultStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, equalsIndex + 1);
        var defaultEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, defaultStart, stopAtComma: false, stopAtArrow: false);
        if (defaultEnd <= defaultStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(defaultStart, defaultEnd - defaultStart),
            defaultStart,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(defaultStart));
    }

    private static int FindTopLevelAssignmentEquals(string preparedLine, int startIndex)
    {
        var searchStart = startIndex;
        while (searchStart < preparedLine.Length)
        {
            var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', searchStart);
            if (equalsIndex < 0)
                return -1;

            var previous = equalsIndex > 0 ? preparedLine[equalsIndex - 1] : '\0';
            var next = equalsIndex + 1 < preparedLine.Length ? preparedLine[equalsIndex + 1] : '\0';
            if (previous is not ('=' or '!' or '<' or '>') && next != '=')
                return equalsIndex;

            searchStart = equalsIndex + 1;
        }

        return -1;
    }

    private static void EmitTypedThrowsReferences(
        string preparedLine,
        int searchStart,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var throwsIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "throws", searchStart))
        {
            var openParen = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, throwsIndex + "throws".Length);
            if (openParen >= preparedLine.Length || preparedLine[openParen] != '(')
                continue;

            var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
            if (closeParen < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, openParen + 1);
            if (typeStart >= closeParen)
                return;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, closeParen - typeStart),
                typeStart,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
            return;
        }
    }

    private static void EmitHeritageTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var trimmed = preparedLine.TrimStart();
        if (!(trimmed.StartsWith("class ", StringComparison.Ordinal)
              || trimmed.StartsWith("struct ", StringComparison.Ordinal)
              || trimmed.StartsWith("protocol ", StringComparison.Ordinal)
              || trimmed.StartsWith("enum ", StringComparison.Ordinal)
              || trimmed.StartsWith("extension ", StringComparison.Ordinal)))
        {
            return;
        }

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':');
        if (colonIndex < 0)
            return;

        var listStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        var listEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, listStart, stopAtComma: false);
        if (listEnd <= listStart)
            return;

        TypedLanguageReferenceExtractor.EmitCommaSeparatedTypeListReferences(
            preparedLine,
            listStart,
            listEnd,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }
}
