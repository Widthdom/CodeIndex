using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex PascalBeginRegex = new(@"\bbegin\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PascalEndRegex = new(@"\bend\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PascalNestedEndBlockStartRegex = new(@"\b(?:case|try)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PascalRoutineStartRegex = new(@"^\s*(?:(?:class|static)\s+)?(?:procedure|function|constructor|destructor)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PascalRangeBoundaryRegex = new(@"^\s*(?:interface|implementation|initialization|finalization)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindPascalRange(string[] lines, int startIndex)
    {
        var opened = false;
        var depth = 0;
        int? bodyStartLine = null;
        var inBraceComment = false;
        var inParenStarComment = false;

        for (var i = startIndex + 1; i < lines.Length; i++)
        {
            var code = StripPascalRangeComments(MaskPascalRangeStrings(lines[i]), ref inBraceComment, ref inParenStarComment);
            var trimmed = code.Trim();
            if (trimmed.Length == 0)
                continue;

            if (!opened)
            {
                if (PascalRoutineStartRegex.IsMatch(trimmed) || PascalRangeBoundaryRegex.IsMatch(trimmed))
                    return (startIndex + 1, null, null);

                var beginCount = PascalBeginRegex.Matches(code).Count;
                if (beginCount == 0)
                    continue;

                opened = true;
                bodyStartLine = i + 1;
                depth += beginCount;
                depth -= PascalEndRegex.Matches(code).Count;
                if (depth <= 0)
                    return (i + 1, bodyStartLine, i + 1);
                continue;
            }

            depth += CountPascalRangeBlockStarts(code);
            depth -= PascalEndRegex.Matches(code).Count;
            if (depth <= 0)
                return (i + 1, bodyStartLine, i + 1);
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lines.Length, bodyStartLine, lines.Length);
    }

    private static int CountPascalRangeBlockStarts(string code) =>
        PascalBeginRegex.Matches(code).Count + PascalNestedEndBlockStartRegex.Matches(code).Count;

    private static string MaskPascalRangeStrings(string line)
    {
        var chars = line.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] != '\'')
                continue;

            chars[i] = ' ';
            i++;
            while (i < chars.Length)
            {
                if (chars[i] == '\'' && i + 1 < chars.Length && chars[i + 1] == '\'')
                {
                    chars[i++] = ' ';
                    chars[i] = ' ';
                    i++;
                    continue;
                }

                var closes = chars[i] == '\'';
                chars[i] = ' ';
                if (closes)
                    break;
                i++;
            }
        }

        return new string(chars);
    }

    private static string StripPascalRangeComments(string line, ref bool inBraceComment, ref bool inParenStarComment)
    {
        var chars = line.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (inBraceComment)
            {
                chars[i] = ' ';
                if (line[i] == '}')
                    inBraceComment = false;
                continue;
            }

            if (inParenStarComment)
            {
                chars[i] = ' ';
                if (line[i] == '*' && i + 1 < chars.Length && line[i + 1] == ')')
                {
                    chars[++i] = ' ';
                    inParenStarComment = false;
                }
                continue;
            }

            if (line[i] == '/' && i + 1 < chars.Length && line[i + 1] == '/')
            {
                for (; i < chars.Length; i++)
                    chars[i] = ' ';
                break;
            }

            if (line[i] == '{')
            {
                chars[i] = ' ';
                inBraceComment = true;
                continue;
            }

            if (line[i] == '(' && i + 1 < chars.Length && line[i + 1] == '*')
            {
                chars[i++] = ' ';
                chars[i] = ' ';
                inParenStarComment = true;
            }
        }

        return new string(chars);
    }
}
