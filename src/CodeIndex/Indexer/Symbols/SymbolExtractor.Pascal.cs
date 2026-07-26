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
    private static readonly Regex AdaRangeDeclarationNameRegex = new(
        @"^\s*(?:(?:overriding|not\s+overriding)\s+)?(?:(?:package\s+(?:body\s+)?)|(?:function|procedure)\s+(?:(?:[A-Za-z]\w*)\.)*|(?:task|protected)\s+(?:type\s+)?)(?<name>[A-Za-z]\w*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdaNamedEndRegex = new(
        @"\bend\s+(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdaBeginRegex = new(
        @"\bbegin\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdaUnnamedOuterEndRegex = new(
        @"\bend\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdaBodylessAfterIsRegex = new(
        @"^(?:abstract|separate|null|new)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdaRoutineBodySignatureRegex = new(
        @"^\s*(?:(?:overriding|not\s+overriding)\s+)?(?:procedure\b[\s\S]*?\bis\b|function\b[\s\S]*?\breturn\b[\s\S]*?\bis\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindAdaRange(
        string[] lines,
        int startIndex)
    {
        var declaration = AdaRangeDeclarationNameRegex.Match(lines[startIndex]);
        if (!declaration.Success)
            return (startIndex + 1, null, null);

        if (TryFindAdaBodylessDeclarationEnd(lines, startIndex, out var declarationEndLine))
            return (declarationEndLine, null, null);

        var declarationName = declaration.Groups["name"].Value;
        int? bodyStartLine = null;
        for (var i = startIndex; i < lines.Length; i++)
        {
            var code = MaskAdaRangeStringsAndComments(lines[i]);
            if (bodyStartLine == null && AdaBeginRegex.IsMatch(code))
                bodyStartLine = i + 1;

            foreach (Match endMatch in Regex.EnumerateMatches(AdaNamedEndRegex, code))
            {
                var endName = endMatch.Groups["name"].Value;
                var endLeaf = endName[(endName.LastIndexOf('.') + 1)..];
                if (string.Equals(endLeaf, declarationName, StringComparison.OrdinalIgnoreCase))
                    return (i + 1, bodyStartLine, i + 1);
            }
        }

        if (bodyStartLine == null)
            return (startIndex + 1, null, null);

        var beginDepth = 0;
        for (var i = bodyStartLine.Value - 1; i < lines.Length; i++)
        {
            var code = MaskAdaRangeStringsAndComments(lines[i]);
            beginDepth += Regex.CountMatches(AdaBeginRegex, code);
            foreach (Match _ in Regex.EnumerateMatches(AdaUnnamedOuterEndRegex, code))
            {
                if (beginDepth > 0)
                    beginDepth--;
                if (beginDepth == 0)
                    return (i + 1, bodyStartLine, i + 1);
            }
        }

        return (lines.Length, bodyStartLine, lines.Length);
    }

    private static bool TryFindAdaBodylessDeclarationEnd(
        string[] lines,
        int startIndex,
        out int declarationEndLine)
    {
        declarationEndLine = startIndex + 1;
        var delimiterDepth = 0;
        for (var lineIndex = startIndex; lineIndex < lines.Length; lineIndex++)
        {
            var code = MaskAdaRangeStringsAndComments(lines[lineIndex]);
            for (var index = 0; index < code.Length; index++)
            {
                if (code[index] is '(' or '[')
                {
                    delimiterDepth++;
                    continue;
                }
                if (code[index] is ')' or ']')
                {
                    delimiterDepth = Math.Max(0, delimiterDepth - 1);
                    continue;
                }
                if (delimiterDepth != 0)
                    continue;

                if (code[index] == ';')
                {
                    declarationEndLine = lineIndex + 1;
                    return true;
                }

                if (index + 2 > code.Length
                    || !code.AsSpan(index, 2).Equals("is", StringComparison.OrdinalIgnoreCase)
                    || (index > 0 && (char.IsLetterOrDigit(code[index - 1]) || code[index - 1] == '_'))
                    || (index + 2 < code.Length
                        && (char.IsLetterOrDigit(code[index + 2]) || code[index + 2] == '_')))
                {
                    continue;
                }

                var tail = code[(index + 2)..].TrimStart();
                for (var tailLineIndex = lineIndex + 1;
                     tail.Length == 0 && tailLineIndex < lines.Length;
                     tailLineIndex++)
                {
                    tail = MaskAdaRangeStringsAndComments(lines[tailLineIndex]).TrimStart();
                }

                return tail.StartsWith('(') || AdaBodylessAfterIsRegex.IsMatch(tail);
            }
        }

        return false;
    }

    private static string MaskAdaRangeStringsAndComments(string line)
    {
        char[]? chars = null;
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (inString)
            {
                chars![i] = ' ';
                if (line[i] != '"')
                    continue;
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    chars[++i] = ' ';
                    continue;
                }

                inString = false;
                continue;
            }

            if (line[i] == '"')
            {
                chars ??= line.ToCharArray();
                chars[i] = ' ';
                inString = true;
                continue;
            }

            if (line[i] == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                chars ??= line.ToCharArray();
                for (; i < line.Length; i++)
                    chars[i] = ' ';
                break;
            }
        }

        return chars == null ? line : new string(chars);
    }

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

                var beginCount = CountPascalBeginTokens(code);
                if (beginCount == 0)
                    continue;

                opened = true;
                bodyStartLine = i + 1;
                depth += beginCount;
                depth -= CountPascalEndTokens(code);
                if (depth <= 0)
                    return (i + 1, bodyStartLine, i + 1);
                continue;
            }

            depth += CountPascalRangeBlockStarts(code);
            depth -= CountPascalEndTokens(code);
            if (depth <= 0)
                return (i + 1, bodyStartLine, i + 1);
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lines.Length, bodyStartLine, lines.Length);
    }

    private static int CountPascalBeginTokens(string code) =>
        code.Contains("begin", StringComparison.OrdinalIgnoreCase)
            ? Regex.CountMatches(PascalBeginRegex, code)
            : 0;

    private static int CountPascalEndTokens(string code) =>
        code.Contains("end", StringComparison.OrdinalIgnoreCase)
            ? Regex.CountMatches(PascalEndRegex, code)
            : 0;

    private static int CountPascalRangeBlockStarts(string code)
    {
        var count = CountPascalBeginTokens(code);
        if (code.Contains("case", StringComparison.OrdinalIgnoreCase)
            || code.Contains("try", StringComparison.OrdinalIgnoreCase))
        {
            count += Regex.CountMatches(PascalNestedEndBlockStartRegex, code);
        }

        return count;
    }

    private static string MaskPascalRangeStrings(string line)
    {
        var quoteIndex = line.IndexOf('\'');
        if (quoteIndex < 0)
            return line;

        var chars = line.ToCharArray();
        for (var i = quoteIndex; i < chars.Length; i++)
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
        char[]? chars = inBraceComment || inParenStarComment
            ? line.ToCharArray()
            : null;

        for (var i = 0; i < line.Length; i++)
        {
            if (inBraceComment)
            {
                chars![i] = ' ';
                if (line[i] == '}')
                    inBraceComment = false;
                continue;
            }

            if (inParenStarComment)
            {
                chars![i] = ' ';
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == ')')
                {
                    chars[++i] = ' ';
                    inParenStarComment = false;
                }
                continue;
            }

            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                chars ??= line.ToCharArray();
                for (; i < line.Length; i++)
                    chars[i] = ' ';
                break;
            }

            if (line[i] == '{')
            {
                chars ??= line.ToCharArray();
                chars[i] = ' ';
                inBraceComment = true;
                continue;
            }

            if (line[i] == '(' && i + 1 < line.Length && line[i + 1] == '*')
            {
                chars ??= line.ToCharArray();
                chars[i++] = ' ';
                chars[i] = ' ';
                inParenStarComment = true;
            }
        }

        return chars == null ? line : new string(chars);
    }
}
