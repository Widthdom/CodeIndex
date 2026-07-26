using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    private static void NormalizeIdentifier(
        string rawName,
        int rawIndex,
        out string resolvedName,
        out int resolvedIndex,
        out bool wasQuoted)
    {
        if (rawName.Length >= 2
            && ((rawName[0] == '[' && rawName[^1] == ']')
                || (rawName[0] == '`' && rawName[^1] == '`')
                || (rawName[0] == '"' && rawName[^1] == '"')))
        {
            resolvedName = rawName.Substring(1, rawName.Length - 2);
            if (rawName[0] == '"')
                resolvedName = resolvedName.Replace("\"\"", "\"", StringComparison.Ordinal);
            else if (rawName[0] == '[')
                resolvedName = resolvedName.Replace("]]", "]", StringComparison.Ordinal);
            resolvedIndex = rawIndex + 1;
            wasQuoted = true;
            return;
        }

        resolvedName = rawName;
        resolvedIndex = rawIndex;
        wasQuoted = false;
    }

    private static bool IsFollowedByOpenParen(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        return index < line.Length && line[index] == '(';
    }

    private static int GetCallLikeSuppressionIndex(string line, int index)
    {
        while (index < line.Length && line[index] == '#')
            index++;

        return index;
    }

    private static void AddCallLikeSuppressionIndices(
        HashSet<int> suppressedCallIndices,
        string line,
        int leafIndex,
        int statementStart,
        int lineOffset)
    {
        var leafSuppressionIndex = GetCallLikeSuppressionIndex(line, leafIndex) + statementStart - lineOffset;
        suppressedCallIndices.Add(leafSuppressionIndex);

        var qualifiedStart = FindQualifiedIdentifierStart(line, leafIndex);
        if (qualifiedStart == leafIndex)
            return;

        suppressedCallIndices.Add(GetCallLikeSuppressionIndex(line, qualifiedStart) + statementStart - lineOffset);
    }

    private static int FindQualifiedIdentifierStart(string line, int leafIndex)
    {
        var start = leafIndex;
        while (start > 0)
        {
            var scan = start - 1;
            while (scan >= 0 && char.IsWhiteSpace(line[scan]))
                scan--;
            if (scan < 0 || line[scan] != '.')
                break;

            scan--;
            while (scan >= 0 && char.IsWhiteSpace(line[scan]))
                scan--;
            if (scan < 0)
                break;

            start = ScanIdentifierSegmentStart(line, scan);
        }

        return start;
    }

    private static int ScanIdentifierSegmentStart(string line, int index)
    {
        if (line[index] == ']')
        {
            index--;
            while (index >= 0)
            {
                if (line[index] == '[')
                    return index;
                index--;
            }

            return 0;
        }

        if (line[index] is '"' or '`')
        {
            var quote = line[index--];
            while (index >= 0)
            {
                if (line[index] == quote)
                    return index;
                index--;
            }

            return 0;
        }

        while (index >= 0 && IsIdentifierContinuationForReverseScan(line[index]))
            index--;

        return index + 1;
    }

    private static bool IsIdentifierContinuationForReverseScan(char ch)
        => ch is '_' or '$' or '#'
           || char.IsLetterOrDigit(ch)
           || char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.NonSpacingMark
               or System.Globalization.UnicodeCategory.SpacingCombiningMark
               or System.Globalization.UnicodeCategory.ConnectorPunctuation;

    private static string CombineStatementPrefix(string prefix, string line, out int lineOffset)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            lineOffset = 0;
            return line;
        }

        lineOffset = prefix.Length + 1;
        return prefix + "\n" + line;
    }

    private static string AdvanceStatementPrefix(
        string combined,
        int statementStart,
        bool lineEndedByLineComment)
    {
        var remaining = statementStart == 0 ? combined : combined[statementStart..];
        if (!lineEndedByLineComment)
            return remaining;

        return CanStatementRequireLineCommentCarry(remaining) ? remaining : string.Empty;
    }

    private static bool ShouldFlushTempObjectPrefixAtLineBoundary(
        string prefix,
        string nextLine)
    {
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(nextLine))
            return false;
        if (!CanStatementEstablishTempObject(prefix))
            return false;

        return StartsTopLevelStatement(nextLine);
    }

    private static bool CanStatementEstablishTempObject(string statement)
    {
        if (statement.IndexOf('#') < 0)
            return false;

        var mayContainTargetStatement = statement.IndexOf("INSERT", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("UPDATE", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("MERGE", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("ALTER", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("BULK", StringComparison.OrdinalIgnoreCase) >= 0;
        if (mayContainTargetStatement && TargetReferenceRegex.IsMatch(statement))
            return true;

        if (statement.IndexOf("TRUNCATE", StringComparison.OrdinalIgnoreCase) >= 0
            && TruncateTargetRegex.IsMatch(statement))
        {
            return true;
        }

        if (statement.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("INTO", StringComparison.OrdinalIgnoreCase) >= 0
            && SelectIntoTargetStatementRegex.IsMatch(statement))
        {
            return true;
        }

        if (statement.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return (statement.IndexOf("TABLE", StringComparison.OrdinalIgnoreCase) >= 0
                && CreateTempTableRegex.IsMatch(statement))
            || ((statement.IndexOf("PROC", StringComparison.OrdinalIgnoreCase) >= 0
                    || statement.IndexOf("FUNCTION", StringComparison.OrdinalIgnoreCase) >= 0)
                && CreateTempRoutineRegex.IsMatch(statement));
    }

    private static bool CanStatementRequireLineCommentCarry(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return false;

        return CanStatementEstablishTempObject(statement)
            || TargetReferencePrefixRegex.IsMatch(statement)
            || FromListContinuationPrefixRegex.IsMatch(statement)
            || SelectIntoTargetPrefixRegex.IsMatch(statement)
            || DeleteUsingPrefixRegex.IsMatch(statement)
            || DeleteUsingListContinuationPrefixRegex.IsMatch(statement)
            || MergeUsingPrefixRegex.IsMatch(statement)
            || MergeTargetHintContinuationPrefixRegex.IsMatch(statement);
    }

    private static bool StartsTopLevelStatement(string line)
    {
        int index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length || !char.IsLetter(line[index]))
            return false;

        int start = index;
        while (index < line.Length && char.IsLetter(line[index]))
            index++;

        var keyword = line[start..index].ToUpperInvariant();
        if (keyword == "WITH")
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            return index >= line.Length || line[index] != '(';
        }

        return keyword switch
        {
            "SELECT" => true,
            "INSERT" => true,
            "UPDATE" => true,
            "DELETE" => true,
            "MERGE" => true,
            "CREATE" => true,
            "ALTER" => true,
            "DROP" => true,
            "TRUNCATE" => true,
            "SET" => true,
            "DECLARE" => true,
            "IF" => true,
            "WHILE" => true,
            "DO" => true,
            "BEGIN" => true,
            "EXEC" => true,
            "EXECUTE" => true,
            "CALL" => true,
            _ => false,
        };
    }

    private static int FindStatementTerminator(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ';')
                return i;
            if (c == '`')
            {
                int closing = text.IndexOf('`', i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
                continue;
            }
            if (c == '[')
            {
                int closing = text.IndexOf(']', i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
                continue;
            }
            if (c == '"')
            {
                int closing = FindClosingDoubleQuote(text, i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
            }
        }

        return -1;
    }

    private static int FindClosingDoubleQuote(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;
            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static int FindClosingSingleQuote(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                continue;
            }
            if (text[i] != '\'')
                continue;
            if (i + 1 < text.Length && text[i + 1] == '\'')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static bool IsInsideDoubleQuotedRegion(string text, int index)
    {
        if (index <= 0)
            return false;

        bool inside = false;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;
            if (inside && i + 1 < index && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            inside = !inside;
        }

        return inside;
    }

    private static bool TryReadDollarQuoteDelimiter(
        string line,
        int index,
        out string delimiter)
    {
        delimiter = string.Empty;
        if (index < 0 || index >= line.Length || line[index] != '$')
            return false;
        if (index > 0 && (char.IsLetterOrDigit(line[index - 1]) || line[index - 1] == '_'))
            return false;
        if (index + 1 >= line.Length)
            return false;
        if (line[index + 1] == '$')
        {
            delimiter = "$$";
            return true;
        }
        if (!(char.IsLetter(line[index + 1]) || line[index + 1] == '_'))
            return false;

        int probe = index + 2;
        while (probe < line.Length && (char.IsLetterOrDigit(line[probe]) || line[probe] == '_'))
            probe++;
        if (probe >= line.Length || line[probe] != '$')
            return false;

        delimiter = line[index..(probe + 1)];
        return true;
    }

    private static int SkipWhitespaceAhead(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static void CollectTempObjectNamesFromStatement(
        string statement,
        HashSet<string> names)
    {
        if (statement.IndexOf('#') < 0)
            return;

        var mayContainTargetStatement = statement.IndexOf("INSERT", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("UPDATE", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("MERGE", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("ALTER", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("BULK", StringComparison.OrdinalIgnoreCase) >= 0;
        if (mayContainTargetStatement)
            CollectTempObjectNamesFromTargetMatches(
                BoundedRegex.EnumerateMatches(TargetReferenceRegex, statement),
                statement,
                names);

        if (statement.IndexOf("TRUNCATE", StringComparison.OrdinalIgnoreCase) >= 0)
            CollectTempObjectNamesFromMatches(
                BoundedRegex.EnumerateMatches(TruncateTargetRegex, statement),
                statement,
                names);

        if (statement.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("INTO", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            CollectTempObjectNamesFromMatches(
                BoundedRegex.EnumerateMatches(SelectIntoTargetStatementRegex, statement),
                statement,
                names);
        }

        if (statement.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        if (statement.IndexOf("TABLE", StringComparison.OrdinalIgnoreCase) >= 0)
            CollectTempObjectNamesFromMatches(
                BoundedRegex.EnumerateMatches(CreateTempTableRegex, statement),
                statement,
                names);
        if (statement.IndexOf("PROC", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("FUNCTION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            CollectTempObjectNamesFromMatches(
                BoundedRegex.EnumerateMatches(CreateTempRoutineRegex, statement),
                statement,
                names);
        }
    }

    private static void CollectTempObjectNamesFromTargetMatches(
        IEnumerable<Match> matches,
        string statement,
        HashSet<string> names)
    {
        foreach (Match match in matches)
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            if (!TryGetTrailingQualifiedIdentifierLeaf(match, out var rawName, out var rawIndex))
                continue;

            NormalizeIdentifier(rawName, rawIndex, out var resolvedName, out _, out _);
            if (resolvedName.StartsWith("#", StringComparison.Ordinal))
                names.Add(resolvedName);
        }
    }

    private static void CollectTempObjectNamesFromMatches(
        IEnumerable<Match> matches,
        string statement,
        HashSet<string> names)
    {
        foreach (Match match in matches)
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Captures.Count == 0)
                continue;

            foreach (Capture capture in nameGroup.Captures)
            {
                NormalizeIdentifier(capture.Value, capture.Index, out var resolvedName, out _, out _);
                if (resolvedName.StartsWith("#", StringComparison.Ordinal))
                    names.Add(resolvedName);
            }
        }
    }

    private static bool TryFindDefinitionLeafSpan(
        string line,
        string qualifiedName,
        Dictionary<string, DefinitionLeafPattern> patternCache,
        out DefinitionLeafSpan span)
    {
        span = default;
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(qualifiedName))
            return false;

        if (!TryGetDefinitionLeafPattern(qualifiedName, patternCache, out var leafPattern))
            return false;

        var match = BoundedRegex.Match(line, leafPattern.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var leafGroup = match.Groups["leaf"];
        if (!leafGroup.Success)
            return false;

        span = new DefinitionLeafSpan(leafPattern.LeafName, leafGroup.Index, leafGroup.Index + leafGroup.Length);
        return true;
    }

    private static bool TryGetDefinitionLeafPattern(
        string qualifiedName,
        Dictionary<string, DefinitionLeafPattern> patternCache,
        out DefinitionLeafPattern leafPattern)
    {
        if (patternCache.TryGetValue(qualifiedName, out leafPattern))
            return true;

        var leafName = SqlNameResolver.GetLeafName(qualifiedName);
        if (string.IsNullOrWhiteSpace(leafName))
            return false;

        if (!TryBuildQualifiedNameSourcePattern(qualifiedName, out var pattern))
            return false;

        leafPattern = new DefinitionLeafPattern(leafName, pattern);
        patternCache[qualifiedName] = leafPattern;
        return true;
    }

    private static bool TryBuildQualifiedNameSourcePattern(string qualifiedName, out string pattern)
    {
        pattern = string.Empty;
        var trimmed = qualifiedName.Trim();
        if (trimmed.Length == 0)
            return false;

        var builder = new StringBuilder(trimmed.Length + "(?<leaf>)".Length);
        string? pendingSegment = null;
        var segmentStart = 0;
        char quote = '\0';

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (quote != '\0')
            {
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < trimmed.Length && trimmed[i + 1] == ']')
                            i++;
                        else
                            quote = '\0';
                    }

                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == quote)
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

            if (ch == '.')
            {
                QueueQualifiedNameSourcePatternSegment(builder, trimmed, segmentStart, i, ref pendingSegment);
                segmentStart = i + 1;
                continue;
            }

        }

        QueueQualifiedNameSourcePatternSegment(builder, trimmed, segmentStart, trimmed.Length, ref pendingSegment);
        if (pendingSegment is null)
            return false;

        AppendQualifiedNameSourcePatternSegment(builder, pendingSegment, isLeaf: true);
        pattern = builder.ToString();
        return true;
    }

    private static void QueueQualifiedNameSourcePatternSegment(
        StringBuilder builder,
        string text,
        int segmentStart,
        int segmentEnd,
        ref string? pendingSegment)
    {
        while (segmentStart < segmentEnd && char.IsWhiteSpace(text[segmentStart]))
            segmentStart++;
        while (segmentEnd > segmentStart && char.IsWhiteSpace(text[segmentEnd - 1]))
            segmentEnd--;
        if (segmentStart >= segmentEnd)
            return;

        if (pendingSegment is not null)
            AppendQualifiedNameSourcePatternSegment(builder, pendingSegment, isLeaf: false);

        pendingSegment = text[segmentStart..segmentEnd];
    }

    private static void AppendQualifiedNameSourcePatternSegment(StringBuilder builder, string segment, bool isLeaf)
    {
        if (builder.Length > 0)
            builder.Append(@"\s*\.\s*");

        var escaped = Regex.Escape(segment);
        if (isLeaf)
            builder.Append("(?<leaf>").Append(escaped).Append(')');
        else
            builder.Append(escaped);
    }

}
