using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    private static void EmitSourceReference(
        string rawName,
        int rawIndex,
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        string referenceKind = "reference")
    {
        if (rawIndex < statementLineOffset)
            return;

        var followedByOpenParen = IsFollowedByOpenParen(statement, rawIndex + rawName.Length);
        NormalizeIdentifier(rawName, rawIndex, out var resolvedName, out var nameIndex, out var wasQuoted);
        int nameColumn = nameIndex + statementStart - lineOffset;
        if (!wasQuoted && shouldIgnoreName(resolvedName))
            return;
        if (followedByOpenParen)
        {
            var container = resolveContainerForCall(rawIndex);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "call", context, lineNumber, container);
            if (!wasQuoted)
                suppressedCallIndices.Add(GetCallLikeSuppressionIndex(statement, rawIndex) + statementStart - lineOffset);
            return;
        }
        if (resolvedName.StartsWith("#", StringComparison.Ordinal)
            && !establishedTempObjectNames.Contains(resolvedName))
            return;

        var referenceContainer = resolveContainerForCall(rawIndex);
        ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, referenceKind, context, lineNumber, referenceContainer);
    }

    private static string GetSourceReferenceKind(int index, IReadOnlyList<CteBodySpan>? cteBodySpans)
    {
        if (cteBodySpans == null)
            return "reference";

        foreach (var span in cteBodySpans)
        {
            if (index >= span.StartIndex && index < span.EndIndexExclusive)
                return "cte_body_reference";
        }

        return "reference";
    }

    private static List<CteBodySpan>? FindCteBodySpans(string statement)
    {
        if (statement.IndexOf("WITH", StringComparison.OrdinalIgnoreCase) < 0)
            return null;

        List<CteBodySpan>? spans = null;
        foreach (Match match in BoundedRegex.EnumerateMatches(CteDefinitionRegex, statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            var openParenIndex = match.Index + match.Length - 1;
            var closeParenIndex = FindMatchingParen(statement, openParenIndex);
            (spans ??= []).Add(new CteBodySpan(openParenIndex + 1, closeParenIndex < 0 ? statement.Length : closeParenIndex));
        }

        return spans;
    }

    private static int FindMatchingParen(string text, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
                continue;
            }

            if (text[i] != ')')
                continue;

            depth--;
            if (depth == 0)
                return i;
        }

        return -1;
    }

    private static int FindMatchingOpenParen(string text, int closeParenIndex)
    {
        var depth = 0;
        for (var i = closeParenIndex; i >= 0; i--)
        {
            if (text[i] == ')')
            {
                depth++;
                continue;
            }

            if (text[i] != '(')
                continue;

            depth--;
            if (depth == 0)
                return i;
        }

        return -1;
    }

    private static void EmitSelectIntoTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int>? suppressedCallIndices = null)
    {
        foreach (Match match in BoundedRegex.EnumerateMatches(SelectIntoTargetStatementRegex, statement))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;
            NormalizeIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
            int nameColumn = nameIndex + statementStart - lineOffset;
            if (!wasQuoted && shouldIgnoreName(resolvedName))
                continue;

            var container = resolveContainerForCall(nameGroup.Index);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, container);
        }
    }

    private static void EmitTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in BoundedRegex.EnumerateMatches(TargetReferenceRegex, statement))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            if (!TryGetTrailingQualifiedIdentifierLeaf(match, out var rawName, out var rawIndex))
                continue;
            if (rawIndex < statementLineOffset)
                continue;
            NormalizeIdentifier(rawName, rawIndex, out var resolvedName, out var nameIndex, out var wasQuoted);
            int nameColumn = nameIndex + statementStart - lineOffset;
            if (!wasQuoted && shouldIgnoreName(resolvedName))
                continue;
            if (!wasQuoted
                && string.Equals(resolvedName, "SET", StringComparison.OrdinalIgnoreCase)
                && match.Value.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!wasQuoted
                && string.Equals(resolvedName, "STATISTICS", StringComparison.OrdinalIgnoreCase)
                && match.Value.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                continue;

            var container = resolveContainerForCall(rawIndex);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, container);
            if (IsFollowedByOpenParen(statement, rawIndex + rawName.Length))
                AddCallLikeSuppressionIndices(suppressedCallIndices, statement, rawIndex, statementStart, lineOffset);
        }
    }

    private static bool TryGetTrailingQualifiedIdentifierLeaf(Match match, out string rawName, out int rawIndex) =>
        TryGetTrailingQualifiedIdentifierLeaf(match.Value, match.Index, out rawName, out rawIndex);

    private static bool TryGetTrailingQualifiedIdentifierLeaf(
        string text,
        int absoluteOffset,
        out string rawName,
        out int rawIndex)
    {
        rawName = string.Empty;
        rawIndex = -1;

        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            end--;
        if (end <= 0)
            return false;

        var last = text[end - 1];
        if (last == ']')
        {
            var bracketStart = text.LastIndexOf('[', end - 2);
            if (bracketStart >= 0)
            {
                rawName = text[bracketStart..end];
                rawIndex = absoluteOffset + bracketStart;
                return true;
            }

            return false;
        }

        if (last == '`')
        {
            var backtickStart = text.LastIndexOf('`', end - 2);
            if (backtickStart >= 0)
            {
                rawName = text[backtickStart..end];
                rawIndex = absoluteOffset + backtickStart;
                return true;
            }

            return false;
        }

        if (last == '"')
        {
            var doubleQuoteStart = FindOpeningDoubleQuoteForIdentifier(text, end - 1);
            if (doubleQuoteStart >= 0)
            {
                rawName = text[doubleQuoteStart..end];
                rawIndex = absoluteOffset + doubleQuoteStart;
                return true;
            }

            return false;
        }

        var bareStart = end - 1;
        while (bareStart >= 0 && IsSqlBareIdentifierPart(text[bareStart]))
            bareStart--;
        bareStart++;
        if (bareStart >= end)
            return false;

        var identifierStart = bareStart;
        if (identifierStart > 0 && text[identifierStart - 1] == '#')
        {
            identifierStart--;
            if (identifierStart > 0 && text[identifierStart - 1] == '#')
                identifierStart--;
            if (identifierStart > 0 && text[identifierStart - 1] == '#')
                return false;
        }
        else if (!IsSqlBareIdentifierStart(text[identifierStart]))
        {
            return false;
        }

        rawName = text[identifierStart..end];
        rawIndex = absoluteOffset + identifierStart;
        return true;
    }

    private static int FindOpeningDoubleQuoteForIdentifier(string text, int closingQuoteIndex)
    {
        var index = closingQuoteIndex - 1;
        while (index >= 0)
        {
            if (text[index] != '"')
            {
                index--;
                continue;
            }

            var runStart = index;
            while (runStart > 0 && text[runStart - 1] == '"')
                runStart--;
            var runLength = index - runStart + 1;
            if (runLength % 2 == 1)
                return runStart;

            index = runStart - 1;
        }

        return -1;
    }

    private static bool IsSqlBareIdentifierStart(char value) =>
        value == '_' || char.IsLetter(value);

    private static bool IsSqlBareIdentifierPart(char value)
    {
        if (char.IsLetterOrDigit(value) || value == '_' || value == '$')
            return true;

        var category = CharUnicodeInfo.GetUnicodeCategory(value);
        return category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.ConnectorPunctuation;
    }

    private static void EmitMultiTargetReferences(
        IEnumerable<Match> matches,
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int>? suppressedCallIndices = null)
    {
        foreach (Match match in matches)
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            foreach (Capture capture in match.Groups["name"].Captures)
            {
                if (capture.Index < statementLineOffset)
                    continue;
                NormalizeIdentifier(capture.Value, capture.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                int nameColumn = nameIndex + statementStart - lineOffset;
                if (!wasQuoted && shouldIgnoreName(resolvedName))
                    continue;

                var container = resolveContainerForCall(capture.Index);
                ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, container);
                if (suppressedCallIndices != null && IsFollowedByOpenParen(statement, capture.Index + capture.Length))
                    AddCallLikeSuppressionIndices(suppressedCallIndices, statement, capture.Index, statementStart, lineOffset);
            }
        }
    }

}
