using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class KotlinReferenceExtractor
{
    private static SymbolRecord? FindEnclosingKotlinConstructor(
        IReadOnlyList<SymbolRecord> symbols,
        SymbolRecord enclosingType,
        int lineNumber)
    {
        SymbolRecord? best = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function")
                continue;
            if (!string.Equals(symbol.ContainerName, enclosingType.Name, StringComparison.Ordinal)
                && !IsWithinSymbolRange(enclosingType, symbol.StartLine))
            {
                continue;
            }

            var signature = symbol.Signature?.TrimStart();
            var isSecondaryConstructor = !string.IsNullOrWhiteSpace(signature)
                && (signature.StartsWith("constructor", StringComparison.Ordinal)
                    || signature.StartsWith("public constructor", StringComparison.Ordinal)
                    || signature.StartsWith("private constructor", StringComparison.Ordinal)
                    || signature.StartsWith("protected constructor", StringComparison.Ordinal)
                    || signature.StartsWith("internal constructor", StringComparison.Ordinal));
            if (!isSecondaryConstructor
                && !string.Equals(symbol.Name, enclosingType.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (symbol.StartLine > lineNumber)
                continue;
            var symbolEnd = symbol.BodyEndLine ?? symbol.EndLine;
            if (symbolEnd < lineNumber)
                continue;

            if (best == null || symbol.StartLine >= best.StartLine)
                best = symbol;
        }

        return best;
    }

    private static bool IsWithinSymbolRange(SymbolRecord container, int lineNumber)
    {
        var start = container.BodyStartLine ?? container.StartLine;
        var end = container.BodyEndLine ?? container.EndLine;
        return lineNumber >= start && lineNumber <= end;
    }

    private static string? ParseKotlinBaseType(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(signature, ':');
        if (colonIndex < 0)
            return null;

        var listStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(signature, colonIndex + 1);
        var listEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(
            signature,
            listStart,
            stopAtComma: false);
        if (listEnd <= listStart)
            return null;

        var typeList = signature.AsSpan(listStart, listEnd - listStart);
        string? fallback = null;
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(typeList))
        {
            var segment = typeList.Slice(segmentStart, segmentLength).Trim();
            if (segment.IsEmpty)
                continue;

            var segmentText = segment.ToString();
            var typeName = ExtractKotlinBareTypeName(segmentText);
            if (string.IsNullOrWhiteSpace(typeName))
                continue;

            if (TypedLanguageReferenceExtractor.FindTopLevelChar(segmentText, '(') >= 0)
                return typeName;

            fallback ??= typeName;
        }

        return fallback;
    }

    private static string? ExtractKotlinBareTypeName(string segment)
    {
        var trimmed = TrimTopLevelByClause(segment.Trim());
        if (trimmed.Length == 0)
            return null;

        var callIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(trimmed, '(');
        if (callIndex > 0)
        {
            var callEnd = callIndex;
            while (callEnd > 0 && char.IsWhiteSpace(trimmed[callEnd - 1]))
                callEnd--;
            trimmed = trimmed.Substring(0, callEnd);
        }

        var lastSegmentStart = 0;
        var endIndex = trimmed.Length;
        var angleDepth = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '<')
            {
                if (angleDepth == 0)
                    endIndex = Math.Min(endIndex, i);
                angleDepth++;
            }
            else if (ch == '>')
            {
                if (angleDepth > 0)
                    angleDepth--;
            }
            else if (angleDepth == 0 && ch == '.')
            {
                lastSegmentStart = i + 1;
            }
        }

        if (endIndex < lastSegmentStart)
            endIndex = trimmed.Length;

        var typeNameLeading = 0;
        while (lastSegmentStart + typeNameLeading < endIndex && char.IsWhiteSpace(trimmed[lastSegmentStart + typeNameLeading]))
            typeNameLeading++;

        var typeNameEnd = endIndex;
        while (typeNameEnd > lastSegmentStart + typeNameLeading && char.IsWhiteSpace(trimmed[typeNameEnd - 1]))
            typeNameEnd--;

        var typeName = trimmed.Substring(lastSegmentStart + typeNameLeading, typeNameEnd - lastSegmentStart - typeNameLeading);
        return typeName.Length > 0 ? typeName : null;
    }

    private static string TrimTopLevelByClause(string segment)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        for (var i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            if (ch == '<')
            {
                angleDepth++;
            }
            else if (ch == '>')
            {
                if (angleDepth > 0)
                    angleDepth--;
            }
            else if (ch == '(')
            {
                parenDepth++;
            }
            else if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
            }
            else if (angleDepth == 0
                     && parenDepth == 0
                     && i + 4 <= segment.Length
                     && string.CompareOrdinal(segment, i, " by ", 0, 4) == 0)
            {
                var end = i;
                while (end > 0 && char.IsWhiteSpace(segment[end - 1]))
                    end--;
                return segment.Substring(0, end);
            }
        }

        return segment;
    }
}
