using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class TypeScriptReferenceExtractor
{
    private static void EmitMappedTypeMemberReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var bracketStart = preparedLine.IndexOf('[');
        if (bracketStart < 0)
            return;

        var bracketEnd = ReferenceExtractor.FindMatchingChar(preparedLine, bracketStart, '[', ']');
        if (bracketEnd <= bracketStart)
            return;

        var clause = preparedLine.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
        if (!clause.Contains("keyof", StringComparison.Ordinal)
            && !clause.Contains(" in ", StringComparison.Ordinal)
            && !clause.Contains(" as ", StringComparison.Ordinal))
        {
            return;
        }

        var clauseStart = bracketStart + 1;
        ReferenceExtractor.AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            clause,
            clauseStart,
            context,
            lineNumber,
            resolveContainerForColumn(clauseStart),
            "typescript",
            MappedTypeClauseIgnoredSegments);

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', bracketEnd + 1);
        if (colonIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        ReferenceExtractor.AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart),
            "typescript");
    }

    public static bool IsSatisfiesTypeOperand(string preparedLine, int tokenIndex)
    {
        foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "satisfies"))
        {
            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, keywordIndex + "satisfies".Length);
            if (typeStart >= preparedLine.Length || tokenIndex < typeStart)
                continue;

            var typeEnd = TypedLanguageReferenceExtractor.FindKeywordFollowingTypeExpressionEnd(preparedLine, typeStart, "typescript");
            if (typeEnd <= typeStart)
                continue;

            if (tokenIndex < typeEnd)
                return true;
        }

        return false;
    }

    private static void EmitGenericConstraintTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf('<') < 0
            || preparedLine.IndexOf("extends", StringComparison.Ordinal) < 0)
        {
            return;
        }

        for (var index = 0; index < preparedLine.Length; index++)
        {
            if (preparedLine[index] != '<')
                continue;

            var closeIndex = ReferenceExtractor.FindMatchingChar(preparedLine, index, '<', '>');
            if (closeIndex <= index)
                continue;

            var clauseStart = index + 1;
            var clause = preparedLine.AsSpan(clauseStart, closeIndex - clauseStart);
            foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
            {
                var fragment = clause.Slice(segmentStart, segmentLength).ToString();
                foreach (var extendsIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(fragment, "extends"))
                {
                    var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, extendsIndex + "extends".Length);
                    if (typeStart >= fragment.Length)
                        continue;

                    var typeEnd = FindGenericConstraintExpressionEnd(fragment, typeStart);
                    if (typeEnd <= typeStart)
                        continue;

                    var absoluteStart = clauseStart + segmentStart + typeStart;
                    ReferenceExtractor.AddTypeScriptTypeExpressionSegments(
                        references,
                        seen,
                        fileId,
                        fragment.Substring(typeStart, typeEnd - typeStart),
                        absoluteStart,
                        context,
                        lineNumber,
                        resolveContainerForColumn(absoluteStart));
                }
            }

            index = closeIndex;
        }
    }

    private static int FindGenericConstraintExpressionEnd(string fragment, int typeStart)
    {
        var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(fragment, '=', typeStart);
        return equalsIndex >= 0 ? equalsIndex : fragment.Length;
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
              || trimmed.StartsWith("abstract class ", StringComparison.Ordinal)
              || trimmed.StartsWith("export class ", StringComparison.Ordinal)
              || trimmed.StartsWith("export abstract class ", StringComparison.Ordinal)
              || trimmed.StartsWith("interface ", StringComparison.Ordinal)
              || trimmed.StartsWith("export interface ", StringComparison.Ordinal)))
        {
            return;
        }

        EmitHeritageKeyword(preparedLine, "extends", references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitHeritageKeyword(preparedLine, "implements", references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitTypeAliasTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (!TryFindTypeAliasShape(preparedLine, out var nameEnd, out var assignmentIndex))
            return;

        var genericOpen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '<', nameEnd);
        if (genericOpen >= 0 && genericOpen < assignmentIndex)
        {
            var genericClose = ReferenceExtractor.FindMatchingChar(preparedLine, genericOpen, '<', '>');
            if (genericClose > genericOpen && genericClose < assignmentIndex)
            {
                EmitTypeParameterDefaultReferences(
                    preparedLine,
                    genericOpen + 1,
                    genericClose,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn);
            }
        }

        var rhsStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, assignmentIndex + 1);
        if (rhsStart >= preparedLine.Length)
            return;

        var rhsEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(
            preparedLine,
            rhsStart,
            stopAtComma: false,
            stopAtArrow: false);
        if (rhsEnd <= rhsStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(rhsStart, rhsEnd - rhsStart),
            rhsStart,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(rhsStart));
    }

    private static bool TryFindTypeAliasShape(string line, out int nameEnd, out int assignmentIndex)
    {
        nameEnd = -1;
        assignmentIndex = -1;

        var index = 0;
        SkipWhitespace(line, ref index);
        TryConsumeKeyword(line, "export", ref index);
        SkipWhitespace(line, ref index);
        TryConsumeKeyword(line, "declare", ref index);
        SkipWhitespace(line, ref index);
        if (!TryConsumeKeyword(line, "type", ref index))
            return false;

        SkipWhitespace(line, ref index);
        if (index >= line.Length || !IsTypeScriptIdentifierStart(line[index]))
            return false;

        index++;
        while (index < line.Length && IsTypeScriptIdentifierPart(line[index]))
            index++;

        nameEnd = index;
        assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(line, '=', nameEnd);
        return assignmentIndex > nameEnd;
    }

    private static void EmitTypeParameterDefaultReferences(
        string line,
        int listStart,
        int listEnd,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var parameterList = line.AsSpan(listStart, listEnd - listStart);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(parameterList))
        {
            var fragment = parameterList.Slice(segmentStart, segmentLength).ToString();
            var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(fragment, '=');
            if (equalsIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, equalsIndex + 1);
            if (typeStart >= fragment.Length)
                continue;

            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart, stopAtArrow: false);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = listStart + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "typescript",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart));
        }
    }

    private static void EmitHeritageKeyword(
        string preparedLine,
        string keyword,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, keyword))
        {
            var listStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, keywordIndex + keyword.Length);
            var listEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, listStart, stopAtComma: false);
            if (listEnd <= listStart)
                continue;

            TypedLanguageReferenceExtractor.EmitCommaSeparatedTypeListReferences(
                preparedLine,
                listStart,
                listEnd,
                "typescript",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
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
        if (preparedLine.IndexOf('(') < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(
            preparedLine,
            '(',
            SkipLeadingDecorators(preparedLine));
        if (openParen <= 0)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        var returnColon = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, closeParen + 1);
        if (returnColon >= preparedLine.Length || preparedLine[returnColon] != ':')
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, returnColon + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static void EmitDecoratedMemberTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf('@') < 0 || preparedLine.IndexOf(':') < 0)
            return;

        var memberStart = SkipLeadingDecorators(preparedLine);
        if (memberStart <= 0 || memberStart >= preparedLine.Length)
            return;

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', memberStart);
        if (colonIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', memberStart);
        if (openParen >= 0 && openParen < colonIndex)
            return;

        var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', memberStart);
        if (equalsIndex >= 0 && equalsIndex < colonIndex)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static void EmitFunctionPropertyTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf(':') < 0)
            return;

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':');
        if (colonIndex < 0)
            return;

        var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=');
        if (equalsIndex >= 0 && equalsIndex < colonIndex)
            return;

        var questionIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '?');
        if (questionIndex >= 0 && questionIndex != colonIndex - 1)
            return;

        var prefix = preparedLine.Substring(0, colonIndex).TrimEnd();
        if (prefix.Length == 0 || prefix.EndsWith(")", StringComparison.Ordinal))
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        var container = resolveContainerForColumn(typeStart);
        TypedLanguageReferenceExtractor.TryEmitTypeScriptFunctionTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);
    }

}
