namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static string[] MaskSassStylusBlockCommentLines(string language, string[] originalLines)
    {
        var maskedLines = new string[originalLines.Length];
        if (language == "sass")
        {
            var state = new CssReferenceExtractor.SassLoudCommentState();
            for (var i = 0; i < originalLines.Length; i++)
                maskedLines[i] = CssReferenceExtractor.MaskSassBlockCommentLine(originalLines[i], state);
            return maskedLines;
        }

        var inBlockComment = false;
        for (var i = 0; i < originalLines.Length; i++)
            maskedLines[i] = CssReferenceExtractor.MaskSassStylusBlockCommentLine(originalLines[i], ref inBlockComment);
        return maskedLines;
    }

    private static bool[] FindCssQualifiedRuleAncestors(string[] lines)
    {
        var ancestors = new bool[lines.Length];
        var contexts = new Stack<CssContextKind>();

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            ancestors[lineIndex] = contexts.Contains(CssContextKind.QualifiedRule);
            var line = lines[lineIndex];
            var segmentStart = 0;
            for (int cursor = 0; cursor < line.Length; cursor++)
            {
                var ch = line[cursor];
                if (ch == '{')
                {
                    var segment = line[segmentStart..cursor].Trim();
                    var contextKind = segment.StartsWith("@", StringComparison.Ordinal)
                        ? CssContextKind.GroupingAtRule
                        : CssContextKind.QualifiedRule;
                    contexts.Push(contextKind);
                    segmentStart = cursor + 1;
                }
                else if (ch == '}' && contexts.Count > 0)
                {
                    contexts.Pop();
                    segmentStart = cursor + 1;
                }
                else if (ch == ';')
                {
                    segmentStart = cursor + 1;
                }
            }
        }

        return ancestors;
    }

    private static string[] MaskCssScannerLines(string[] originalLines)
        => MaskCssScannerLines(originalLines, 0, originalLines.Length);

    private static string[] MaskCssScannerLines(IReadOnlyList<string> originalLines, int startIndex, int lineCount)
    {
        var start = Math.Max(0, startIndex);
        var end = Math.Min(originalLines.Count, start + Math.Max(0, lineCount));
        var maskedLines = new string[end - start];
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inUrlToken = false;
        var urlParenDepth = 0;

        for (int lineIndex = start; lineIndex < end; lineIndex++)
            maskedLines[lineIndex - start] = MaskCssScannerLine(
                originalLines[lineIndex],
                ref inBlockComment,
                ref inSingleQuote,
                ref inDoubleQuote,
                ref inUrlToken,
                ref urlParenDepth);

        return maskedLines;
    }

    private static string MaskCssScannerLine(string line)
    {
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inUrlToken = false;
        var urlParenDepth = 0;
        return MaskCssScannerLine(
            line,
            ref inBlockComment,
            ref inSingleQuote,
            ref inDoubleQuote,
            ref inUrlToken,
            ref urlParenDepth);
    }

    private static string MaskCssScannerLine(
        string line,
        ref bool inBlockComment,
        ref bool inSingleQuote,
        ref bool inDoubleQuote,
        ref bool inUrlToken,
        ref int urlParenDepth)
    {
        var chars = line.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (inBlockComment)
            {
                chars[i] = ' ';
                if (i + 1 < chars.Length && line[i] == '*' && line[i + 1] == '/')
                {
                    chars[i + 1] = ' ';
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && i + 1 < chars.Length && line[i] == '/' && line[i + 1] == '*')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                inBlockComment = true;
                i++;
                continue;
            }

            if (inUrlToken)
            {
                chars[i] = ' ';

                if (line[i] == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (line[i] == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inDoubleQuote;
                    continue;
                }

                if ((inSingleQuote || inDoubleQuote) && line[i] == '\\' && i + 1 < chars.Length)
                {
                    chars[i + 1] = ' ';
                    i++;
                    continue;
                }

                if (!inSingleQuote && !inDoubleQuote)
                {
                    if (line[i] == '(')
                        urlParenDepth++;
                    else if (line[i] == ')')
                    {
                        urlParenDepth--;
                        if (urlParenDepth <= 0)
                        {
                            inUrlToken = false;
                            urlParenDepth = 0;
                        }
                    }
                }

                continue;
            }

            if (!inSingleQuote
                && !inDoubleQuote
                && !inUrlToken
                && i + 3 < chars.Length
                && (line[i] == 'u' || line[i] == 'U')
                && (line[i + 1] == 'r' || line[i + 1] == 'R')
                && (line[i + 2] == 'l' || line[i + 2] == 'L')
                && line[i + 3] == '(')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                chars[i + 2] = ' ';
                chars[i + 3] = ' ';
                inUrlToken = true;
                urlParenDepth = 1;
                i += 3;
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && !inUrlToken && i + 1 < chars.Length && line[i] == '/' && line[i + 1] == '/')
            {
                for (int j = i; j < chars.Length; j++)
                    chars[j] = ' ';

                break;
            }

            if ((inSingleQuote || inDoubleQuote) && line[i] == '\\' && i + 1 < chars.Length)
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                continue;
            }

            if (line[i] == '"' && !inSingleQuote)
            {
                chars[i] = ' ';
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (line[i] == '\'' && !inDoubleQuote)
            {
                chars[i] = ' ';
                inSingleQuote = !inDoubleQuote;
                continue;
            }

            if (inSingleQuote || inDoubleQuote)
                chars[i] = ' ';
        }

        return new string(chars);
    }
}
