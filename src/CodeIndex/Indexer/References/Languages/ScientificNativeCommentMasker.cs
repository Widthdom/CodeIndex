namespace CodeIndex.Indexer;

internal static class ScientificNativeCommentMasker
{
    internal static string[] MaskBlockComments(string language, string[] lines) =>
        language switch
        {
            "d" => MaskNestedBlockComments(MaskDTokenBraceStrings(lines), "/+", "+/", "//"),
            "julia" => MaskNestedBlockComments(
                MaskTripleQuotedStrings(lines, "#=", "=#", "#"),
                "#=",
                "=#",
                "#",
                singleQuoteCanBePostfix: true),
            "nim" => MaskNestedBlockComments(
                MaskTripleQuotedStrings(lines, "#[", "]#", "#"),
                "#[",
                "]#",
                "#"),
            "matlab" => MaskMatlabBlockComments(lines),
            _ => lines,
        };

    internal static string MaskLineStringLiteralsPreservingPostfixSingleQuotes(
        string line,
        bool useMatlabStringRules)
    {
        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= line.ToCharArray())[index] = ' ';

        for (var cursor = 0; cursor < line.Length; cursor++)
        {
            var quote = line[cursor];
            if (quote is not ('"' or '\'' or '`'))
                continue;

            if (quote == '\''
                && IsPostfixSingleQuote(
                    line,
                    cursor,
                    skipWhitespace: !useMatlabStringRules))
                continue;

            MaskAt(cursor);
            cursor++;
            while (cursor < line.Length)
            {
                var current = line[cursor];
                MaskAt(cursor);
                if (!useMatlabStringRules
                    && current == '\\'
                    && cursor + 1 < line.Length)
                {
                    MaskAt(++cursor);
                    cursor++;
                    continue;
                }

                if (current != quote)
                {
                    cursor++;
                    continue;
                }

                if (cursor + 1 < line.Length && line[cursor + 1] == quote)
                {
                    MaskAt(++cursor);
                    cursor++;
                    continue;
                }

                break;
            }
        }

