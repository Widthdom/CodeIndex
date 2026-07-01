using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static List<(string Name, int StartColumn)>? TryExpandFortranEnumeratorDeclaratorList(
        string patternMatchLine,
        Match match)
    {
        if (!match.Groups["name"].Success || !match.Groups["enumTail"].Success)
            return null;

        var listStart = match.Groups["name"].Index;
        var listEnd = match.Groups["enumTail"].Index + match.Groups["enumTail"].Length;
        if (listStart < 0 || listStart >= patternMatchLine.Length || listEnd <= listStart)
            return null;
        if (listEnd > patternMatchLine.Length)
            listEnd = patternMatchLine.Length;

        var list = patternMatchLine[listStart..listEnd];
        var results = new List<(string Name, int StartColumn)>();
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var segment = list.Substring(segmentStart, segmentLength);
            var leading = 0;
            while (leading < segment.Length && char.IsWhiteSpace(segment[leading]))
                leading++;
            if (leading >= segment.Length)
                continue;

            if (segment[leading] != '_' && !char.IsLetter(segment[leading]))
                return null;

            var index = leading + 1;
            while (index < segment.Length && (segment[index] == '_' || char.IsLetterOrDigit(segment[index])))
                index++;

            var name = segment[leading..index];
            if (name.Length == 0)
                return null;

            results.Add((name, listStart + segmentStart + leading));
        }

        return results.Count > 1 ? results : null;
    }

    private static List<(string Name, int StartColumn)>? TryExpandFortranParameterDeclaratorList(
        string patternMatchLine,
        Match match)
    {
        if (!match.Groups["name"].Success || !match.Groups["paramTail"].Success)
            return null;

        var listStart = match.Groups["name"].Index;
        var listEnd = match.Groups["paramTail"].Index + match.Groups["paramTail"].Length;
        if (listStart < 0 || listStart >= patternMatchLine.Length || listEnd <= listStart)
            return null;
        if (listEnd > patternMatchLine.Length)
            listEnd = patternMatchLine.Length;

        var list = patternMatchLine[listStart..listEnd];
        var results = new List<(string Name, int StartColumn)>();
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var segment = list.Substring(segmentStart, segmentLength);
            var leading = 0;
            while (leading < segment.Length && char.IsWhiteSpace(segment[leading]))
                leading++;
            if (leading >= segment.Length)
                continue;

            if (segment[leading] != '_' && !char.IsLetter(segment[leading]))
                return null;

            var index = leading + 1;
            while (index < segment.Length && (segment[index] == '_' || char.IsLetterOrDigit(segment[index])))
                index++;

            var name = segment[leading..index];
            if (name.Length == 0)
                return null;

            results.Add((name, listStart + segmentStart + leading));
        }

        return results.Count > 1 ? results : null;
    }
}
