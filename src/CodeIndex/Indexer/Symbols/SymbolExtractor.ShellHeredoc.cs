using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex ShellHeredocRedirectRegex = new(
        @"(?<!<)<<-?(?!<)\s*(?:""(?<dq>[^""]+)""|'(?<sq>[^']+)'|\\?(?<bare>[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly record struct ShellHeredocTerminator(string Delimiter, bool StripLeadingTabs);

    private static string[] MaskShellHeredocLines(string[] lines)
    {
        var maskedLines = (string[])lines.Clone();
        var pendingTerminators = new Queue<ShellHeredocTerminator>();
        ShellHeredocTerminator? activeTerminator = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (activeTerminator is { } terminator)
            {
                maskedLines[i] = string.Empty;
                if (ShellHeredocTerminatorMatches(line, terminator))
                {
                    activeTerminator = pendingTerminators.Count > 0
                        ? pendingTerminators.Dequeue()
                        : null;
                }

                continue;
            }

            foreach (var heredocTerminator in EnumerateShellHeredocTerminators(line))
                pendingTerminators.Enqueue(heredocTerminator);

            if (pendingTerminators.Count > 0)
                activeTerminator = pendingTerminators.Dequeue();
        }

        return maskedLines;
    }

    private static bool ShellHeredocTerminatorMatches(string line, ShellHeredocTerminator terminator)
    {
        var start = 0;
        if (terminator.StripLeadingTabs)
        {
            while (start < line.Length && line[start] == '\t')
                start++;
        }

        var end = line.Length;
        while (end > start && line[end - 1] == '\r')
            end--;

        var length = end - start;
        return length == terminator.Delimiter.Length
            && string.CompareOrdinal(line, start, terminator.Delimiter, 0, length) == 0;
    }

    private static IEnumerable<ShellHeredocTerminator> EnumerateShellHeredocTerminators(string line)
    {
        if (line.IndexOf("<<", StringComparison.Ordinal) < 0)
            yield break;

        var ignored = BuildShellIgnoredCharacterMask(line);
        foreach (Match match in ShellHeredocRedirectRegex.Matches(line))
        {
            if (match.Index < ignored.Length && ignored[match.Index])
                continue;

            var delimiter = match.Groups["dq"].Success
                ? match.Groups["dq"].Value
                : match.Groups["sq"].Success
                    ? match.Groups["sq"].Value
                    : match.Groups["bare"].Value;
            if (delimiter.Length == 0)
                continue;

            var stripLeadingTabs = match.Index + 2 < line.Length && line[match.Index + 2] == '-';
            yield return new ShellHeredocTerminator(delimiter, stripLeadingTabs);
        }
    }

    private static bool[] BuildShellIgnoredCharacterMask(string line)
    {
        var ignored = new bool[line.Length];
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inSingleQuote)
            {
                ignored[i] = true;
                if (c == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                ignored[i] = true;
                if (c == '\\' && i + 1 < line.Length)
                {
                    ignored[++i] = true;
                    continue;
                }

                if (c == '"')
                    inDoubleQuote = false;
                continue;
            }

            if (c == '\\' && i + 1 < line.Length)
            {
                i++;
                continue;
            }

            if (c == '\'')
            {
                ignored[i] = true;
                inSingleQuote = true;
                continue;
            }

            if (c == '"')
            {
                ignored[i] = true;
                inDoubleQuote = true;
                continue;
            }

            if (c == '#' && IsShellCommentStart(line, i))
            {
                Array.Fill(ignored, true, i, line.Length - i);
                break;
            }
        }

        return ignored;
    }
}
