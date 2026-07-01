using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static List<(string Name, int StartColumn, string? ReturnType)>? TryExpandSwiftEnumCaseDeclaratorList(
        string patternMatchLine,
        int absoluteStartColumn,
        Match match)
    {
        if (!match.Groups["caseTail"].Success || !match.Groups["name"].Success)
            return null;

        var listStart = match.Groups["name"].Index;
        var listEnd = absoluteStartColumn + match.Length;
        if (listStart < 0 || listStart >= patternMatchLine.Length || listEnd <= listStart)
            return null;
        if (listEnd > patternMatchLine.Length)
            listEnd = patternMatchLine.Length;

        var list = patternMatchLine[listStart..listEnd];
        var results = new List<(string Name, int StartColumn, string? ReturnType)>();
        foreach (var (segmentStart, segmentLength) in SplitSwiftEnumCaseSegments(list))
        {
            var segment = list.Substring(segmentStart, segmentLength);
            var leading = 0;
            while (leading < segment.Length && char.IsWhiteSpace(segment[leading]))
                leading++;
            if (leading >= segment.Length)
                return null;

            var nameStart = leading;
            if (segment[nameStart] != '_' && !char.IsLetter(segment[nameStart]))
                return null;

            var index = nameStart + 1;
            while (index < segment.Length && (segment[index] == '_' || char.IsLetterOrDigit(segment[index])))
                index++;

            var name = segment[nameStart..index];
            if (name.Length == 0)
                return null;

            if (!TryReadSwiftEnumCaseRawValue(segment, index, out var rawValue))
                return null;

            results.Add((name, listStart + segmentStart + nameStart, rawValue));
        }

        return results.Count > 1 ? results : null;
    }

    private static List<(int Start, int Length)> SplitSwiftEnumCaseSegments(string text)
    {
        var spans = new List<(int Start, int Length)>();
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        var braceDepth = 0;
        var start = 0;
        var inString = false;
        var escaped = false;

        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            switch (ch)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case ',' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    spans.Add((start, index - start));
                    start = index + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    private static bool TryReadSwiftEnumCaseRawValue(string segment, int afterName, out string? rawValue)
    {
        rawValue = null;

        var index = SkipWhitespace(segment, afterName);
        if (index < segment.Length && segment[index] == '(')
        {
            var closeParen = ReferenceExtractor.FindMatchingChar(segment, index, '(', ')');
            if (closeParen < 0)
                return false;
            index = closeParen + 1;
        }

        index = SkipWhitespace(segment, index);
        if (index >= segment.Length)
            return true;
        if (segment[index] != '=')
            return false;

        var valueStart = SkipWhitespace(segment, index + 1);
        var valueEnd = segment.Length;
        while (valueEnd > valueStart && char.IsWhiteSpace(segment[valueEnd - 1]))
            valueEnd--;

        rawValue = valueEnd > valueStart ? segment[valueStart..valueEnd] : null;
        return true;
    }
}
