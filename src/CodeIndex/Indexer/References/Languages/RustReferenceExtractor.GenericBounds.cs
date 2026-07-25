using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class RustReferenceExtractor
{
    private static void EmitGenericBoundReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var hasGenericMarker = preparedLine.IndexOf('<') >= 0;
        var hasWhereMarker = preparedLine.IndexOf("where", StringComparison.Ordinal) >= 0;
        if (!hasGenericMarker && !hasWhereMarker)
            return;

        var genericOpenIndex = hasGenericMarker
            ? TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '<')
            : -1;
        if (genericOpenIndex >= 0)
        {
            var constGenericNames = EmitConstGenericParameterReferences(
                preparedLine,
                genericOpenIndex,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            EmitConstGenericUsageReferences(
                preparedLine,
                genericOpenIndex,
                constGenericNames,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            TypedLanguageReferenceExtractor.EmitGenericColonBoundReferences(
                preparedLine,
                genericOpenIndex,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            EmitGenericDefaultTypeReferences(
                preparedLine,
                genericOpenIndex,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            EmitGenericFunctionTraitReturnTypeReferences(
                preparedLine,
                genericOpenIndex,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }

        if (!hasWhereMarker)
            return;

        TypedLanguageReferenceExtractor.EmitWhereClauseTypeReferences(
            preparedLine,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        EmitWhereClauseConstGenericReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        EmitWhereClauseFunctionTraitReturnTypeReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static HashSet<string> EmitConstGenericParameterReferences(
        string preparedLine,
        int genericOpenIndex,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var constGenericNames = new HashSet<string>(StringComparer.Ordinal);
        var genericCloseIndex = FindRustGenericClose(preparedLine, genericOpenIndex);
        if (genericCloseIndex <= genericOpenIndex)
            return constGenericNames;

        var clause = preparedLine.Substring(genericOpenIndex + 1, genericCloseIndex - genericOpenIndex - 1);
        EmitConstGenericSegments(
            clause,
            genericOpenIndex + 1,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            constGenericNames);
        return constGenericNames;
    }

    private static void EmitConstGenericUsageReferences(
        string preparedLine,
        int genericOpenIndex,
        HashSet<string> constGenericNames,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (constGenericNames.Count == 0)
            return;

        var genericCloseIndex = FindRustGenericClose(preparedLine, genericOpenIndex);
        if (genericCloseIndex <= genericOpenIndex)
            return;

        for (var index = genericCloseIndex + 1; index < preparedLine.Length; index++)
        {
            if (!IsRustIdentifierStart(preparedLine[index]))
                continue;

            var end = index + 1;
            while (end < preparedLine.Length && IsRustIdentifierPart(preparedLine[end]))
                end++;

            var name = preparedLine.Substring(index, end - index);
            if (constGenericNames.Contains(name))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    name,
                    index,
                    "const_generic_reference",
                    context,
                    lineNumber,
                    resolveContainerForColumn(index));
            }

            index = end - 1;
        }
    }

    private static void EmitWhereClauseConstGenericReferences(
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
            var clauseEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, clauseStart, stopAtComma: false, stopAtArrow: false);
            if (clauseEnd <= clauseStart)
                clauseEnd = preparedLine.Length;

            EmitConstGenericSegments(
                preparedLine.Substring(clauseStart, clauseEnd - clauseStart),
                clauseStart,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
    }

    private static void EmitConstGenericSegments(
        string clause,
        int clauseStart,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        HashSet<string>? constGenericNames = null)
    {
        if (clause.IndexOf("const", StringComparison.Ordinal) < 0
            || clause.IndexOf(':') < 0)
        {
            return;
        }

        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
        {
            var fragment = clause.Substring(segmentStart, segmentLength);
            var match = ConstGenericParameterRegex.Match(fragment);
            if (!match.Success)
                continue;

            var nameGroup = match.Groups["name"];
            var name = NormalizeIdentifier(nameGroup.Value);
            constGenericNames?.Add(name);
            var absoluteNameStart = clauseStart + segmentStart + nameGroup.Index;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                absoluteNameStart,
                "const_generic_reference",
                context,
                lineNumber,
                resolveContainerForColumn(absoluteNameStart));

            var typeGroup = match.Groups["type"];
            var typeMatch = ConstGenericTypeHeadRegex.Match(typeGroup.Value);
            if (!typeMatch.Success)
                continue;

            var typeNameGroup = typeMatch.Groups["name"];
            var absoluteTypeStart = clauseStart + segmentStart + typeGroup.Index + typeNameGroup.Index;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                NormalizeIdentifier(typeNameGroup.Value),
                absoluteTypeStart,
                "annotation",
                context,
                lineNumber,
                resolveContainerForColumn(absoluteTypeStart));
        }
    }

    private static void EmitGenericFunctionTraitReturnTypeReferences(
        string preparedLine,
        int genericOpenIndex,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("->", StringComparison.Ordinal) < 0)
            return;

        var genericCloseIndex = FindRustGenericClose(preparedLine, genericOpenIndex);
        if (genericCloseIndex <= genericOpenIndex)
            return;

        var clause = preparedLine.Substring(genericOpenIndex + 1, genericCloseIndex - genericOpenIndex - 1);
        EmitFunctionTraitReturnTypesFromBoundClause(
            clause,
            genericOpenIndex + 1,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitWhereClauseFunctionTraitReturnTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("->", StringComparison.Ordinal) < 0)
            return;

        foreach (var whereIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "where"))
        {
            var clauseStart = whereIndex + "where".Length;
            var clauseEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, clauseStart, stopAtComma: false, stopAtArrow: false);
            if (clauseEnd <= clauseStart)
                clauseEnd = preparedLine.Length;

            EmitFunctionTraitReturnTypesFromBoundClause(
                preparedLine.Substring(clauseStart, clauseEnd - clauseStart),
                clauseStart,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
    }

    private static void EmitFunctionTraitReturnTypesFromBoundClause(
        string clause,
        int clauseStart,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (clause.IndexOf("->", StringComparison.Ordinal) < 0
            || clause.IndexOf(':') < 0)
        {
            return;
        }

        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
        {
            var fragment = clause.Substring(segmentStart, segmentLength);
            var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(fragment, ':');
            if (colonIndex < 0)
                continue;

            var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(fragment, "->", colonIndex + 1);
            if (arrowIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, arrowIndex + 2);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart, stopAtArrow: false);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = clauseStart + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart));
        }
    }

    private static void EmitFunctionTraitReturnTypeFromExpression(
        string expression,
        int expressionStart,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (expression.IndexOf("->", StringComparison.Ordinal) < 0)
            return;

        var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(expression, "->");
        if (arrowIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(expression, arrowIndex + 2);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(expression, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        var absoluteStart = expressionStart + typeStart;
        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            expression.Substring(typeStart, typeEnd - typeStart),
            absoluteStart,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(absoluteStart));
    }

    private static void EmitGenericDefaultTypeReferences(
        string preparedLine,
        int genericOpenIndex,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf('=') < 0)
            return;

        var genericCloseIndex = FindRustGenericClose(preparedLine, genericOpenIndex);
        if (genericCloseIndex <= genericOpenIndex)
            return;

        var clause = preparedLine.Substring(genericOpenIndex + 1, genericCloseIndex - genericOpenIndex - 1);
        if (clause.IndexOf('=') < 0)
            return;

        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
        {
            var fragment = clause.Substring(segmentStart, segmentLength);
            var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(fragment, '=');
            if (assignmentIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, assignmentIndex + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = genericOpenIndex + 1 + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart));
        }
    }

    private static int FindRustGenericClose(string text, int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < text.Length; index++)
        {
            if (text[index] == '-' && index + 1 < text.Length && text[index + 1] == '>')
            {
                index++;
                continue;
            }

            if (text[index] == '<')
            {
                depth++;
                continue;
            }

            if (text[index] != '>')
                continue;

            depth--;
            if (depth == 0)
                return index;
        }

        return -1;
    }

    public static string NormalizeIdentifier(string identifier)
    {
        if (identifier.Length == 0)
            return identifier;

        if (!identifier.Contains("r#", StringComparison.Ordinal))
            return identifier;

        if (!identifier.Contains("::", StringComparison.Ordinal))
            return identifier.StartsWith("r#", StringComparison.Ordinal)
                ? identifier[2..]
                : identifier;

        var builder = new StringBuilder(identifier.Length);
        var segmentStart = 0;
        while (segmentStart <= identifier.Length)
        {
            var separator = identifier.IndexOf("::", segmentStart, StringComparison.Ordinal);
            var segmentEnd = separator >= 0 ? separator : identifier.Length;
            AppendNormalizedRustIdentifierSegment(builder, identifier, segmentStart, segmentEnd - segmentStart);
            if (separator < 0)
                break;

            builder.Append("::");
            segmentStart = separator + 2;
        }

        return builder.ToString();
    }

    private static void AppendNormalizedRustIdentifierSegment(StringBuilder builder, string identifier, int start, int length)
    {
        if (length >= 2
            && identifier[start] == 'r'
            && identifier[start + 1] == '#')
        {
            start += 2;
            length -= 2;
        }

        builder.Append(identifier, start, length);
    }

    public static bool IsFunctionDeclarationCallSite(string line, int callIndex)
    {
        if (callIndex <= 0)
            return false;

        var prefix = line.AsSpan(0, callIndex).TrimEnd();
        return prefix.EndsWith("fn", StringComparison.Ordinal);
    }

    public static bool IsDeriveAttributeCallSite(string line, string name, int callIndex)
    {
        if (!string.Equals(name, "derive", StringComparison.Ordinal) || callIndex <= 0)
            return false;

        var index = callIndex - 1;
        while (index >= 0 && char.IsWhiteSpace(line[index]))
            index--;

        if (index < 0 || line[index] != '[')
            return false;

        index--;
        while (index >= 0 && char.IsWhiteSpace(line[index]))
            index--;

        if (index >= 0 && line[index] == '!')
        {
            index--;
            while (index >= 0 && char.IsWhiteSpace(line[index]))
                index--;
        }

        return index >= 0 && line[index] == '#';
    }

    public static bool IsLikelyInstantiationCallName(string originalName, string normalizedName, string line, int callIndex)
    {
        var normalizedLeaf = LastPathSegment(normalizedName);
        var originalLeaf = LastPathSegment(originalName);
        if (!IsLikelyRustTypePathLeaf(originalLeaf) && !IsLikelyRustTypePathLeaf(normalizedLeaf))
            return false;

        var afterName = callIndex + originalName.Length;
        while (afterName < line.Length && char.IsWhiteSpace(line[afterName]))
            afterName++;

        if (afterName >= line.Length)
            return false;

        if (line[afterName] == '!')
            return false;

        return line[afterName] is '(' or '<'
               || (afterName + 1 < line.Length && line[afterName] == ':' && line[afterName + 1] == ':');
    }

    private static string LastPathSegment(string name)
    {
        var leafStart = name.LastIndexOf("::", StringComparison.Ordinal);
        return leafStart >= 0 ? name[(leafStart + 2)..] : name;
    }

    public static bool IsRawIdentifierPrefix(string line, int callIndex) =>
        callIndex >= 2
        && line[callIndex - 2] == 'r'
        && line[callIndex - 1] == '#';
}
