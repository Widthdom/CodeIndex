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
        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= line.ToCharArray())[index] = ' ';

        void MaskRange(int start)
        {
            var masked = chars ??= line.ToCharArray();
            for (int index = start; index < line.Length; index++)
                masked[index] = ' ';
        }

        for (int i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                MaskAt(i);
                if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                {
                    MaskAt(i + 1);
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
            {
                MaskAt(i);
                MaskAt(i + 1);
                inBlockComment = true;
                i++;
                continue;
            }

            if (inUrlToken)
            {
                MaskAt(i);

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

                if ((inSingleQuote || inDoubleQuote) && line[i] == '\\' && i + 1 < line.Length)
                {
                    MaskAt(i + 1);
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
                && i + 3 < line.Length
                && (line[i] == 'u' || line[i] == 'U')
                && (line[i + 1] == 'r' || line[i + 1] == 'R')
                && (line[i + 2] == 'l' || line[i + 2] == 'L')
                && line[i + 3] == '(')
            {
                MaskAt(i);
                MaskAt(i + 1);
                MaskAt(i + 2);
                MaskAt(i + 3);
                inUrlToken = true;
                urlParenDepth = 1;
                i += 3;
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && !inUrlToken && i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            {
                MaskRange(i);
                break;
            }

            if ((inSingleQuote || inDoubleQuote) && line[i] == '\\' && i + 1 < line.Length)
            {
                MaskAt(i);
                MaskAt(i + 1);
                i++;
                continue;
            }

            if (line[i] == '"' && !inSingleQuote)
            {
                MaskAt(i);
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (line[i] == '\'' && !inDoubleQuote)
            {
                MaskAt(i);
                inSingleQuote = !inDoubleQuote;
                continue;
            }

            if (inSingleQuote || inDoubleQuote)
                MaskAt(i);
        }

        return chars is null ? line : new string(chars);
    }
}
