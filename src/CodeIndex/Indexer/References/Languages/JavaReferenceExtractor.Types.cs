using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class JavaReferenceExtractor
{
    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        var genericParameterNames = CollectGenericParameterNamesForDeclaration(preparedLine);
        EmitKeywordTypeListReferences(
            preparedLine,
            "extends",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            genericParameterNames);
        EmitKeywordTypeListReferences(
            preparedLine,
            "implements",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            genericParameterNames);
        EmitKeywordTypeListReferences(
            preparedLine,
            "permits",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            genericParameterNames);
        EmitGenericBoundReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitThrowsReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        ReferenceExtractor.EmitDeclarationTypeReferences(
            "java",
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            genericParameterNames);

        foreach (Match match in Regex.EnumerateMatches(
                     InstanceofRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var typeGroup = match.Groups["type"];
            ReferenceExtractor.AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                typeGroup.Value,
                typeGroup.Index,
                context,
                lineNumber,
                resolveContainerForColumn(typeGroup.Index),
                "java");
        }
    }

    private static void EmitKeywordTypeListReferences(
        string line,
        string keyword,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        int keywordIndex = ReferenceExtractor.FindTopLevelKeyword(line, keyword);
        if (keywordIndex < 0)
            return;

        int listStart = keywordIndex + keyword.Length;
        while (listStart < line.Length && char.IsWhiteSpace(line[listStart]))
            listStart++;

        var remaining = line.AsSpan(listStart);
        int listEnd = ReferenceExtractor.FindJavaTypeListTerminator(remaining);
        if (listEnd < 0)
            listEnd = remaining.Length;
        var typeList = remaining[..listEnd];
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(typeList))
        {
            var leading = ReferenceExtractor.CountLeadingWhitespace(typeList, segmentStart, segmentLength);
            var trimmedLength = segmentLength - leading;
            while (trimmedLength > 0 && char.IsWhiteSpace(typeList[segmentStart + leading + trimmedLength - 1]))
                trimmedLength--;
            if (trimmedLength == 0)
                continue;
            var absoluteStart = listStart + segmentStart + leading;
            var rawSegment = typeList.Slice(segmentStart + leading, trimmedLength);
            ReferenceExtractor.AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                rawSegment.ToString(),
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                "java",
                ignoredSegments: ignoredSegments);
        }
    }

    private static void EmitGenericBoundReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        EmitCallableGenericBoundReferences(line, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitNamedTypeGenericBoundReferences(line, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static IReadOnlySet<string> CollectGenericParameterNamesForDeclaration(string line)
    {
        if (ReferenceExtractor.TryFindCallableParameterList(line, "java", out var callableNameStart, out _, out _))
        {
            var headerEnd = callableNameStart;
            if (ReferenceExtractor.TryGetCallableReturnTypeSpan(line, callableNameStart, "java", out var typeStart, out _))
                headerEnd = typeStart;

            if (headerEnd > 0)
                return CollectGenericParameterNamesFromHeader(line.Substring(0, headerEnd));
        }

        var tokens = ReferenceExtractor.GetTopLevelTokenSpans(line);
        if (tokens.Count < 2)
            return EmptyGenericParameterNames;

        for (int i = 0; i < tokens.Count; i++)
        {
            if (!IsNamedTypeKeyword(line.AsSpan(tokens[i].Start, tokens[i].Length)))
                continue;
            var nameIndex = i + 1;
            if (nameIndex >= tokens.Count)
                return EmptyGenericParameterNames;
            return CollectGenericParameterNamesFromHeader(line.Substring(tokens[nameIndex].Start, tokens[nameIndex].Length));
        }

        return EmptyGenericParameterNames;
    }

    private static IReadOnlySet<string> CollectGenericParameterNamesFromHeader(string header)
    {
        int openAngle = header.IndexOf('<');
        if (openAngle < 0)
            return EmptyGenericParameterNames;

        int closeAngle = ReferenceExtractor.FindMatchingChar(header, openAngle, '<', '>');
        if (closeAngle < 0)
            return EmptyGenericParameterNames;

        return CollectGenericParameterNames(header.Substring(openAngle + 1, closeAngle - openAngle - 1));
    }

    private static void EmitCallableGenericBoundReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (!ReferenceExtractor.TryFindCallableParameterList(line, "java", out var callableNameStart, out _, out _))
            return;

        var headerEnd = callableNameStart;
        if (ReferenceExtractor.TryGetCallableReturnTypeSpan(line, callableNameStart, "java", out var typeStart, out _))
            headerEnd = typeStart;

        if (headerEnd <= 0)
            return;

        EmitGenericBoundReferencesFromHeader(
            line.Substring(0, headerEnd),
            0,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitNamedTypeGenericBoundReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var tokens = ReferenceExtractor.GetTopLevelTokenSpans(line);
        if (tokens.Count < 2)
            return;

        int keywordIndex = -1;
        int nameIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (IsNamedTypeKeyword(line.AsSpan(tokens[i].Start, tokens[i].Length)))
            {
                keywordIndex = i;
                nameIndex = i + 1;
                break;
            }
        }

        if (keywordIndex < 0 || nameIndex < 0 || nameIndex >= tokens.Count)
            return;

        var nameToken = line.AsSpan(tokens[nameIndex].Start, tokens[nameIndex].Length);
        EmitGenericBoundReferencesFromHeader(
            nameToken,
            tokens[nameIndex].Start,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitGenericBoundReferencesFromHeader(
        ReadOnlySpan<char> header,
        int headerStartInLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        int openAngle = header.IndexOf('<');
        if (openAngle < 0)
            return;

        int closeAngle = ReferenceExtractor.FindMatchingChar(header, openAngle, '<', '>');
        if (closeAngle < 0)
            return;

        var parameterClauseText = header.Slice(openAngle + 1, closeAngle - openAngle - 1);
        var genericParameterNames = CollectGenericParameterNames(parameterClauseText);

        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(parameterClauseText))
        {
            var parameterLeading = ReferenceExtractor.CountLeadingWhitespace(parameterClauseText, segmentStart, segmentLength);
            var parameterLength = segmentLength - parameterLeading;
            while (parameterLength > 0 && char.IsWhiteSpace(parameterClauseText[segmentStart + parameterLeading + parameterLength - 1]))
                parameterLength--;
            if (parameterLength == 0)
                continue;

            var rawParameter = parameterClauseText.Slice(segmentStart + parameterLeading, parameterLength);
            int extendsIndex = ReferenceExtractor.FindTopLevelKeyword(rawParameter, "extends");
            if (extendsIndex < 0)
                continue;

            var boundsStart = extendsIndex + "extends".Length;
            var boundsLeading = ReferenceExtractor.CountLeadingWhitespace(rawParameter, boundsStart, rawParameter.Length - boundsStart);
            var boundsLength = rawParameter.Length - boundsStart - boundsLeading;
            while (boundsLength > 0 && char.IsWhiteSpace(rawParameter[boundsStart + boundsLeading + boundsLength - 1]))
                boundsLength--;
            if (boundsLength == 0)
                continue;

            var boundsText = rawParameter.Slice(boundsStart + boundsLeading, boundsLength);
            foreach (var (boundStart, boundLength) in ReferenceExtractor.SplitTopLevelAmpersandSpans(boundsText))
            {
                var boundLeading = ReferenceExtractor.CountLeadingWhitespace(boundsText, boundStart, boundLength);
                var rawBoundLength = boundLength - boundLeading;
                while (rawBoundLength > 0 && char.IsWhiteSpace(boundsText[boundStart + boundLeading + rawBoundLength - 1]))
                    rawBoundLength--;
                if (rawBoundLength == 0)
                    continue;

                var rawBound = boundsText.Slice(boundStart + boundLeading, rawBoundLength).ToString();
                var absoluteStart = headerStartInLine + openAngle + 1 + segmentStart + extendsIndex + "extends".Length + boundStart + boundLeading;
                ReferenceExtractor.AddTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    rawBound,
                    absoluteStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(absoluteStart),
                    "java",
                    genericParameterNames);
            }
        }
    }

    private static bool IsNamedTypeKeyword(ReadOnlySpan<char> token) =>
        token is "class" or "interface" or "enum" or "record";

    private static IReadOnlySet<string> CollectGenericParameterNames(ReadOnlySpan<char> parameterClause)
    {
        HashSet<string>? names = null;
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(parameterClause))
        {
            var parameterLeading = ReferenceExtractor.CountLeadingWhitespace(parameterClause, segmentStart, segmentLength);
            var parameterLength = segmentLength - parameterLeading;
            while (parameterLength > 0 && char.IsWhiteSpace(parameterClause[segmentStart + parameterLeading + parameterLength - 1]))
                parameterLength--;
            if (parameterLength == 0)
                continue;

            var rawParameter = parameterClause.Slice(segmentStart + parameterLeading, parameterLength);
            int extendsIndex = ReferenceExtractor.FindTopLevelKeyword(rawParameter, "extends");
            var nameFragment = extendsIndex >= 0 ? rawParameter[..extendsIndex] : rawParameter;
            if (TryReadGenericParameterName(nameFragment, out var name))
                (names ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
        }

        return names ?? EmptyGenericParameterNames;
    }

    private static bool TryReadGenericParameterName(ReadOnlySpan<char> text, out string name)
    {
        name = string.Empty;
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length)
                return false;
            if (text[i] == '@')
            {
                i = ReferenceExtractor.SkipJavaAnnotation(text, i);
                continue;
            }
            break;
        }

        int start = i;
        if (start >= text.Length || !ReferenceExtractor.IsJavaIdentifierPart(text[start]))
            return false;

        i++;
        while (i < text.Length && ReferenceExtractor.IsJavaIdentifierPart(text[i]))
            i++;

        name = text.Slice(start, i - start).ToString();
        return name.Length > 0;
    }

    private static void EmitThrowsReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        int keywordIndex = ReferenceExtractor.FindTopLevelKeyword(line, "throws");
        if (keywordIndex < 0)
            return;

        int listStart = keywordIndex + "throws".Length;
        while (listStart < line.Length && char.IsWhiteSpace(line[listStart]))
            listStart++;
        var remaining = line.AsSpan(listStart);
        int listEnd = ReferenceExtractor.FindTypeListTerminator(remaining, allowArrow: false);
        if (listEnd < 0)
            listEnd = remaining.Length;
        var typeList = remaining[..listEnd];
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(typeList))
        {
            var leading = ReferenceExtractor.CountLeadingWhitespace(typeList, segmentStart, segmentLength);
            var trimmedLength = segmentLength - leading;
            while (trimmedLength > 0 && char.IsWhiteSpace(typeList[segmentStart + leading + trimmedLength - 1]))
                trimmedLength--;
            if (trimmedLength == 0)
                continue;
            var absoluteStart = listStart + segmentStart + leading;
            var rawSegment = typeList.Slice(segmentStart + leading, trimmedLength);
            ReferenceExtractor.AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                rawSegment.ToString(),
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                "java",
                ignoredSegments);
        }
    }

}
