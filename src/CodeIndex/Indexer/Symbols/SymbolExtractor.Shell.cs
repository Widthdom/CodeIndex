using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExpandShellAliasSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (var (tokenStart, tokenEnd) in EnumerateShellAliasDefinitions(line))
            {
                var token = line[tokenStart..tokenEnd];
                var equalsIndex = token.IndexOf('=');
                if (equalsIndex <= 0)
                    continue;

                var name = token[..equalsIndex].Trim();
                if (!IsShellAliasName(name))
                    continue;

                if (HasShellAliasSymbol(symbols, fileId, i + 1, name))
                    continue;

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    i + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "alias",
                        Name = name,
                        Line = i + 1,
                        StartLine = i + 1,
                        StartColumn = tokenStart,
                        EndLine = i + 1,
                        Signature = line.Trim(),
                    },
                    line);
            }
        }
    }

    private static IEnumerable<(int Start, int End)> EnumerateShellAliasDefinitions(string line)
    {
        var segmentStart = 0;
        while (segmentStart < line.Length)
        {
            var segmentEnd = FindShellAliasSegmentEnd(line, segmentStart);
            var segment = line[segmentStart..segmentEnd];
            var trimmedSegment = segment.TrimStart();
            var trimmedOffset = segment.Length - trimmedSegment.Length;

            if (trimmedSegment.StartsWith("alias", StringComparison.Ordinal))
            {
                var cursor = trimmedOffset + "alias".Length;
                while (TryReadShellAliasToken(segment, ref cursor, out var tokenStart, out var tokenEnd))
                {
                    var token = segment[tokenStart..tokenEnd];
                    if (token.Length == 0)
                        continue;

                    if (token[0] == '-' && token.IndexOf('=') < 0)
                        continue;

                    if (token.IndexOf('=') > 0)
                        yield return (segmentStart + tokenStart, segmentStart + tokenEnd);
                }
            }

            if (segmentEnd >= line.Length)
                break;

            segmentStart = segmentEnd + 1;
            if (segmentStart < line.Length && line[segmentEnd] == '&' && segmentEnd + 1 < line.Length && line[segmentEnd + 1] == '&')
                segmentStart++;
            else if (segmentStart < line.Length && line[segmentEnd] == '|' && segmentEnd + 1 < line.Length && line[segmentEnd + 1] == '|')
                segmentStart++;
        }
    }

    private static int FindShellAliasSegmentEnd(string line, int startIndex)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (var i = startIndex; i < line.Length; i++)
        {
            var ch = line[i];
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '"')
                {
                    inDoubleQuote = false;
                    continue;
                }

                if (ch == '\\')
                    escapeNext = true;
                continue;
            }

            if (ch == '#')
                return i;

            if (ch == '\'')
            {
                inSingleQuote = true;
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = true;
                continue;
            }

            if (ch == ';' || ch == '|' || ch == '&')
                return i;

            if (ch == '\\')
                escapeNext = true;
        }

        return line.Length;
    }

    private static bool TryReadShellAliasToken(string segment, ref int cursor, out int tokenStart, out int tokenEnd)
    {
        tokenStart = -1;
        tokenEnd = -1;

        while (cursor < segment.Length && char.IsWhiteSpace(segment[cursor]))
            cursor++;

        if (cursor >= segment.Length)
            return false;

        if (segment[cursor] is ';' or '|' or '&' or '#')
            return false;

        tokenStart = cursor;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        while (cursor < segment.Length)
        {
            var ch = segment[cursor];
            if (escapeNext)
            {
                escapeNext = false;
                cursor++;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\'')
                    inSingleQuote = false;
                cursor++;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '"')
                {
                    inDoubleQuote = false;
                    cursor++;
                    continue;
                }

                if (ch == '\\')
                    escapeNext = true;
                cursor++;
                continue;
            }

            if (char.IsWhiteSpace(ch) || ch is ';' or '|' or '&' or '#')
                break;

            if (ch == '\'')
            {
                inSingleQuote = true;
                cursor++;
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = true;
                cursor++;
                continue;
            }

            if (ch == '\\')
                escapeNext = true;

            cursor++;
        }

        tokenEnd = cursor;
        return tokenEnd > tokenStart;
    }

    private static bool HasShellAliasSymbol(List<SymbolRecord> symbols, long fileId, int lineNumber, string name)
    {
        return symbols.Any(symbol =>
            symbol.FileId == fileId
            && symbol.Line == lineNumber
            && symbol.Kind == "alias"
            && string.Equals(symbol.Name, name, StringComparison.Ordinal));
    }

    private static bool IsShellAliasName(string name) =>
        Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);

}
