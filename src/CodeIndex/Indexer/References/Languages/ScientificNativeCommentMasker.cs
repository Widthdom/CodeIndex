namespace CodeIndex.Indexer;

internal static class ScientificNativeCommentMasker
{
    internal static string[] MaskBlockComments(string language, string[] lines) =>
        language switch
        {
            "d" => MaskNestedBlockComments(lines, "/+", "+/", "//"),
            "julia" => MaskNestedBlockComments(lines, "#=", "=#", "#", singleQuoteCanBePostfix: true),
            "nim" => MaskNestedBlockComments(lines, "#[", "]#", "#"),
            "matlab" => MaskMatlabBlockComments(lines),
            _ => lines,
        };

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

    private static bool IsPostfixSingleQuote(string line, int quoteIndex)
    {
        for (var index = quoteIndex - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(line[index]))
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
}
