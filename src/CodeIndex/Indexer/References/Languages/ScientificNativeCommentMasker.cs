namespace CodeIndex.Indexer;

internal static class ScientificNativeCommentMasker
{
    internal static string[] MaskBlockComments(string language, string[] lines) =>
        language switch
        {
            "d" => MaskDNonCodeRegions(lines),
            "julia" => MaskNestedBlockComments(
                MaskTripleQuotedStrings(
                    lines,
                    "#=",
                    "=#",
                    "#",
                    singleQuoteCanBePostfix: true,
                    tripleQuoteUsesBackslashEscapes: true,
                    maskMultilineBacktickStrings: true),
                "#=",
                "=#",
                "#",
                singleQuoteCanBePostfix: true),
            "nim" => MaskNimNonCodeRegions(lines),
            "matlab" => MaskMatlabBlockComments(lines),
            _ => lines,
        };

    internal static string MaskLineStringLiteralsPreservingPostfixSingleQuotes(
        string line,
        bool useMatlabStringRules,
        bool useBackslashEscapes)
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
                if (useBackslashEscapes
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
        string lineComment,
        bool singleQuoteCanBePostfix = false,
        bool tripleQuoteUsesBackslashEscapes = false,
        bool maskMultilineBacktickStrings = false)
    {
        if (!MayContain(lines, "\"\"\"")
            && (!maskMultilineBacktickStrings || !MayContain(lines, "`")))
        {
            return lines;
        }

        var result = new string[lines.Length];
        var inTripleQuotedString = false;
        var inBacktickString = false;
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
                if (inBacktickString)
                {
                    var current = line[cursor];
                    MaskAt(cursor++);
                    if (current == '`' && !HasOddBackslashPrefix(line, cursor - 1))
                        inBacktickString = false;
                    continue;
                }

                if (inTripleQuotedString)
                {
                    if (StartsWith(line, cursor, "\"\"\"")
                        && (!tripleQuoteUsesBackslashEscapes
                            || !HasOddBackslashPrefix(line, cursor)))
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

                if (StartsWith(line, cursor, blockOpening))
                {
                    blockCommentDepth++;
                    cursor += blockOpening.Length;
                    continue;
                }

                if (StartsWith(line, cursor, lineComment))
                    break;

                if (StartsWith(line, cursor, "\"\"\""))
                {
                    MaskToken("\"\"\"");
                    inTripleQuotedString = true;
                    continue;
                }

                if (maskMultilineBacktickStrings && line[cursor] == '`')
                {
                    MaskAt(cursor++);
                    inBacktickString = true;
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

    private static bool HasOddBackslashPrefix(string line, int index)
    {
        var backslashCount = 0;
        for (index--; index >= 0 && line[index] == '\\'; index--)
            backslashCount++;

        return (backslashCount & 1) != 0;
    }

    internal static string MaskNimRawStringLiterals(string line)
    {
        if (!line.Contains("r\"", StringComparison.Ordinal))
            return line;

        char[]? chars = null;
        for (var cursor = 0; cursor + 1 < line.Length; cursor++)
        {
            if (line[cursor] != 'r'
                || line[cursor + 1] != '"'
                || (cursor > 0 && IsIdentifierChar(line[cursor - 1])))
            {
                continue;
            }

            chars ??= line.ToCharArray();
            chars[cursor++] = ' ';
            chars[cursor++] = ' ';
            while (cursor < line.Length)
            {
                var current = line[cursor];
                chars[cursor++] = ' ';
                if (current != '"')
                    continue;

                if (cursor < line.Length && line[cursor] == '"')
                {
                    chars[cursor++] = ' ';
                    continue;
                }

                break;
            }

            cursor--;
        }

        return chars is null ? line : new string(chars);
    }

    private static string[] MaskNimNonCodeRegions(string[] lines)
    {
        string[]? rawStringMaskedLines = null;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var maskedLine = MaskNimRawStringLiterals(lines[lineIndex]);
            if (ReferenceEquals(maskedLine, lines[lineIndex]))
                continue;

            rawStringMaskedLines ??= (string[])lines.Clone();
            rawStringMaskedLines[lineIndex] = maskedLine;
        }

        var preparedLines = rawStringMaskedLines ?? lines;
        return MaskNestedBlockComments(
            MaskTripleQuotedStrings(preparedLines, "#[", "]#", "#"),
            "#[",
            "]#",
            "#");
    }

    private static string[] MaskDNonCodeRegions(string[] lines)
    {
        var result = new string[lines.Length];
        var tokenStringDepth = 0;
        var nestedCommentDepth = 0;
        var inCStyleBlockComment = false;
        var inBacktickString = false;
        string? tokenStringClosing = null;
        var tokenStringQuote = '\0';
        var tokenStringQuoteUsesEscapes = false;
        var tokenStringInBacktickString = false;
        var tokenStringNestedCommentDepth = 0;
        var tokenStringInCStyleBlockComment = false;
        string? nestedTokenStringClosing = null;
        var quote = '\0';
        var quoteUsesEscapes = false;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            char[]? chars = null;

            void MaskAt(int index) =>
                (chars ??= line.ToCharArray())[index] = ' ';

            var cursor = 0;
            while (cursor < line.Length)
            {
                if (inBacktickString)
                {
                    var current = line[cursor];
                    MaskAt(cursor++);
                    if (current == '`')
                        inBacktickString = false;
                    continue;
                }

                if (tokenStringClosing != null)
                {
                    if (StartsWith(line, cursor, tokenStringClosing))
                    {
                        MaskToken(tokenStringClosing);
                        tokenStringClosing = null;
                        continue;
                    }

                    MaskAt(cursor++);
                    continue;
                }

                if (tokenStringDepth > 0)
                {
                    if (nestedTokenStringClosing != null)
                    {
                        if (StartsWith(line, cursor, nestedTokenStringClosing))
                        {
                            MaskToken(nestedTokenStringClosing);
                            nestedTokenStringClosing = null;
                            continue;
                        }

                        MaskAt(cursor++);
                        continue;
                    }

                    if (tokenStringInBacktickString)
                    {
                        var backtickCurrent = line[cursor];
                        MaskAt(cursor++);
                        if (backtickCurrent == '`')
                            tokenStringInBacktickString = false;
                        continue;
                    }

                    if (tokenStringNestedCommentDepth > 0)
                    {
                        if (StartsWith(line, cursor, "/+"))
                        {
                            MaskToken("/+");
                            tokenStringNestedCommentDepth++;
                            continue;
                        }

                        if (StartsWith(line, cursor, "+/"))
                        {
                            MaskToken("+/");
                            tokenStringNestedCommentDepth--;
                            continue;
                        }

                        MaskAt(cursor++);
                        continue;
                    }

                    if (tokenStringInCStyleBlockComment)
                    {
                        if (StartsWith(line, cursor, "*/"))
                        {
                            MaskToken("*/");
                            tokenStringInCStyleBlockComment = false;
                            continue;
                        }

                        MaskAt(cursor++);
                        continue;
                    }

                    if (tokenStringQuote != '\0')
                    {
                        var quotedCurrent = line[cursor];
                        MaskAt(cursor++);
                        if (tokenStringQuoteUsesEscapes
                            && quotedCurrent == '\\'
                            && cursor < line.Length)
                        {
                            MaskAt(cursor++);
                            continue;
                        }

                        if (quotedCurrent == tokenStringQuote)
                            tokenStringQuote = '\0';
                        continue;
                    }

                    if (StartsWith(line, cursor, "//"))
                    {
                        while (cursor < line.Length)
                            MaskAt(cursor++);
                        break;
                    }

                    if (StartsWith(line, cursor, "/*"))
                    {
                        MaskToken("/*");
                        tokenStringInCStyleBlockComment = true;
                        continue;
                    }

                    if (StartsWith(line, cursor, "/+"))
                    {
                        MaskToken("/+");
                        tokenStringNestedCommentDepth++;
                        continue;
                    }

                    if (StartsWith(line, cursor, "q\"")
                        && (cursor == 0 || !IsIdentifierChar(line[cursor - 1]))
                        && TryGetDTokenStringClosing(
                            line,
                            cursor + 2,
                            out var nestedOpeningLength,
                            out var nestedClosing))
                    {
                        for (var openingIndex = 0; openingIndex < 2 + nestedOpeningLength; openingIndex++)
                            MaskAt(cursor++);
                        nestedTokenStringClosing = nestedClosing;
                        continue;
                    }

                    if (line[cursor] == '`')
                    {
                        MaskAt(cursor++);
                        tokenStringInBacktickString = true;
                        continue;
                    }

                    if (StartsWith(line, cursor, "r\"")
                        && (cursor == 0 || !IsIdentifierChar(line[cursor - 1])))
                    {
                        MaskAt(cursor++);
                        MaskAt(cursor++);
                        tokenStringQuote = '"';
                        tokenStringQuoteUsesEscapes = false;
                        continue;
                    }

                    if (line[cursor] is '"' or '\'')
                    {
                        tokenStringQuote = line[cursor];
                        tokenStringQuoteUsesEscapes = true;
                        MaskAt(cursor++);
                        continue;
                    }

                    var structuralCurrent = line[cursor];
                    MaskAt(cursor++);
                    if (structuralCurrent == '{')
                        tokenStringDepth++;
                    else if (structuralCurrent == '}')
                        tokenStringDepth--;
                    continue;
                }

                if (nestedCommentDepth > 0)
                {
                    if (StartsWith(line, cursor, "/+"))
                    {
                        MaskToken("/+");
                        nestedCommentDepth++;
                        continue;
                    }

                    if (StartsWith(line, cursor, "+/"))
                    {
                        MaskToken("+/");
                        nestedCommentDepth--;
                        continue;
                    }

                    MaskAt(cursor++);
                    continue;
                }

                if (inCStyleBlockComment)
                {
                    if (StartsWith(line, cursor, "*/"))
                    {
                        MaskToken("*/");
                        inCStyleBlockComment = false;
                        continue;
                    }

                    MaskAt(cursor++);
                    continue;
                }

                if (quote != '\0')
                {
                    var current = line[cursor];
                    MaskAt(cursor++);
                    if (quoteUsesEscapes && current == '\\' && cursor < line.Length)
                    {
                        MaskAt(cursor++);
                        continue;
                    }

                    if (current == quote)
                        quote = '\0';
                    continue;
                }

                if (StartsWith(line, cursor, "//"))
                {
                    while (cursor < line.Length)
                        MaskAt(cursor++);
                    break;
                }

                if (StartsWith(line, cursor, "/*"))
                {
                    MaskToken("/*");
                    inCStyleBlockComment = true;
                    continue;
                }

                if (StartsWith(line, cursor, "/+"))
                {
                    MaskToken("/+");
                    nestedCommentDepth++;
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

                if (StartsWith(line, cursor, "q\"")
                    && (cursor == 0 || !IsIdentifierChar(line[cursor - 1]))
                    && TryGetDTokenStringClosing(line, cursor + 2, out var openingLength, out var closing))
                {
                    for (var openingIndex = 0; openingIndex < 2 + openingLength; openingIndex++)
                        MaskAt(cursor++);
                    tokenStringClosing = closing;
                    continue;
                }

                if (line[cursor] == '`')
                {
                    MaskAt(cursor++);
                    inBacktickString = true;
                    continue;
                }

                if (StartsWith(line, cursor, "r\"")
                    && (cursor == 0 || !IsIdentifierChar(line[cursor - 1])))
                {
                    MaskAt(cursor++);
                    MaskAt(cursor++);
                    quote = '"';
                    quoteUsesEscapes = false;
                    continue;
                }

                if (line[cursor] is '"' or '\'')
                {
                    quote = line[cursor];
                    quoteUsesEscapes = true;
                    MaskAt(cursor++);
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

    private static bool TryGetDTokenStringClosing(
        string line,
        int delimiterIndex,
        out int openingLength,
        out string closing)
    {
        openingLength = 0;
        closing = string.Empty;
        if (delimiterIndex >= line.Length)
            return false;

        closing = line[delimiterIndex] switch
        {
            '[' => "]\"",
            '(' => ")\"",
            '{' => "}\"",
            '<' => ">\"",
            _ => string.Empty,
        };
        if (closing.Length != 0)
        {
            openingLength = 1;
            return true;
        }

        var end = delimiterIndex;
        while (end < line.Length && IsIdentifierChar(line[end]))
            end++;
        if (end == delimiterIndex
            || (end < line.Length && !char.IsWhiteSpace(line[end])))
        {
            return false;
        }

        openingLength = end - delimiterIndex;
        closing = line[delimiterIndex..end] + '"';
        return true;
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

            if (char.IsLetterOrDigit(line[index]) || line[index] is '_' or ')' or ']' or '}')
                return true;

            return line[index] == '.'
                && index > 0
                && (char.IsLetterOrDigit(line[index - 1])
                    || line[index - 1] is '_' or ')' or ']' or '}');
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
