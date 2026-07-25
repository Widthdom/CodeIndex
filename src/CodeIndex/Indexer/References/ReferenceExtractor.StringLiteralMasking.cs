namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool IsPythonStringPrefixChar(char c) =>
        c is 'r' or 'R' or 'u' or 'U' or 'b' or 'B' or 'f' or 'F';

    private static string MaskJavaTextBlocks(string content)
    {
        var firstTextBlockCandidate = content.IndexOf("\"\"\"", StringComparison.Ordinal);
        if (firstTextBlockCandidate < 0)
            return content;

        var chars = content.ToCharArray();

        for (var i = firstTextBlockCandidate; i + 2 < chars.Length; i++)
        {
            if (!IsJavaTextBlockOpening(chars, i))
                continue;

            // Mask the body but keep line breaks so all existing line/column logic stays valid.
            i += 3;
            while (i < chars.Length)
            {
                if (i + 2 < chars.Length
                    && chars[i] == '"'
                    && chars[i + 1] == '"'
                    && chars[i + 2] == '"'
                    && !IsEscapedByBackslashes(chars, i))
                {
                    i += 2;
                    break;
                }

                if (chars[i] != '\r' && chars[i] != '\n')
                    chars[i] = ' ';
                i++;
            }
        }

        return new string(chars);
    }

    private static bool IsJavaTextBlockOpening(IReadOnlyList<char> chars, int index)
    {
        if (index + 2 >= chars.Count)
            return false;

        if (chars[index] != '"' || chars[index + 1] != '"' || chars[index + 2] != '"')
            return false;

        if (IsEscapedByBackslashes(chars, index))
            return false;

        for (var i = index + 3; i < chars.Count; i++)
        {
            var c = chars[i];
            if (c == '\r' || c == '\n')
                return true;
            if (!char.IsWhiteSpace(c))
                return false;
        }

        return true;
    }

    private static bool IsEscapedByBackslashes(IReadOnlyList<char> chars, int index)
    {
        var backslashCount = 0;
        for (var i = index - 1; i >= 0 && chars[i] == '\\'; i--)
            backslashCount++;

        return (backslashCount & 1) == 1;
    }
}
