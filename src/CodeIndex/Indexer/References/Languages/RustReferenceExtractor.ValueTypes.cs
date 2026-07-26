using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class RustReferenceExtractor
{
    private static void EmitEnumVariantTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? enumContainer)
    {
        if (enumContainer?.Kind != "enum")
            return;
        if (preparedLine.IndexOf('(') < 0
            && preparedLine.IndexOf('{') < 0)
        {
            return;
        }

        var variantStart = FirstNonWhitespaceIndex(preparedLine);
        if (variantStart >= preparedLine.Length
            || preparedLine[variantStart] is '}' or '#'
            || !IsLikelyRustEnumVariantStart(preparedLine, variantStart))
        {
            return;
        }

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', variantStart);
        var openBrace = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '{', variantStart);
        if (openParen >= 0 && (openBrace < 0 || openParen < openBrace))
        {
            EmitEnumTupleVariantTypeReferences(preparedLine, openParen, references, seen, fileId, context, lineNumber, enumContainer);
        }

        if (openBrace >= 0)
            EmitEnumStructVariantTypeReferences(preparedLine, openBrace, references, seen, fileId, context, lineNumber, enumContainer);
    }

    private static void EmitEnumTupleVariantTypeReferences(
        string preparedLine,
        int openParen,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord enumContainer)
    {
        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen <= openParen)
            return;

        var fieldList = preparedLine.AsSpan(openParen + 1, closeParen - openParen - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(fieldList))
        {
            var fragment = fieldList.Slice(segmentStart, segmentLength).ToString();
            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, 0);
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
                enumContainer);
        }
    }

    private static void EmitEnumStructVariantTypeReferences(
        string preparedLine,
        int openBrace,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord enumContainer)
    {
        var closeBrace = ReferenceExtractor.FindMatchingChar(preparedLine, openBrace, '{', '}');
        if (closeBrace <= openBrace)
            return;

        var fieldList = preparedLine.Substring(openBrace + 1, closeBrace - openBrace - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(fieldList))
        {
            var fragment = fieldList.Substring(segmentStart, segmentLength);
            var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(fragment, ':');
            if (colonIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, colonIndex + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = openBrace + 1 + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                enumContainer);
        }
    }

    private static int FirstNonWhitespaceIndex(string text)
    {
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    private static bool IsLikelyRustEnumVariantStart(string line, int startIndex)
    {
        if (startIndex < line.Length && char.IsUpper(line[startIndex]))
            return true;

        return startIndex + 2 < line.Length
            && line[startIndex] == 'r'
            && line[startIndex + 1] == '#'
            && IsRustIdentifierPart(line[startIndex + 2]);
    }

    private static void EmitAsCastTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("as", StringComparison.Ordinal) < 0)
            return;

        var trimmed = preparedLine.TrimStart();
        if (trimmed.StartsWith("use ", StringComparison.Ordinal)
            || trimmed.StartsWith("pub use ", StringComparison.Ordinal)
            || trimmed.StartsWith("extern crate ", StringComparison.Ordinal)
            || trimmed.StartsWith("pub extern crate ", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var asIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "as"))
        {
            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, asIndex + "as".Length);
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

    private static void EmitAssociatedCallReceiverTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("::", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     AssociatedCallReceiverRegex,
                     preparedLine,
                     references))
        {
            var receiverGroup = match.Groups["receiver"];
            var receiver = receiverGroup.Value;
            var leafStart = receiver.LastIndexOf("::", StringComparison.Ordinal);
            var leaf = leafStart >= 0 ? receiver[(leafStart + 2)..] : receiver;
            var leafOffset = leafStart >= 0 ? leafStart + 2 : 0;
            var normalizedLeaf = NormalizeIdentifier(leaf);
            if (normalizedLeaf == "Self" || !IsLikelyRustTypePathLeaf(leaf))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                normalizedLeaf,
                receiverGroup.Index + leafOffset,
                "type_reference",
                context,
                lineNumber,
                resolveContainerForColumn(receiverGroup.Index));

            var argsGroup = match.Groups["args"];
            if (!argsGroup.Success)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                argsGroup.Value,
                argsGroup.Index,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(argsGroup.Index));
        }
    }

    private static void EmitAssociatedValueReceiverTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("::", StringComparison.Ordinal) < 0)
        {
            return;
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     AssociatedValueReceiverRegex,
                     preparedLine,
                     references))
        {
            var receiverGroup = match.Groups["receiver"];
            var receiver = receiverGroup.Value;
            var leafStart = receiver.LastIndexOf("::", StringComparison.Ordinal);
            var leaf = leafStart >= 0 ? receiver[(leafStart + 2)..] : receiver;
            var leafOffset = leafStart >= 0 ? leafStart + 2 : 0;
            var normalizedLeaf = NormalizeIdentifier(leaf);
            if (normalizedLeaf == "Self" || !IsLikelyRustTypePathLeaf(leaf))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                normalizedLeaf,
                receiverGroup.Index + leafOffset,
                "type_reference",
                context,
                lineNumber,
                resolveContainerForColumn(receiverGroup.Index));

            var argsGroup = match.Groups["args"];
            if (!argsGroup.Success)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                argsGroup.Value,
                argsGroup.Index,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(argsGroup.Index));
        }
    }

    private static void EmitQualifiedAssociatedCallReceiverTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf('<') < 0
            || preparedLine.IndexOf("::", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        var searchIndex = 0;
        while (searchIndex < preparedLine.Length)
        {
            var openAngle = preparedLine.IndexOf('<', searchIndex);
            if (openAngle < 0)
                return;

            searchIndex = openAngle + 1;
            var closeAngle = ReferenceExtractor.FindMatchingChar(preparedLine, openAngle, '<', '>');
            if (closeAngle <= openAngle)
                continue;

            var afterClose = SkipWhitespace(preparedLine, closeAngle + 1);
            if (afterClose + 2 > preparedLine.Length
                || preparedLine[afterClose] != ':'
                || preparedLine[afterClose + 1] != ':')
            {
                continue;
            }

            var methodStart = SkipWhitespace(preparedLine, afterClose + 2);
            if (methodStart >= preparedLine.Length || !IsRustIdentifierStart(preparedLine[methodStart]))
                continue;

            var methodEnd = methodStart + 1;
            while (methodEnd < preparedLine.Length && IsRustIdentifierPart(preparedLine[methodEnd]))
                methodEnd++;

            var callOpen = SkipWhitespace(preparedLine, methodEnd);
            if (callOpen >= preparedLine.Length || preparedLine[callOpen] != '(')
                continue;

            var qualified = preparedLine.Substring(openAngle + 1, closeAngle - openAngle - 1);
            foreach (var asIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(qualified, "as"))
            {
                EmitQualifiedAssociatedCallTypePart(
                    qualified,
                    openAngle + 1,
                    0,
                    asIndex,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn);
                EmitQualifiedAssociatedCallTypePart(
                    qualified,
                    openAngle + 1,
                    asIndex + "as".Length,
                    qualified.Length,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn);
                break;
            }
        }
    }

    private static void EmitQualifiedAssociatedCallTypePart(
        string qualified,
        int qualifiedStart,
        int partStart,
        int partEnd,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(qualified, partStart);
        while (partEnd > typeStart && char.IsWhiteSpace(qualified[partEnd - 1]))
            partEnd--;
        if (partEnd <= typeStart)
            return;

        var absoluteStart = qualifiedStart + typeStart;
        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            qualified.Substring(typeStart, partEnd - typeStart),
            absoluteStart,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(absoluteStart));
    }

    private static bool IsLikelyRustTypePathLeaf(string leaf)
    {
        if (leaf.StartsWith("r#", StringComparison.Ordinal))
            return leaf.Length > 2 && IsRustIdentifierPart(leaf[2]);

        return leaf.Length > 0 && char.IsUpper(leaf[0]);
    }

    private static void EmitStructLiteralInstantiationReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? enumContainer)
    {
        if (enumContainer != null
            || preparedLine.IndexOf('{') < 0
            || IsRustTypeDeclarationLine(preparedLine))
        {
            return;
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     StructLiteralRegex,
                     preparedLine,
                     references))
        {
            var nameGroup = match.Groups["name"];
            var name = nameGroup.Value;
            var leafStart = name.LastIndexOf("::", StringComparison.Ordinal);
            var leaf = leafStart >= 0 ? name[(leafStart + 2)..] : name;
            var leafOffset = leafStart >= 0 ? leafStart + 2 : 0;
            var normalizedLeaf = NormalizeIdentifier(leaf);
            if (normalizedLeaf == "Self" || !IsLikelyRustTypePathLeaf(leaf))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                normalizedLeaf,
                nameGroup.Index + leafOffset,
                "instantiate",
                context,
                lineNumber,
                resolveContainerForColumn(nameGroup.Index));

            var argsGroup = match.Groups["args"];
            if (!argsGroup.Success)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                argsGroup.Value,
                argsGroup.Index,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(argsGroup.Index));
        }
    }

    private static bool IsRustTypeDeclarationLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("pub", StringComparison.Ordinal))
        {
            var afterPub = "pub".Length;
            if (afterPub < trimmed.Length && trimmed[afterPub] == '(')
            {
                var closeParen = ReferenceExtractor.FindMatchingChar(trimmed, afterPub, '(', ')');
                if (closeParen > afterPub)
                    trimmed = trimmed[(closeParen + 1)..].TrimStart();
            }
            else if (afterPub < trimmed.Length && char.IsWhiteSpace(trimmed[afterPub]))
            {
                trimmed = trimmed[afterPub..].TrimStart();
            }
        }

        return trimmed.StartsWith("struct ", StringComparison.Ordinal)
               || trimmed.StartsWith("enum ", StringComparison.Ordinal)
               || trimmed.StartsWith("union ", StringComparison.Ordinal);
    }

    private static void EmitImplAndTraitTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var hasImplMarker = preparedLine.IndexOf("impl", StringComparison.Ordinal) >= 0;
        var hasTraitMarker = preparedLine.IndexOf("trait", StringComparison.Ordinal) >= 0;
        if (!hasImplMarker && !hasTraitMarker)
            return;

        if (hasImplMarker)
        {
            var implIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "impl");
            if (implIndex >= 0)
            {
                var typeListStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, implIndex + "impl".Length);
                var forIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "for");
                var typeListEnd = forIndex >= 0
                    ? forIndex
                    : TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeListStart);

                if (typeListEnd > typeListStart)
                {
                    TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                        preparedLine.Substring(typeListStart, typeListEnd - typeListStart),
                        typeListStart,
                        "rust",
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        resolveContainerForColumn(typeListStart));
                }

                if (forIndex >= 0)
                {
                    var targetStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, forIndex + "for".Length);
                    var targetEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, targetStart);
                    if (targetEnd > targetStart)
                    {
                        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                            preparedLine.Substring(targetStart, targetEnd - targetStart),
                            targetStart,
                            "rust",
                            references,
                            seen,
                            fileId,
                            context,
                            lineNumber,
                            resolveContainerForColumn(targetStart));
                    }
                }
            }
        }

        if (!hasTraitMarker || preparedLine.IndexOf(':') < 0)
            return;

        var traitIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "trait");
        if (traitIndex < 0)
            return;

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', traitIndex + "trait".Length);
        if (colonIndex < 0)
            return;

        var boundsStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        var boundsEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, boundsStart);
        if (boundsEnd <= boundsStart)
            return;

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

        var fullBoundsEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, boundsStart, stopAtArrow: false);
        if (fullBoundsEnd > boundsStart)
        {
            EmitFunctionTraitReturnTypeFromExpression(
                preparedLine.Substring(boundsStart, fullBoundsEnd - boundsStart),
                boundsStart,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
    }

}
