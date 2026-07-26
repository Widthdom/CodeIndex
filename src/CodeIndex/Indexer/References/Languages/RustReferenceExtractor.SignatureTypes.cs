using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class RustReferenceExtractor
{
    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container,
        SymbolRecord? enumContainer)
    {
        EmitLifetimeReferences(context, references, seen, fileId, context, lineNumber, container);
        EmitHigherRankedTraitBoundReferences(context, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitUseReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
        EmitExternCrateReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
        EmitModuleDeclarationReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
        EmitFunctionSignatureTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitClosureSignatureTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitLetTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitConstStaticTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitTypeAliasTargetReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitTraitAliasTargetReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitAssociatedTypeBoundReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitTupleStructFieldTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, container);
        EmitStructFieldTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
        EmitEnumVariantTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, enumContainer);
        EmitAsCastTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitQualifiedAssociatedCallReceiverTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitAssociatedCallReceiverTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitAssociatedValueReceiverTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitStructLiteralInstantiationReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, enumContainer);
        EmitImplAndTraitTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitMutableReferenceTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGenericBoundReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitMutableReferenceTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf('&') < 0
            || preparedLine.IndexOf("mut", StringComparison.Ordinal) < 0)
        {
            return;
        }

        foreach (Match match in Regex.EnumerateMatches(
                     MutableReferenceTypeRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            if (!IsMutableReferenceTypeContext(preparedLine, match.Index))
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, match.Index + match.Length);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static bool IsMutableReferenceTypeContext(string preparedLine, int ampersandIndex)
    {
        var cursor = ampersandIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(preparedLine[cursor]))
            cursor--;

        if (cursor < 0)
            return false;
        if (preparedLine[cursor] == ':')
            return true;

        return preparedLine[cursor] == '>'
            && cursor > 0
            && preparedLine[cursor - 1] == '-';
    }

    private static void EmitLifetimeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf('\'') < 0)
        {
            return;
        }

        for (var index = 0; index + 1 < preparedLine.Length; index++)
        {
            if (preparedLine[index] != '\'' || !IsRustLifetimeStart(preparedLine[index + 1]))
                continue;

            var end = index + 2;
            while (end < preparedLine.Length && IsRustLifetimePart(preparedLine[end]))
                end++;

            if (end < preparedLine.Length && preparedLine[end] == '\'')
            {
                index = end;
                continue;
            }

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                preparedLine.Substring(index, end - index),
                index,
                "lifetime_reference",
                context,
                lineNumber,
                container);
            index = end - 1;
        }
    }

    private static void EmitHigherRankedTraitBoundReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (line.IndexOf("for", StringComparison.Ordinal) < 0
            || line.IndexOf('<') < 0)
        {
            return;
        }

        foreach (var forIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(line, "for"))
        {
            var openAngle = SkipWhitespace(line, forIndex + "for".Length);
            if (openAngle >= line.Length || line[openAngle] != '<')
                continue;

            var closeAngle = FindRustGenericClose(line, openAngle);
            if (closeAngle <= openAngle)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(line, closeAngle + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(line, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                line.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitUseReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("use", StringComparison.Ordinal) < 0)
        {
            return;
        }

        var match = UseStatementRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var bodyGroup = match.Groups["body"];
        EmitUseBodyReferences(bodyGroup.Value, bodyGroup.Index, references, seen, fileId, context, lineNumber, container, prefix: null);
    }

    private static void EmitUseBodyReferences(
        string body,
        int bodyStart,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string? prefix)
    {
        var text = body.Trim();
        if (text.Length == 0)
            return;

        var textStart = bodyStart + body.IndexOf(text, StringComparison.Ordinal);
        var openBrace = text.IndexOf('{');
        if (openBrace >= 0)
        {
            var closeBrace = text.LastIndexOf('}');
            if (closeBrace > openBrace)
            {
                var groupedPrefix = CombineUsePath(prefix, text[..openBrace].Trim());
                var inner = text.AsSpan(openBrace + 1, closeBrace - openBrace - 1);
                foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(inner))
                {
                    EmitUseBodyReferences(
                        inner.Slice(segmentStart, segmentLength).ToString(),
                        textStart + openBrace + 1 + segmentStart,
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        container,
                        groupedPrefix);
                }

                return;
            }
        }

        var aliasIndex = FindTopLevelUseAliasIndex(text);
        var target = aliasIndex >= 0 ? text[..aliasIndex].Trim() : text;
        if (target.Length == 0
            || target is "crate" or "super"
            || target == "*" && string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        if (target == "self" && !string.IsNullOrWhiteSpace(prefix))
            target = prefix;
        else if (target == "self")
            return;
        else if (!string.IsNullOrWhiteSpace(prefix))
            target = CombineUsePath(prefix, target);

        var leafStart = target.LastIndexOf("::", StringComparison.Ordinal);
        var leaf = leafStart >= 0 ? target[(leafStart + 2)..].Trim() : target.Trim();
        if (leaf == "*")
        {
            if (leafStart < 0)
                return;

            var globParent = target[..leafStart].Trim();
            var globParentLeafStart = globParent.LastIndexOf("::", StringComparison.Ordinal);
            leaf = globParentLeafStart >= 0 ? globParent[(globParentLeafStart + 2)..].Trim() : globParent;
        }

        if (leaf.Length == 0 || leaf is "crate" or "self" or "super" or "*")
            return;

        var leafIndex = text.IndexOf(leaf, StringComparison.Ordinal);
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            NormalizeIdentifier(leaf),
            textStart + Math.Max(0, leafIndex),
            "reference",
            context,
            lineNumber,
            container);
    }

    private static int FindTopLevelUseAliasIndex(string text)
    {
        foreach (var asIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(text, "as"))
            return asIndex;

        return -1;
    }

    private static string CombineUsePath(string? prefix, string name)
    {
        var cleanedPrefix = TrimRustUsePathSegment(prefix);
        var cleanedName = TrimRustUsePathSegment(name);
        if (cleanedPrefix.Length == 0)
            return cleanedName;
        if (cleanedName.Length == 0)
            return cleanedPrefix;
        return $"{cleanedPrefix}::{cleanedName}";
    }

    private static string TrimRustUsePathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var span = value.AsSpan().Trim();
        while (span.Length > 0 && span[^1] == ':')
            span = span[..^1];

        return span.ToString();
    }

    private static void EmitExternCrateReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("extern", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf("crate", StringComparison.Ordinal) < 0)
        {
            return;
        }

        var match = ExternCrateRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            NormalizeIdentifier(nameGroup.Value),
            nameGroup.Index,
            "reference",
            context,
            lineNumber,
            container);
    }

    private static void EmitModuleDeclarationReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("mod", StringComparison.Ordinal) < 0)
        {
            return;
        }

        var match = ModuleDeclarationRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            NormalizeIdentifier(nameGroup.Value),
            nameGroup.Index,
            "reference",
            context,
            lineNumber,
            container);
    }

    private static void EmitFunctionSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("fn", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        var fnIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "fn");
        if (fnIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', fnIndex + 2);
        if (openParen <= fnIndex)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        if (preparedLine.IndexOf("->", StringComparison.Ordinal) < 0)
            return;

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
            "rust",
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
        if (preparedLine.IndexOf('|') < 0
            || (preparedLine.IndexOf(':') < 0 && preparedLine.IndexOf("->", StringComparison.Ordinal) < 0))
        {
            return;
        }

        var searchIndex = 0;
        while (searchIndex < preparedLine.Length)
        {
            var openPipe = preparedLine.IndexOf('|', searchIndex);
            if (openPipe < 0)
                return;

            var closePipe = preparedLine.IndexOf('|', openPipe + 1);
            if (closePipe < 0)
                return;

            searchIndex = closePipe + 1;
            var parameterList = preparedLine.Substring(openPipe + 1, closePipe - openPipe - 1);
            var hasParameterTypes = TypedLanguageReferenceExtractor.FindTopLevelChar(parameterList, ':') >= 0;
            var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(preparedLine, "->", closePipe + 1);
            var hasImmediateReturnType = arrowIndex >= 0 && HasOnlyWhitespace(preparedLine, closePipe + 1, arrowIndex);
            if (!hasParameterTypes && !hasImmediateReturnType)
                continue;

            if (hasParameterTypes)
            {
                TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
                    preparedLine,
                    openPipe + 1,
                    closePipe,
                    "rust",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn);
            }

            if (!hasImmediateReturnType)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, arrowIndex + 2);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static bool HasOnlyWhitespace(string text, int startIndex, int endIndex)
    {
        for (var index = Math.Max(0, startIndex); index < endIndex && index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
                return false;
        }

        return true;
    }

    private static void EmitLetTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("let", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf(':') < 0)
        {
            return;
        }

        foreach (var letIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "let"))
        {
            var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', letIndex + "let".Length);
            if (colonIndex < 0)
                continue;

            var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', letIndex + "let".Length);
            if (assignmentIndex >= 0 && assignmentIndex < colonIndex)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitConstStaticTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf(':') < 0
            || (preparedLine.IndexOf("const", StringComparison.Ordinal) < 0
                && preparedLine.IndexOf("static", StringComparison.Ordinal) < 0))
        {
            return;
        }

        foreach (var keyword in ConstStaticKeywords)
        {
            foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, keyword))
            {
                var declarationStart = keywordIndex + keyword.Length;
                if (keyword == "static")
                    declarationStart = SkipOptionalRustMut(preparedLine, declarationStart);

                var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', declarationStart);
                if (colonIndex < 0)
                    continue;

                var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', declarationStart);
                if (assignmentIndex >= 0 && assignmentIndex < colonIndex)
                    continue;

                var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
                var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
                if (typeEnd <= typeStart)
                    continue;

                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    preparedLine.Substring(typeStart, typeEnd - typeStart),
                    typeStart,
                    "rust",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(typeStart));
            }
        }
    }

    private static int SkipOptionalRustMut(string line, int startIndex)
    {
        var index = startIndex;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        if (index + "mut".Length > line.Length
            || string.CompareOrdinal(line, index, "mut", 0, "mut".Length) != 0)
        {
            return startIndex;
        }

        var afterMut = index + "mut".Length;
        if (afterMut < line.Length && IsRustIdentifierPart(line[afterMut]))
            return startIndex;

        return afterMut;
    }

    private static void EmitTypeAliasTargetReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("type", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('=') < 0)
        {
            return;
        }

        foreach (var typeIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "type"))
        {
            var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', typeIndex + "type".Length);
            if (assignmentIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, assignmentIndex + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitTraitAliasTargetReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("trait", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('=') < 0)
        {
            return;
        }

        foreach (var traitIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "trait"))
        {
            var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', traitIndex + "trait".Length);
            if (assignmentIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, assignmentIndex + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitAssociatedTypeBoundReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("type", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf(':') < 0)
        {
            return;
        }

        foreach (var typeIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "type"))
        {
            var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', typeIndex + "type".Length);
            if (colonIndex < 0)
                continue;

            var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', typeIndex + "type".Length);
            if (assignmentIndex >= 0 && assignmentIndex < colonIndex)
                continue;

            var boundsStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
            var boundsEnd = assignmentIndex > colonIndex
                ? assignmentIndex
                : TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, boundsStart);
            if (boundsEnd <= boundsStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(boundsStart, boundsEnd - boundsStart),
                boundsStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(boundsStart));
        }
    }

    private static void EmitTupleStructFieldTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("struct", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        var structIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "struct");
        if (structIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', structIndex + "struct".Length);
        if (openParen < 0)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen <= openParen)
            return;

        var fieldList = preparedLine.AsSpan(openParen + 1, closeParen - openParen - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(fieldList))
        {
            var fragment = fieldList.Slice(segmentStart, segmentLength).ToString();
            var typeStart = SkipRustTupleFieldPrefix(fragment);
            if (typeStart >= fragment.Length)
                continue;

            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = openParen + 1 + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container ?? resolveContainerForColumn(absoluteStart));
        }
    }

    private static void EmitStructFieldTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (container?.Kind is not "class" and not "struct")
            return;

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':');
        if (colonIndex < 0)
            return;

        var trimmed = preparedLine.TrimStart();
        if (trimmed.StartsWith("fn ", StringComparison.Ordinal)
            || trimmed.StartsWith("let ", StringComparison.Ordinal)
            || trimmed.StartsWith("type ", StringComparison.Ordinal)
            || trimmed.StartsWith("impl ", StringComparison.Ordinal))
        {
            return;
        }

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);
    }

    private static int SkipRustTupleFieldPrefix(string fragment)
    {
        var index = 0;
        while (index < fragment.Length && char.IsWhiteSpace(fragment[index]))
            index++;

        if (index + 3 > fragment.Length
            || string.CompareOrdinal(fragment, index, "pub", 0, "pub".Length) != 0)
        {
            return index;
        }

        var afterPub = index + "pub".Length;
        if (afterPub < fragment.Length && IsRustIdentifierPart(fragment[afterPub]))
            return index;

        index = afterPub;
        while (index < fragment.Length && char.IsWhiteSpace(fragment[index]))
            index++;

        if (index < fragment.Length && fragment[index] == '(')
        {
            var closeParen = ReferenceExtractor.FindMatchingChar(fragment, index, '(', ')');
            if (closeParen < 0)
                return fragment.Length;

            index = closeParen + 1;
            while (index < fragment.Length && char.IsWhiteSpace(fragment[index]))
                index++;
        }

        return index;
    }

    private static bool IsRustIdentifierPart(char ch) =>
        ch == '_' || ch == '$' || char.IsLetterOrDigit(ch);

    private static bool IsRustIdentifierStart(char ch) =>
        ch == '_' || char.IsLetter(ch);

    private static bool IsRustLifetimeStart(char ch) =>
        ch == '_' || char.IsLetter(ch);

    private static bool IsRustLifetimePart(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

}
