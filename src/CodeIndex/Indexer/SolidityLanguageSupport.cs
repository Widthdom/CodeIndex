using System.Text;

namespace CodeIndex.Indexer;

internal static class SolidityLanguageSupport
{
    internal const string IdentifierPattern = @"[A-Za-z_$][A-Za-z0-9_$]*";

    internal static string[] MaskCommentsAndStrings(string[] lines)
    {
        var masked = new string[lines.Length];
        var inBlockComment = false;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var builder = new StringBuilder(line.Length);
            var i = 0;

            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                    {
                        builder.Append("  ");
                        i += 2;
                        inBlockComment = false;
                    }
                    else
                    {
                        builder.Append(' ');
                        i++;
                    }

                    continue;
                }

                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                {
                    builder.Append(' ', line.Length - i);
                    break;
                }

                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                {
                    builder.Append("  ");
                    i += 2;
                    inBlockComment = true;
                    continue;
                }

                if (line[i] is '"' or '\'')
                {
                    var quote = line[i];
                    builder.Append(' ');
                    i++;

                    while (i < line.Length)
                    {
                        var current = line[i];
                        builder.Append(' ');
                        i++;

                        if (current == '\\' && i < line.Length)
                        {
                            builder.Append(' ');
                            i++;
                            continue;
                        }

                        if (current == quote)
                            break;
                    }

                    continue;
                }

                builder.Append(line[i]);
                i++;
            }

            masked[lineIndex] = builder.ToString();
        }

        return masked;
    }
}
