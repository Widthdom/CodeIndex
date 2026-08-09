using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class KotlinReferenceExtractor
{
    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var genericParameterNames = CollectGenericParameterNames(preparedLine);
        EmitCallableSignatureTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        EmitPrimaryConstructorTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        EmitHeritageTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        EmitGenericBoundReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        EmitExtensionPropertyReceiverTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        if (preparedLine.IndexOf(':') >= 0
            && (preparedLine.IndexOf("val", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("var", StringComparison.Ordinal) >= 0))
        {
            TypedLanguageReferenceExtractor.EmitColonVariableTypeReferences(
                preparedLine,
                DeclarationKeywords,
                "kotlin",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                genericParameterNames);
        }

        if (!preparedLine.TrimStart().StartsWith("import ", StringComparison.Ordinal)
            && (preparedLine.IndexOf("is", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("as", StringComparison.Ordinal) >= 0))
        {
            TypedLanguageReferenceExtractor.EmitKeywordFollowingTypeReferences(
                preparedLine,
                TypeOperatorKeywords,
                "kotlin",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                genericParameterNames);
        }
    }

    private static void EmitCallableSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        if (preparedLine.IndexOf("fun", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        var funIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "fun");
        if (funIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', funIndex + "fun".Length);
        if (openParen <= funIndex)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        EmitExtensionFunctionReceiverTypeReferences(
            preparedLine,
            funIndex,
            openParen,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            ignoredSegments);

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            ignoredSegments);

        var returnColon = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, closeParen + 1);
        if (returnColon >= preparedLine.Length || preparedLine[returnColon] != ':')
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, returnColon + 1);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart),
            ignoredSegments);
    }

    private static void EmitExtensionFunctionReceiverTypeReferences(
        string preparedLine,
        int funIndex,
        int openParen,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        var headStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, funIndex + "fun".Length);
        if (headStart >= openParen)
            return;

        if (preparedLine[headStart] == '<')
        {
            var genericClose = ReferenceExtractor.FindMatchingChar(preparedLine, headStart, '<', '>');
            if (genericClose < 0 || genericClose >= openParen)
                return;
            headStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, genericClose + 1);
            if (headStart >= openParen)
                return;
        }

        var receiverDot = FindLastTopLevelChar(preparedLine, '.', headStart, openParen);
        if (receiverDot <= headStart)
            return;

        var receiverEnd = receiverDot;
        while (receiverEnd > headStart && char.IsWhiteSpace(preparedLine[receiverEnd - 1]))
            receiverEnd--;
        if (receiverEnd <= headStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(headStart, receiverEnd - headStart),
            headStart,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(headStart),
            ignoredSegments);
    }

    private static void EmitExtensionPropertyReceiverTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        if (preparedLine.IndexOf('.') < 0
            || (preparedLine.IndexOf("val", StringComparison.Ordinal) < 0
                && preparedLine.IndexOf("var", StringComparison.Ordinal) < 0))
        {
            return;
        }

        foreach (var keyword in DeclarationKeywords)
        {
            foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, keyword))
            {
                var declarationStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, keywordIndex + keyword.Length);
                if (declarationStart >= preparedLine.Length)
                    continue;

                var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', declarationStart);
                var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', declarationStart);
                var declarationEnd = preparedLine.Length;
                if (colonIndex >= 0)
                    declarationEnd = Math.Min(declarationEnd, colonIndex);
                if (assignmentIndex >= 0)
                    declarationEnd = Math.Min(declarationEnd, assignmentIndex);
                if (declarationEnd <= declarationStart)
                    continue;

                var receiverDot = FindLastTopLevelChar(preparedLine, '.', declarationStart, declarationEnd);
                if (receiverDot <= declarationStart)
                    continue;

                var receiverEnd = receiverDot;
                while (receiverEnd > declarationStart && char.IsWhiteSpace(preparedLine[receiverEnd - 1]))
                    receiverEnd--;
                if (receiverEnd <= declarationStart)
                    continue;

                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    preparedLine.Substring(declarationStart, receiverEnd - declarationStart),
                    declarationStart,
                    "kotlin",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(declarationStart),
                    ignoredSegments);
            }
        }
    }

    private static int FindLastTopLevelChar(string text, char target, int startIndex, int endIndex)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        var braceDepth = 0;
        var last = -1;
        var end = Math.Min(text.Length, endIndex);
        for (var i = Math.Max(0, startIndex); i < end; i++)
        {
            var ch = text[i];
            if (angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0 && ch == target)
                last = i;

            switch (ch)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }
        }

        return last;
    }

    private static void EmitHeritageTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        if (preparedLine.IndexOf(':') < 0)
            return;

        var trimmed = preparedLine.TrimStart();
        if (!(trimmed.StartsWith("class ", StringComparison.Ordinal)
              || trimmed.StartsWith("data class ", StringComparison.Ordinal)
              || trimmed.StartsWith("sealed class ", StringComparison.Ordinal)
              || trimmed.StartsWith("interface ", StringComparison.Ordinal)
              || trimmed.StartsWith("object ", StringComparison.Ordinal)
              || trimmed.StartsWith("enum class ", StringComparison.Ordinal)))
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
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            trimTopLevelCallArguments: true,
            ignoredSegments: ignoredSegments);
    }

    private static void EmitPrimaryConstructorTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        if (preparedLine.IndexOf('(') < 0)
            return;

        var trimmed = preparedLine.TrimStart();
        if (!(trimmed.StartsWith("class ", StringComparison.Ordinal)
              || trimmed.StartsWith("data class ", StringComparison.Ordinal)
              || trimmed.StartsWith("sealed class ", StringComparison.Ordinal)
              || trimmed.StartsWith("enum class ", StringComparison.Ordinal)))
        {
            return;
        }

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(');
        if (openParen < 0)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            ignoredSegments);
    }

    private static void EmitGenericBoundReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? genericParameterNames)
    {
        var genericOpenIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '<');
        if (genericOpenIndex >= 0)
        {
            TypedLanguageReferenceExtractor.EmitGenericColonBoundReferences(
                preparedLine,
                genericOpenIndex,
                "kotlin",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                genericParameterNames);
        }

        if (preparedLine.IndexOf("where", StringComparison.Ordinal) >= 0)
        {
            TypedLanguageReferenceExtractor.EmitWhereClauseTypeReferences(
                preparedLine,
                "kotlin",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                genericParameterNames);
        }
    }

    private static IReadOnlySet<string> CollectGenericParameterNames(string preparedLine)
    {
        if (preparedLine.IndexOf('<') < 0)
            return EmptyGenericParameterNames;

        foreach (var funIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "fun"))
        {
            var genericOpenIndex = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, funIndex + "fun".Length);
            if (genericOpenIndex < preparedLine.Length && preparedLine[genericOpenIndex] == '<')
                return CollectGenericParameterNamesFromClause(preparedLine, genericOpenIndex);
        }

        foreach (var keyword in GenericOwnerKeywords)
        {
            foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, keyword))
            {
                var nameStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, keywordIndex + keyword.Length);
                var nameEnd = ConsumeKotlinDeclarationName(preparedLine, nameStart);
                if (nameEnd <= nameStart)
                    continue;

                var genericOpenIndex = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, nameEnd);
                if (genericOpenIndex < preparedLine.Length && preparedLine[genericOpenIndex] == '<')
                    return CollectGenericParameterNamesFromClause(preparedLine, genericOpenIndex);
            }
        }

        return EmptyGenericParameterNames;
    }

    private static IReadOnlySet<string> CollectGenericParameterNamesFromClause(string preparedLine, int genericOpenIndex)
    {
        var genericCloseIndex = ReferenceExtractor.FindMatchingChar(preparedLine, genericOpenIndex, '<', '>');
        if (genericCloseIndex <= genericOpenIndex)
            return EmptyGenericParameterNames;

        HashSet<string>? names = null;
        var clause = preparedLine.AsSpan(genericOpenIndex + 1, genericCloseIndex - genericOpenIndex - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
        {
            var fragment = clause.Slice(segmentStart, segmentLength).ToString();
            if (TryReadGenericParameterName(fragment, out var name))
                (names ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
        }

        return names ?? EmptyGenericParameterNames;
    }

    private static int ConsumeKotlinDeclarationName(string preparedLine, int startIndex)
    {
        if (startIndex >= preparedLine.Length)
            return startIndex;

        if (preparedLine[startIndex] == '`')
        {
            var close = preparedLine.IndexOf('`', startIndex + 1);
            return close < 0 ? startIndex : close + 1;
        }

        var index = startIndex;
        while (index < preparedLine.Length && ReferenceExtractor.IsJavaIdentifierPart(preparedLine[index]))
            index++;

        return index;
    }

    private static bool TryReadGenericParameterName(string fragment, out string name)
    {
        name = string.Empty;
        var index = 0;
        while (index < fragment.Length)
        {
            while (index < fragment.Length && char.IsWhiteSpace(fragment[index]))
                index++;

            if (index >= fragment.Length)
                return false;

            if (fragment[index] == '@')
            {
                index = ReferenceExtractor.SkipJavaAnnotation(fragment, index);
                continue;
            }

            var tokenStart = index;
            if (!ReferenceExtractor.IsJavaIdentifierPart(fragment[index]))
                return false;

            index++;
            while (index < fragment.Length && ReferenceExtractor.IsJavaIdentifierPart(fragment[index]))
                index++;

            var token = fragment.Substring(tokenStart, index - tokenStart);
            if (token is "reified" or "in" or "out")
                continue;

            name = token;
            return true;
        }

        return false;
    }

}
