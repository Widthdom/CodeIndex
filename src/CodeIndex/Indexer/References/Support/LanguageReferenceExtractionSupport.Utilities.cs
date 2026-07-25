using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private static void EmitVbGenericConstraintReferences(
        string list,
        int listStart,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var ignoredSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "As", "Class", "New", "Structure",
        };

        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var segment = list.Substring(segmentStart, segmentLength);
            var match = VbGenericConstraintRegex.Match(segment);
            if (match.Success)
            {
                ignoredSegments.Add(match.Groups["param"].Value);
                ignoredSegments.Add(NormalizeVbIdentifierSegment(match.Groups["param"].Value));
            }
        }

        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var segment = list.Substring(segmentStart, segmentLength);
            var match = VbGenericConstraintRegex.Match(segment);
            if (!match.Success)
                continue;

            var constraintGroup = match.Groups["constraint"];
            // The generic-list regex is shallow; skip nested constraints rather than emit type parameters as concrete types.
            if (constraintGroup.Value.Contains("(Of", StringComparison.OrdinalIgnoreCase))
                continue;

            var absoluteConstraintStart = listStart + segmentStart + constraintGroup.Index;
            ReferenceExtractor.AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                constraintGroup.Value,
                absoluteConstraintStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteConstraintStart),
                "vb",
                ignoredSegments);
        }
    }

    private static string StripCppAccessPrefix(string value)
    {
        var text = value.Trim();
        bool removed;
        do
        {
            removed = false;
            foreach (var prefix in CppAccessPrefixes)
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    text = text[prefix.Length..].TrimStart();
                    removed = true;
                }
            }
        } while (removed);

        return text;
    }

    private static string LastCppQualifiedSegment(string value)
    {
        var text = value.Trim();
        var genericIndex = text.IndexOf('<');
        if (genericIndex >= 0)
            text = text[..genericIndex].TrimEnd();
        var separator = text.LastIndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? text[(separator + 2)..].Trim() : text;
    }

    private static bool ContainsAsciiUppercase(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= 'A' and <= 'Z')
                return true;
        }

        return false;
    }

    private static bool IsCppTemplateDeclarationOrSpecializationLine(string line, int matchIndex)
    {
        var prefix = line[..Math.Clamp(matchIndex, 0, line.Length)].TrimStart();
        return prefix.StartsWith("template", StringComparison.Ordinal)
            || prefix.StartsWith("export template", StringComparison.Ordinal);
    }

    private static string LastQualifiedSegment(string value)
    {
        var dot = value.LastIndexOf('.');
        return dot >= 0 && dot + 1 < value.Length ? value[(dot + 1)..] : value;
    }

    private static string NormalizeVbIdentifierSegment(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            return trimmed[1..^1];

        return trimmed;
    }

    private static string LastPathSegment(string value)
    {
        var slash = value.LastIndexOf('/');
        return slash >= 0 && slash + 1 < value.Length ? value[(slash + 1)..] : value;
    }

    private static int LastWhitespaceSeparatedTokenStart(string value)
    {
        var end = value.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(value[end]))
            end--;
        if (end < 0)
            return -1;

        var start = end;
        while (start >= 0 && !char.IsWhiteSpace(value[start]))
            start--;
        return start + 1;
    }

    private static IEnumerable<Match> EnumerateMatches(Regex regex, string input)
    {
        foreach (Match match in BoundedRegex.EnumerateMatches(regex, input))
            yield return match;
    }

    private static void MaskRange(char[] chars, int start, int end)
    {
        for (var i = start; i < end && i < chars.Length; i++)
            chars[i] = ' ';
    }

    private static int SkipWhitespace(string line, int start)
    {
        while (start < line.Length && char.IsWhiteSpace(line[start]))
            start++;
        return start;
    }

    private static bool IsIdentifierStart(char ch) =>
        ch == '_' || char.IsLetter(ch);
}
