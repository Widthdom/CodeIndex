using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    static bool HasCSharpQualifiedSeparatorBeforeToken(string line, int tokenStart)
    {
        var probe = tokenStart - 1;
        while (probe >= 0 && char.IsWhiteSpace(line[probe]))
            probe--;

        if (probe < 0)
            return false;
        if (line[probe] == '.')
            return true;
        return line[probe] == ':' && probe >= 1 && line[probe - 1] == ':';
    }

    static bool TryGetCSharpTokenBoundsAtColumn(string line, int column, string symbolName, out int tokenStart, out int tokenNameStart)
    {
        if (!TryGetCSharpIdentifierAtColumn(line, column, out tokenStart, out tokenNameStart, out var tokenName)
            || string.IsNullOrWhiteSpace(symbolName))
        {
            return false;
        }

        return string.Equals(tokenName, symbolName, StringComparison.Ordinal);
    }

    static bool TryGetCSharpIdentifierAtColumn(string line, int column, out int tokenStart, out int tokenNameStart, out string tokenName)
    {
        tokenStart = column - 1;
        tokenNameStart = tokenStart;
        tokenName = string.Empty;
        if (tokenStart < 0 || tokenStart >= line.Length)
            return false;

        if (line[tokenNameStart] == '@')
            tokenNameStart++;

        if (tokenNameStart >= line.Length || !IsCSharpIdentifierPart(line[tokenNameStart]))
            return false;

        var tokenEnd = tokenNameStart + 1;
        while (tokenEnd < line.Length && IsCSharpIdentifierPart(line[tokenEnd]))
            tokenEnd++;

        tokenName = NormalizeCSharpIdentifier(line[tokenStart..tokenEnd]);
        return !string.IsNullOrWhiteSpace(tokenName);
    }

    static bool TryGetCSharpQualifiedPrefixAtColumn(string line, int column, string symbolName, out string prefix)
    {
        prefix = string.Empty;
        if (!TryGetCSharpTokenBoundsAtColumn(line, column, symbolName, out var tokenStart, out _)
            || !HasCSharpQualifiedSeparatorBeforeToken(line, tokenStart))
        {
            return false;
        }

        var cursor = tokenStart - 1;
        while (cursor >= 0 && char.IsWhiteSpace(line[cursor]))
            cursor--;
        if (cursor >= 0 && line[cursor] == '.')
            cursor--;
        else if (cursor >= 1 && line[cursor] == ':' && line[cursor - 1] == ':')
            cursor -= 2;
        else
            return false;

        string? singleSegment = null;
        List<string>? segments = null;
        while (cursor >= 0)
        {
            while (cursor >= 0 && char.IsWhiteSpace(line[cursor]))
                cursor--;

            var segmentEnd = cursor;
            while (cursor >= 0 && (IsCSharpIdentifierPart(line[cursor]) || line[cursor] == '@'))
                cursor--;

            var segmentStart = cursor + 1;
            if (segmentStart > segmentEnd)
                return false;

            var segment = NormalizeCSharpIdentifier(line[segmentStart..(segmentEnd + 1)]);
            if (singleSegment is null && segments is null)
            {
                singleSegment = segment;
            }
            else
            {
                if (segments is null)
                {
                    segments = [singleSegment!];
                    singleSegment = null;
                }

                segments.Add(segment);
            }
            while (cursor >= 0 && char.IsWhiteSpace(line[cursor]))
                cursor--;

            if (cursor >= 0 && line[cursor] == '.')
            {
                cursor--;
                continue;
            }

            if (cursor >= 1 && line[cursor] == ':' && line[cursor - 1] == ':')
            {
                cursor -= 2;
                continue;
            }

            break;
        }

        if (segments is null)
        {
            if (singleSegment is null)
                return false;

            prefix = singleSegment;
            return true;
        }

        if (segments.Count == 0)
            return false;

        segments.Reverse();
        prefix = string.Join('.', segments);
        return true;
    }

    static bool TryCollectCSharpInvocationArguments(string[] sourceLines, int lineIndex, int openParen, out string args)
    {
        const int MaxInvocationLines = 32;
        var initialCapacity = lineIndex >= 0 && lineIndex < sourceLines.Length
            ? Math.Min(512, Math.Max(0, sourceLines[lineIndex].Length - openParen))
            : 0;
        var builder = new StringBuilder(initialCapacity);
        var depth = 0;
        var started = false;
        var lineLimit = Math.Min(sourceLines.Length, lineIndex + MaxInvocationLines);

        for (var currentLineIndex = lineIndex; currentLineIndex < lineLimit; currentLineIndex++)
        {
            var line = sourceLines[currentLineIndex];
            for (var i = currentLineIndex == lineIndex ? openParen : 0; i < line.Length;)
            {
                var skippedIndex = i;
                if (TrySkipCSharpStringOrCharLiteral(line.AsSpan(), ref skippedIndex))
                {
                    if (started && depth > 0)
                        builder.Append(line.AsSpan(i, skippedIndex - i));
                    i = skippedIndex;
                    continue;
                }

                skippedIndex = i;
                if (TrySkipCSharpComment(line.AsSpan(), ref skippedIndex))
                {
                    if (started && depth > 0)
                        builder.Append(' ');
                    i = skippedIndex;
                    continue;
                }

                var ch = line[i++];
                if (ch == '(')
                {
                    if (started && depth > 0)
                        builder.Append(ch);
                    depth++;
                    started = true;
                    continue;
                }

                if (ch == ')' && started)
                {
                    depth--;
                    if (depth == 0)
                    {
                        args = builder.ToString();
                        return true;
                    }

                    builder.Append(ch);
                    continue;
                }

                if (started && depth > 0)
                    builder.Append(ch);
            }

            if (started && depth > 0)
                builder.Append('\n');
        }

        args = string.Empty;
        return false;
    }

    static int CountTopLevelCSharpArguments(ReadOnlySpan<char> args, out bool hasNamedMatchTimeout)
    {
        hasNamedMatchTimeout = false;
        var count = 0;
        var tokenStart = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var i = 0; i <= args.Length; i++)
        {
            var atEnd = i == args.Length;
            if (!atEnd)
            {
                if (TrySkipCSharpStringOrCharLiteral(args, ref i)
                    || TrySkipCSharpComment(args, ref i))
                {
                    i--;
                    continue;
                }

                var ch = args[i];
                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (ch == '{')
                    braceDepth++;
                else if (ch == '}' && braceDepth > 0)
                    braceDepth--;

                if (ch != ',' || parenDepth != 0 || bracketDepth != 0 || braceDepth != 0)
                    continue;
            }

            var segment = args[tokenStart..i].Trim();
            if (!segment.IsEmpty)
            {
                count++;
                if (CSharpArgumentHasNamedMatchTimeout(segment))
                    hasNamedMatchTimeout = true;
            }

            tokenStart = i + 1;
        }

        return count;
    }

    static bool TrySkipCSharpComment(ReadOnlySpan<char> text, ref int index)
    {
        if (index + 1 >= text.Length || text[index] != '/')
            return false;

        if (text[index + 1] == '/')
        {
            index = text.Length;
            return true;
        }

        if (text[index + 1] != '*')
            return false;

        index += 2;
        while (index + 1 < text.Length)
        {
            if (text[index] == '*' && text[index + 1] == '/')
            {
                index += 2;
                return true;
            }

            index++;
        }

        index = text.Length;
        return true;
    }

    static bool TrySkipCSharpStringOrCharLiteral(ReadOnlySpan<char> text, ref int index)
    {
        var cursor = index;
        var verbatim = false;

        if (cursor < text.Length && text[cursor] == '@')
        {
            verbatim = true;
            cursor++;
            while (cursor < text.Length && text[cursor] == '$')
                cursor++;
        }
        else
        {
            while (cursor < text.Length && text[cursor] == '$')
                cursor++;
            if (cursor < text.Length && text[cursor] == '@')
            {
                verbatim = true;
                cursor++;
            }
        }

        if (cursor >= text.Length || (text[cursor] != '"' && text[cursor] != '\''))
            return false;

        var quote = text[cursor];
        if (quote == '\'')
        {
            index = cursor + 1;
            while (index < text.Length)
            {
                if (text[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (text[index++] == '\'')
                    return true;
            }

            return true;
        }

        var quoteCount = 0;
        while (cursor + quoteCount < text.Length && text[cursor + quoteCount] == '"')
            quoteCount++;

        if (!verbatim && quoteCount >= 3)
        {
            index = cursor + quoteCount;
            while (index + quoteCount <= text.Length)
            {
                var matched = true;
                for (var offset = 0; offset < quoteCount; offset++)
                {
                    if (text[index + offset] != '"')
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    index += quoteCount;
                    return true;
                }

                index++;
            }

            index = text.Length;
            return true;
        }

        index = cursor + 1;
        while (index < text.Length)
        {
            if (!verbatim && text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == '"')
            {
                if (verbatim && index + 1 < text.Length && text[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                index++;
                return true;
            }

            index++;
        }

        return true;
    }

    static bool CSharpArgumentHasNamedMatchTimeout(ReadOnlySpan<char> argument)
    {
        const string MatchTimeoutName = "matchTimeout";
        var cursor = 0;
        while (cursor < argument.Length && char.IsWhiteSpace(argument[cursor]))
            cursor++;
        if (cursor + MatchTimeoutName.Length > argument.Length
            || !argument[cursor..(cursor + MatchTimeoutName.Length)].Equals(MatchTimeoutName, StringComparison.Ordinal))
        {
            return false;
        }

        cursor += MatchTimeoutName.Length;
        while (cursor < argument.Length && char.IsWhiteSpace(argument[cursor]))
            cursor++;
        return cursor < argument.Length && argument[cursor] == ':';
    }

    static string NormalizeCSharpBclRegexQualifiedName(string value)
    {
        var normalized = NormalizeCSharpAliasTargetForTypeLookup(value);
        normalized = TrimLeadingCSharpGlobalQualifier(normalized);
        if (normalized.StartsWith("global.", StringComparison.Ordinal))
            normalized = normalized["global.".Length..];
        return normalized;
    }
}
