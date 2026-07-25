namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindShellFunctionRange(string[] lines, int startIndex, int startColumn)
    {
        var depth = 0;
        var opened = false;
        int? bodyStartLine = null;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = startIndex; i < lines.Length; i++)
        {
            var scanLine = i == startIndex && startColumn > 0 && startColumn < lines[i].Length
                ? lines[i][startColumn..]
                : i == startIndex && startColumn >= lines[i].Length
                    ? string.Empty
                    : lines[i];

            var closeColumn = ScanShellBraceLine(
                scanLine,
                ref depth,
                ref opened,
                ref bodyStartLine,
                i + 1,
                ref inSingleQuote,
                ref inDoubleQuote);
            if (closeColumn >= 0)
                return (i + 1, bodyStartLine, i + 1);
        }

        if (!opened)
            return (startIndex + 1, null, null);

        var boundedEndLine = bodyStartLine.HasValue
            ? Math.Max(startIndex + 1, bodyStartLine.Value)
            : startIndex + 1;
        return (boundedEndLine, bodyStartLine, boundedEndLine);
    }

    private static int FindShellSameLineBraceEndColumn(string line, int startColumn)
    {
        var depth = 0;
        var opened = false;
        int? bodyStartLine = null;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        return ScanShellBraceLine(
            startColumn > 0 && startColumn < line.Length
                ? line[startColumn..]
                : startColumn >= line.Length
                    ? string.Empty
                    : line,
            ref depth,
            ref opened,
            ref bodyStartLine,
            1,
            ref inSingleQuote,
            ref inDoubleQuote) is var relativeCloseColumn && relativeCloseColumn >= 0
                ? startColumn + relativeCloseColumn
                : -1;
    }

    private static int ScanShellBraceLine(
        string line,
        ref int depth,
        ref bool opened,
        ref int? bodyStartLine,
        int currentLine,
        ref bool inSingleQuote,
        ref bool inDoubleQuote)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inSingleQuote)
            {
                if (c == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    i++;
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
                inSingleQuote = true;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                continue;
            }

            if (c == '#' && IsShellCommentStart(line, i))
                break;

            if (c == '{')
            {
                depth++;
                if (!opened)
                {
                    opened = true;
                    bodyStartLine = currentLine;
                }
            }
            else if (c == '}' && opened)
            {
                depth--;
                if (depth <= 0)
                    return i;
            }
        }

        return -1;
    }

    private static bool IsShellCommentStart(string line, int index)
    {
        if (index == 0)
            return true;

        var previous = line[index - 1];
        return char.IsWhiteSpace(previous) || previous is ';' or '|' or '&' or '(' or '{';
    }
}
