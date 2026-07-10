namespace CodeIndex.Indexer;

internal static class SolidityLanguageSupport
{
    internal const string IdentifierPattern = @"[A-Za-z_$][A-Za-z0-9_$]*";

    internal static string[] MaskCommentsAndStrings(string[] lines)
    {
        string[]? masked = null;
        var inBlockComment = false;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var maskedLine = MaskCommentsAndStringsLine(lines[lineIndex], ref inBlockComment);
            if (masked != null)
            {
                masked[lineIndex] = maskedLine;
                continue;
            }

            if (ReferenceEquals(maskedLine, lines[lineIndex]))
                continue;

            masked = (string[])lines.Clone();
            masked[lineIndex] = maskedLine;
        }

        return masked ?? lines;
    }

    private static string MaskCommentsAndStringsLine(string line, ref bool inBlockComment)
    {
        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= line.ToCharArray())[index] = ' ';

        void MaskRange(int start, int length)
        {
            var masked = chars ??= line.ToCharArray();
            for (var index = start; index < start + length; index++)
                masked[index] = ' ';
        }

        void MaskToEnd(int start)
        {
            var masked = chars ??= line.ToCharArray();
            for (var index = start; index < line.Length; index++)
                masked[index] = ' ';
        }

        var i = 0;

        while (i < line.Length)
        {
            if (inBlockComment)
            {
                if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                {
                    MaskRange(i, 2);
                    i += 2;
                    inBlockComment = false;
                }
                else
                {
                    MaskAt(i);
                    i++;
                }

                continue;
            }

            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            {
                MaskToEnd(i);
                break;
            }

            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
            {
                MaskRange(i, 2);
                i += 2;
                inBlockComment = true;
                continue;
            }

            if (line[i] is '"' or '\'')
            {
                var quote = line[i];
                MaskAt(i);
                i++;

                while (i < line.Length)
                {
                    var current = line[i];
                    MaskAt(i);
                    i++;

                    if (current == '\\' && i < line.Length)
                    {
                        MaskAt(i);
                        i++;
                        continue;
                    }

                    if (current == quote)
                        break;
                }

                continue;
            }

            i++;
        }

        return chars is null ? line : new string(chars);
    }
}
