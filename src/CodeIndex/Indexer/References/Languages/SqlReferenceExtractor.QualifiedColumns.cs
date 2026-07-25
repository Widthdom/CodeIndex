using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    private readonly record struct TextSegment(string Text, int StartIndex);

    private static void EmitQualifiedColumnReferences(
        string text,
        int textStart,
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
        string referenceKind)
    {
        foreach (Match match in BoundedRegex.EnumerateMatches(QualifiedColumnReferenceRegex, text))
        {
            if (IsInsideDoubleQuotedRegion(text, match.Index))
                continue;

            var nameGroup = match.Groups["name"];
            EmitMergeColumnReference(
                nameGroup.Value,
                textStart + nameGroup.Index,
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName,
                referenceKind);
        }
    }

    private static void EmitMergeColumnReference(
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
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        string referenceKind)
    {
        var trimmedStart = 0;
        while (trimmedStart < rawName.Length && char.IsWhiteSpace(rawName[trimmedStart]))
            trimmedStart++;
        var trimmedEnd = rawName.Length;
        while (trimmedEnd > trimmedStart && char.IsWhiteSpace(rawName[trimmedEnd - 1]))
            trimmedEnd--;
        if (trimmedStart >= trimmedEnd)
            return;

        rawName = rawName[trimmedStart..trimmedEnd];
        rawIndex += trimmedStart;
        var leafIndex = FindQualifiedIdentifierLeafIndex(rawName);
        rawIndex += leafIndex;
        rawName = rawName[leafIndex..].TrimStart();

        var match = BoundedRegex.Match(
            rawName,
            $"^(?<name>{QuotedIdentifierPattern}|{BareIdentifierPattern})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        var absoluteNameIndex = rawIndex + nameGroup.Index;
        if (absoluteNameIndex < statementLineOffset)
            return;

        NormalizeIdentifier(nameGroup.Value, absoluteNameIndex, out var resolvedName, out var nameIndex, out var wasQuoted);
        if (!wasQuoted && shouldIgnoreName(resolvedName))
            return;

        var nameColumn = nameIndex + statementStart - lineOffset;
        var container = resolveContainerForCall(absoluteNameIndex);
        ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, referenceKind, context, lineNumber, container);
    }

    private static int FindQualifiedIdentifierLeafIndex(string rawName)
    {
        var leafStart = 0;
        var quote = '\0';
        for (var i = 0; i < rawName.Length; i++)
        {
            var ch = rawName[i];
            if (quote != '\0')
            {
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < rawName.Length && rawName[i + 1] == ']')
                            i++;
                        else
                            quote = '\0';
                    }
                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < rawName.Length && rawName[i + 1] == quote)
                        i++;
                    else
                        quote = '\0';
                }
                continue;
            }

            if (ch is '[' or '"' or '`')
            {
                quote = ch;
                continue;
            }

            if (ch != '.')
                continue;

            leafStart = i + 1;
            while (leafStart < rawName.Length && char.IsWhiteSpace(rawName[leafStart]))
                leafStart++;
        }

        return leafStart;
    }

    private static IEnumerable<TextSegment> SplitTopLevelCommaSegments(string text, int textStart)
    {
        var segmentStart = 0;
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < text.Length && text[i + 1] == ']')
                            i++;
                        else
                            quote = '\0';
                    }
                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote)
                        i++;
                    else
                        quote = '\0';
                }
                continue;
            }

            if (ch is '[' or '"' or '`' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                depth++;
                continue;
            }
            if (ch == ')' && depth > 0)
            {
                depth--;
                continue;
            }
            if (ch != ',' || depth != 0)
                continue;

            yield return new TextSegment(text[segmentStart..i], textStart + segmentStart);
            segmentStart = i + 1;
        }

        yield return new TextSegment(text[segmentStart..], textStart + segmentStart);
    }

    private static int IndexOfTopLevelChar(string text, char value)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < text.Length && text[i + 1] == ']')
                            i++;
                        else
                            quote = '\0';
                    }
                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote)
                        i++;
                    else
                        quote = '\0';
                }
                continue;
            }

            if (ch is '[' or '"' or '`' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                depth++;
                continue;
            }
            if (ch == ')' && depth > 0)
            {
                depth--;
                continue;
            }
            if (ch == value && depth == 0)
                return i;
        }

        return -1;
    }

    private static void EmitGeneratedColumnDependencyReferences(
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
        Func<string, bool> shouldIgnoreName)
    {
        var hasAsKeyword = statement.IndexOf("AS", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasGeneratedKeyword = statement.IndexOf("GENERATED", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasNextKeyword = statement.IndexOf("NEXT", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!hasAsKeyword && !hasGeneratedKeyword && !hasNextKeyword)
            return;

        if (!GeneratedColumnMarkerRegex.IsMatch(statement))
            return;

        if (hasAsKeyword || hasGeneratedKeyword)
        {
            foreach (Match match in GeneratedColumnExpressionStartRegex.Matches(statement))
            {
                if (match.Index < statementLineOffset || IsInsideDoubleQuotedRegion(statement, match.Index))
                    continue;
                if (match.Value.TrimStart().StartsWith("AS", StringComparison.OrdinalIgnoreCase)
                    && !IsLikelyComputedColumnAsExpression(statement, match.Index))
                {
                    continue;
                }

                var openParenIndex = statement.IndexOf('(', match.Index + match.Length - 1);
                if (openParenIndex < 0)
                    continue;

                var closeParenIndex = FindMatchingParen(statement, openParenIndex);
                if (closeParenIndex <= openParenIndex)
                    continue;

                EmitSqlExpressionIdentifierDependencies(
                    statement,
                    openParenIndex + 1,
                    closeParenIndex,
                    statementStart,
                    statementLineOffset,
                    lineOffset,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    resolveContainerForCall,
                    shouldIgnoreName);
            }
        }

        if (statement.IndexOf("DEFAULT", StringComparison.OrdinalIgnoreCase) >= 0
            && hasNextKeyword
            && statement.IndexOf("VALUE", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("FOR", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (Match match in DefaultNextValueForExpressionRegex.Matches(statement))
            {
                if (match.Index < statementLineOffset || IsInsideDoubleQuotedRegion(statement, match.Index))
                    continue;

                var sequence = match.Groups["name"];
                EmitSqlExpressionIdentifierDependencies(
                    statement,
                    sequence.Index,
                    sequence.Index + sequence.Length,
                    statementStart,
                    statementLineOffset,
                    lineOffset,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    resolveContainerForCall,
                    shouldIgnoreName);
            }
        }
    }

    private static void EmitSqlExpressionIdentifierDependencies(
        string statement,
        int startIndex,
        int endIndexExclusive,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        var expression = statement[startIndex..endIndexExclusive];
        foreach (Match match in SqlExpressionIdentifierRegex.Matches(expression))
        {
            var rawIndex = startIndex + match.Index;
            if (rawIndex < statementLineOffset || IsInsideDoubleQuotedRegion(statement, rawIndex))
                continue;

            var rawName = match.Value;
            NormalizeIdentifier(rawName, rawIndex, out var resolvedName, out var nameIndex, out var wasQuoted);
            if (!wasQuoted && (shouldIgnoreName(resolvedName) || IsGeneratedColumnDependencyKeyword(resolvedName)))
                continue;

            var nameColumn = nameIndex + statementStart - lineOffset;
            var container = resolveContainerForCall(rawIndex);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "generated_column_dependency", context, lineNumber, container);
        }
    }

    private static bool IsGeneratedColumnDependencyKeyword(string name)
        => name.Equals("GENERATED", StringComparison.OrdinalIgnoreCase)
           || name.Equals("ALWAYS", StringComparison.OrdinalIgnoreCase)
           || name.Equals("AS", StringComparison.OrdinalIgnoreCase)
           || name.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)
           || name.Equals("NEXT", StringComparison.OrdinalIgnoreCase)
           || name.Equals("VALUE", StringComparison.OrdinalIgnoreCase)
           || name.Equals("FOR", StringComparison.OrdinalIgnoreCase)
           || name.Equals("STORED", StringComparison.OrdinalIgnoreCase)
           || name.Equals("VIRTUAL", StringComparison.OrdinalIgnoreCase)
           || name.Equals("PERSISTED", StringComparison.OrdinalIgnoreCase)
           || name.Equals("NULL", StringComparison.OrdinalIgnoreCase)
           || name.Equals("NOT", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyComputedColumnAsExpression(string statement, int asIndex)
    {
        var prefix = statement[..asIndex];
        if (prefix.IndexOf("TABLE", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        if (prefix.IndexOf("ALTER", StringComparison.OrdinalIgnoreCase) < 0
            && prefix.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return Regex.IsMatch(prefix, @"(?<![\w$])ALTER\s+TABLE\b[\s\S]*\bADD\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(prefix, @"(?<![\w$])CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:(?:(?:GLOBAL|LOCAL)\s+)?(?:TEMP|TEMPORARY)\s+|UNLOGGED\s+)?TABLE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

}
