using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    private static string PrepareLineForIdentifierScan(
        string line,
        IdentifierScanState state,
        string? statementPrefix,
        out bool lineEndedByLineComment,
        out IdentifierScanState nextState)
    {
        lineEndedByLineComment = false;
        if (string.IsNullOrEmpty(line))
        {
            nextState = state;
            return line;
        }

        char[]? sanitized = null;
        bool inBlockComment = state.InBlockComment;
        string? dollarQuoteDelimiter = state.DollarQuoteDelimiter;
        bool inSingleQuotedString = state.InSingleQuotedString;

        void BlankRange(int start, int endExclusive)
        {
            start = Math.Max(0, start);
            endExclusive = Math.Min(line.Length, endExclusive);
            sanitized ??= line.ToCharArray();
            for (int blankIndex = start; blankIndex < endExclusive; blankIndex++)
                sanitized[blankIndex] = ' ';
        }

        for (int i = 0; i < line.Length;)
        {
            if (inBlockComment)
            {
                int closing = line.IndexOf("*/", i, StringComparison.Ordinal);
                int end = closing >= 0 ? closing + 2 : line.Length;
                BlankRange(i, end);
                if (closing < 0)
                    break;
                i = end;
                inBlockComment = false;
                continue;
            }
            if (!string.IsNullOrEmpty(dollarQuoteDelimiter))
            {
                int closing = line.IndexOf(dollarQuoteDelimiter, i, StringComparison.Ordinal);
                if (closing < 0)
                {
                    BlankRange(i, line.Length);
                    break;
                }

                int nextContent = SkipWhitespaceAhead(line, closing + dollarQuoteDelimiter.Length);
                if (nextContent < line.Length
                    && line[nextContent] != ';'
                    && line[nextContent] != ','
                    && line[nextContent] != ')'
                    && line[nextContent] != ']')
                {
                    int nestedClosing = line.IndexOf(
                        dollarQuoteDelimiter,
                        closing + dollarQuoteDelimiter.Length,
                        StringComparison.Ordinal);
                    if (nestedClosing >= 0)
                    {
                        int end = nestedClosing + dollarQuoteDelimiter.Length;
                        BlankRange(i, end);
                        i = end;
                        continue;
                    }
                }

                int closingEnd = closing + dollarQuoteDelimiter.Length;
                BlankRange(i, closingEnd);
                i = closingEnd;
                dollarQuoteDelimiter = null;
                continue;
            }
            if (inSingleQuotedString)
            {
                int closing = FindClosingSingleQuote(line, i);
                int end = closing >= 0 ? closing + 1 : line.Length;
                BlankRange(i, end);
                i = end;
                if (closing >= 0)
                {
                    inSingleQuotedString = false;
                    continue;
                }

                break;
            }

            char c = line[i];
            if (c == '"')
            {
                int closing = FindClosingDoubleQuote(line, i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '`')
            {
                int closing = line.IndexOf('`', i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '[')
            {
                int closing = line.IndexOf(']', i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '\'')
            {
                int closing = FindClosingSingleQuote(line, i + 1);
                int end = closing >= 0 ? closing + 1 : line.Length;
                BlankRange(i, end);
                i = end;
                if (closing < 0)
                    inSingleQuotedString = true;
                continue;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                BlankRange(i, i + 2);
                i += 2;
                inBlockComment = true;
                continue;
            }
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                lineEndedByLineComment = true;
                BlankRange(i, line.Length);
                break;
            }
            if (c == '#')
            {
                if (ShouldTreatHashAsComment(line, i, statementPrefix))
                {
                    lineEndedByLineComment = true;
                    BlankRange(i, line.Length);
                    break;
                }
            }
            if (c == '$' && TryReadDollarQuoteDelimiter(line, i, out var delimiter))
            {
                BlankRange(i, i + delimiter.Length);
                i += delimiter.Length;
                dollarQuoteDelimiter = delimiter;
                continue;
            }

            i++;
        }

        nextState = new IdentifierScanState(inBlockComment, dollarQuoteDelimiter, inSingleQuotedString);
        return sanitized is null ? line : new string(sanitized);
    }

    private static bool ShouldTreatHashAsComment(string line, int hashIndex, string? statementPrefix)
    {
        if (hashIndex < 0 || hashIndex >= line.Length || line[hashIndex] != '#')
            return false;

        int probe = hashIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(line[probe]))
            probe--;
        if (probe < 0 && !string.IsNullOrWhiteSpace(statementPrefix))
        {
            var combined = statementPrefix + "\n" + line;
            return ShouldTreatHashAsCommentCore(combined, statementPrefix.Length + 1 + hashIndex);
        }

        return ShouldTreatHashAsCommentCore(line, hashIndex);
    }

    private static bool ShouldTreatHashAsCommentCore(string line, int hashIndex)
    {
        if (hashIndex < 0 || hashIndex >= line.Length || line[hashIndex] != '#')
            return false;

        int next = hashIndex + 1;
        if (hashIndex > 0
            && line[hashIndex - 1] == '#'
            && next < line.Length
            && (char.IsLetterOrDigit(line[next]) || line[next] == '_'))
            return false;
        if (next + 1 < line.Length
            && line[next] == '#'
            && (char.IsLetterOrDigit(line[next + 1]) || line[next + 1] == '_'))
            return false;
        if (next >= line.Length || !(char.IsLetterOrDigit(line[next]) || line[next] == '_'))
            return true;

        int probe = hashIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(line[probe]))
            probe--;
        while (probe >= 0 && line[probe] == ',')
        {
            var priorListItem = line[..probe];
            int sourceStart = FindLastCommaOutsideQuotedIdentifiers(priorListItem);
            if (sourceStart >= 0)
                sourceStart++;
            else
            {
                var usingMatches = UsingKeywordRegex.Matches(priorListItem);
                if (usingMatches.Count > 0)
                    sourceStart = usingMatches[^1].Index + usingMatches[^1].Length;
                else
                {
                    sourceStart = priorListItem.LastIndexOf('#');
                    if (sourceStart < 0)
                        return true;
                }
            }
            while (sourceStart < priorListItem.Length && char.IsWhiteSpace(priorListItem[sourceStart]))
                sourceStart++;

            var listMatch = TrailingTempIdentifierRegex.Match(priorListItem[sourceStart..]);
            if (!listMatch.Success)
                return true;

            probe = sourceStart - 1;
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
        }
        if (probe < 0)
            return true;
        if (line[probe] == '.')
            return false;
        if (line[probe] == ')')
        {
            int depth = 1;
            probe--;
            while (probe >= 0 && depth > 0)
            {
                if (line[probe] == ')')
                    depth++;
                else if (line[probe] == '(')
                    depth--;
                probe--;
            }
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
            if (probe < 0)
                return true;

            int modifierEnd = probe;
            while (probe >= 0 && char.IsLetter(line[probe]))
                probe--;
            int modifierStart = probe + 1;
            if (modifierStart <= modifierEnd
                && string.Equals(line[modifierStart..(modifierEnd + 1)], "TOP", StringComparison.OrdinalIgnoreCase))
            {
                while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                    probe--;
                if (probe < 0)
                    return true;
            }
        }

        int tokenEnd = probe;
        while (probe >= 0 && char.IsLetter(line[probe]))
            probe--;
        int tokenStart = probe + 1;
        if (tokenStart > tokenEnd)
            return true;

        var token = line[tokenStart..(tokenEnd + 1)];
        return !string.Equals(token, "FROM", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "JOIN", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "MERGE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "USING", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "INTO", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "UPDATE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "TABLE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "EXEC", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "EXECUTE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "CALL", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "PROCEDURE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "PROC", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "FUNCTION", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindLastCommaOutsideQuotedIdentifiers(string text)
    {
        int lastComma = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                int closing = FindClosingDoubleQuote(text, i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == '`')
            {
                int closing = text.IndexOf('`', i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == '[')
            {
                int closing = text.IndexOf(']', i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == ',')
                lastComma = i;
        }

        return lastComma;
    }
}