        return chars is null ? line : new string(chars);
    }

    private static string[] MaskTripleQuotedStrings(
        string[] lines,
        string blockOpening,
        string blockClosing,
        string lineComment)
    {
        if (!MayContain(lines, "\"\"\""))
            return lines;

        var result = new string[lines.Length];
        var inTripleQuotedString = false;
        var blockCommentDepth = 0;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            char[]? chars = null;
            var quote = '\0';

            void MaskAt(int index) =>
                (chars ??= line.ToCharArray())[index] = ' ';

            var cursor = 0;
            while (cursor < line.Length)
            {
                if (inTripleQuotedString)
                {
                    if (StartsWith(line, cursor, "\"\"\""))
                    {
                        MaskToken("\"\"\"");
                        inTripleQuotedString = false;
                        continue;
                    }

                    MaskAt(cursor++);
                    continue;
                }

                if (blockCommentDepth > 0)
                {
                    if (StartsWith(line, cursor, blockOpening))
                    {
                        blockCommentDepth++;
                        cursor += blockOpening.Length;
                        continue;
                    }

                    if (StartsWith(line, cursor, blockClosing))
                    {
                        blockCommentDepth--;
                        cursor += blockClosing.Length;
                        continue;
                    }

                    cursor++;
                    continue;
                }

                if (quote != '\0')
                {
                    if (line[cursor] == '\\' && cursor + 1 < line.Length)
                    {
                        cursor += 2;
                        continue;
                    }

                    if (line[cursor] == quote)
                        quote = '\0';
                    cursor++;
                    continue;
                }

                if (StartsWith(line, cursor, lineComment))
                    break;

                if (StartsWith(line, cursor, blockOpening))
                {
                    blockCommentDepth++;
                    cursor += blockOpening.Length;
                    continue;
                }

                if (StartsWith(line, cursor, "\"\"\""))
                {
                    MaskToken("\"\"\"");
                    inTripleQuotedString = true;
                    continue;
                }

                if (line[cursor] is '"' or '\'' or '`')
                {
                    quote = line[cursor++];
                    continue;
                }

                cursor++;
            }

            result[lineIndex] = chars is null ? line : new string(chars);

            void MaskToken(string token)
            {
                for (var tokenIndex = 0; tokenIndex < token.Length; tokenIndex++)
                    MaskAt(cursor++);
            }
        }

        return result;
    }

    private static string[] MaskDTokenBraceStrings(string[] lines)
    {
        if (!MayContain(lines, "q{"))
            return lines;

        var result = new string[lines.Length];
        var tokenStringDepth = 0;
        var blockCommentDepth = 0;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            char[]? chars = null;
            var quote = '\0';

            void MaskAt(int index) =>
                (chars ??= line.ToCharArray())[index] = ' ';

            var cursor = 0;
            while (cursor < line.Length)
            {
                if (tokenStringDepth > 0)
                {
                    var current = line[cursor];
                    MaskAt(cursor++);
                    if (current == '{')
                        tokenStringDepth++;
                    else if (current == '}')
                        tokenStringDepth--;
                    continue;
                }

                if (blockCommentDepth > 0)
                {
                    if (StartsWith(line, cursor, "/+"))
                    {
                        blockCommentDepth++;
                        cursor += 2;
                        continue;
                    }

                    if (StartsWith(line, cursor, "+/"))
                    {
                        blockCommentDepth--;
                        cursor += 2;
                        continue;
                    }

                    cursor++;
                    continue;
                }

                if (quote != '\0')
                {
                    if (line[cursor] == '\\' && cursor + 1 < line.Length)
                    {
                        cursor += 2;
                        continue;
                    }

                    if (line[cursor] == quote)
                        quote = '\0';
                    cursor++;
                    continue;
                }

                if (StartsWith(line, cursor, "//"))
                    break;

                if (StartsWith(line, cursor, "/+"))
                {
                    blockCommentDepth++;
                    cursor += 2;
                    continue;
                }

                if (line[cursor] is '"' or '\'' or '`')
                {
                    quote = line[cursor++];
                    continue;
                }

                if (StartsWith(line, cursor, "q{")
                    && (cursor == 0 || !IsIdentifierChar(line[cursor - 1])))
                {
                    MaskAt(cursor++);
                    MaskAt(cursor++);
                    tokenStringDepth = 1;
                    continue;
                }

                cursor++;
            }

            result[lineIndex] = chars is null ? line : new string(chars);
        }

        return result;
    }

    private static string[] MaskNestedBlockComments(
        string[] lines,
        string opening,
        string closing,
        string lineComment,
        bool singleQuoteCanBePostfix = false)
    {
        if (!MayContain(lines, opening))
            return lines;

        var result = new string[lines.Length];
        var depth = 0;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            char[]? chars = null;
            var quote = '\0';

            void MaskAt(int index) =>
                (chars ??= line.ToCharArray())[index] = ' ';

            var cursor = 0;
            while (cursor < line.Length)
            {
                if (depth > 0)
                {
                    if (StartsWith(line, cursor, opening))
                    {
                        MaskToken(opening);
                        depth++;
                        continue;
                    }

                    if (StartsWith(line, cursor, closing))
                    {
                        MaskToken(closing);
                        depth--;
                        continue;
                    }

                    MaskAt(cursor++);
                    continue;
                }

                if (quote != '\0')
                {
                    if (line[cursor] == '\\' && cursor + 1 < line.Length)
                    {
                        cursor += 2;
                        continue;
                    }

                    if (line[cursor] == quote)
                        quote = '\0';
                    cursor++;
                    continue;
                }

                if (line[cursor] is '"' or '\'' or '`')
                {
                    if (line[cursor] == '\''
                        && singleQuoteCanBePostfix
                        && IsPostfixSingleQuote(line, cursor))
                    {
                        cursor++;
                        continue;
                    }

                    quote = line[cursor++];
                    continue;
                }

                if (StartsWith(line, cursor, opening))
                {
                    MaskToken(opening);
                    depth++;
                    continue;
                }

                if (StartsWith(line, cursor, lineComment))
                    break;

                cursor++;
            }

            result[lineIndex] = chars is null ? line : new string(chars);

            void MaskToken(string token)
            {
                for (var tokenIndex = 0; tokenIndex < token.Length; tokenIndex++)
                    MaskAt(cursor++);
            }
        }

        return result;
    }

    private static bool IsPostfixSingleQuote(
        string line,
        int quoteIndex,
        bool skipWhitespace = true)
    {
        for (var index = quoteIndex - 1; index >= 0; index--)
        {
            if (skipWhitespace && char.IsWhiteSpace(line[index]))
                continue;

            return char.IsLetterOrDigit(line[index]) || line[index] is '_' or ')' or ']' or '}';
        }

        return false;
    }

    private static string[] MaskMatlabBlockComments(string[] lines)
    {
        if (!MayContain(lines, "%{"))
            return lines;

        var result = new string[lines.Length];
        var inBlockComment = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            var opens = !inBlockComment && trimmed.Equals("%{", StringComparison.Ordinal);
            if (opens)
                inBlockComment = true;

            if (!inBlockComment)
            {
                result[index] = line;
                continue;
            }

            result[index] = new string(' ', line.Length);
            if (trimmed.Equals("%}", StringComparison.Ordinal))
                inBlockComment = false;
        }

        return result;
    }

    private static bool MayContain(IEnumerable<string> lines, string token)
    {
        foreach (var line in lines)
        {
            if (line.Contains(token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool StartsWith(string line, int start, string value) =>
        start + value.Length <= line.Length
        && line.AsSpan(start, value.Length).SequenceEqual(value);

    private static bool IsIdentifierChar(char value) =>
        char.IsLetterOrDigit(value) || value == '_';
}
