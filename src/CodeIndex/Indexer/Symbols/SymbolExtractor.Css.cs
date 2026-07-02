using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static int CountNonOverlappingOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            return 0;

        var count = 0;
        var startIndex = 0;
        while (startIndex < text.Length)
        {
            var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            count++;
            startIndex = index + value.Length;
        }

        return count;
    }

    private static void ExtractCssInlineGroupingSelectors(
        long fileId,
        string rawLine,
        string maskedLine,
        string[] cssScannerLines,
        int lineIndex,
        IReadOnlyList<SymbolPattern> patterns,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        var groupingDepth = 0;
        var qualifiedDepth = 0;
        var segmentStart = 0;

        for (int i = 0; i < maskedLine.Length; i++)
        {
            var ch = maskedLine[i];
            if (ch == ';')
            {
                if (groupingDepth == 0 && qualifiedDepth == 0)
                    TryAddCssLayerListSymbols(fileId, rawLine[segmentStart..i], maskedLine[segmentStart..i], lineIndex, symbols, cssSeenSymbols);

                segmentStart = i + 1;
                continue;
            }

            if (ch == '{')
            {
                var maskedSegment = maskedLine[segmentStart..i].Trim();
                var rawSegment = rawLine[segmentStart..i].Trim();
                var isGroupingAtRule = maskedSegment.StartsWith('@');

                if (isGroupingAtRule)
                    groupingDepth++;
                else
                    qualifiedDepth++;

                segmentStart = i + 1;
                continue;
            }

            if (ch == '}')
            {
                if (qualifiedDepth > 0)
                    qualifiedDepth--;
                else if (groupingDepth > 0)
                    groupingDepth--;

                segmentStart = i + 1;
            }
        }
    }

    private static void TryAddCssInlineSelectorSegment(
        long fileId,
        string rawSegment,
        string maskedSegment,
        string[] cssScannerLines,
        int lineIndex,
        int openingBraceIndex,
        IReadOnlyList<SymbolPattern> patterns,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        if (string.IsNullOrWhiteSpace(maskedSegment))
            return;

        var matchLine = $"{rawSegment} {{";
        foreach (var pattern in patterns)
        {
            if (pattern.BodyStyle != BodyStyle.Brace)
                continue;

            var match = pattern.Regex.Match(matchLine);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Success
                ? match.Groups["name"].Value.Trim()
                : match.Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            var (endLine, bodyStartLine, bodyEndLine) = FindBraceRange(cssScannerLines, lineIndex, openingBraceIndex);
            var startLine = lineIndex + 1;
            AddSymbolRecord(
                symbols,
                cssSeenSymbols,
                startLine,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = pattern.Kind,
                    Name = name,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = Math.Max(startLine, endLine),
                    BodyStartLine = bodyStartLine,
                    BodyEndLine = bodyEndLine,
                    Signature = rawSegment.Length > 0 ? $"{rawSegment} {{" : "{",
                });
            return;
        }
    }

    private static void TryAddCssSelectorListSegments(
        long fileId,
        string rawSegment,
        string maskedSegment,
        string[] cssScannerLines,
        int lineIndex,
        int openingBraceIndex,
        IReadOnlyList<SymbolPattern> patterns,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        foreach (var (rawPart, maskedPart) in EnumerateCssCommaSeparatedSegments(rawSegment, maskedSegment))
        {
            TryAddCssInlineSelectorSegment(
                fileId,
                rawPart,
                maskedPart,
                cssScannerLines,
                lineIndex,
                openingBraceIndex,
                patterns,
                symbols,
                cssSeenSymbols);
        }
    }

    private static void TryAddCssLayerListSymbols(
        long fileId,
        string rawSegment,
        string maskedSegment,
        int lineIndex,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        var trimmedMaskedSegment = maskedSegment.TrimStart();
        if (!trimmedMaskedSegment.StartsWith("@layer", StringComparison.OrdinalIgnoreCase))
            return;

        var trimmedRawSegment = rawSegment.Trim();
        if (trimmedRawSegment.Length == 0)
            return;

        const string atLayerPrefix = "@layer";
        if (trimmedRawSegment.Length <= atLayerPrefix.Length)
            return;

        var rawNames = trimmedRawSegment[atLayerPrefix.Length..].Trim();
        var maskedNames = trimmedMaskedSegment[atLayerPrefix.Length..].Trim();
        if (rawNames.Length == 0 || maskedNames.Length == 0)
            return;

        foreach (var (rawName, maskedName) in EnumerateCssCommaSeparatedSegments(rawNames, maskedNames))
        {
            var name = rawName.Trim();
            if (name.Length == 0 || maskedName.Length == 0)
                continue;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols,
                lineIndex + 1,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "namespace",
                    Name = name,
                    Line = lineIndex + 1,
                    StartLine = lineIndex + 1,
                    EndLine = lineIndex + 1,
                    Signature = trimmedRawSegment,
                });
        }
    }

    private static IEnumerable<(string Raw, string Masked)> EnumerateCssCommaSeparatedSegments(string rawText, string maskedText)
    {
        var segmentStart = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var index = 0; index < maskedText.Length; index++)
        {
            var ch = maskedText[index];
            if (ch == '(')
            {
                parenDepth++;
                continue;
            }

            if (ch == ')' && parenDepth > 0)
            {
                parenDepth--;
                continue;
            }

            if (ch == '[')
            {
                bracketDepth++;
                continue;
            }

            if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }

            if (ch == ',' && parenDepth == 0 && bracketDepth == 0)
            {
                yield return (rawText[segmentStart..index].Trim(), maskedText[segmentStart..index].Trim());
                segmentStart = index + 1;
            }
        }

        yield return (rawText[segmentStart..].Trim(), maskedText[segmentStart..].Trim());
    }

    private static int FindCssSameLineBraceEndColumn(string line, int startColumn)
    {
        var maskedLine = MaskCssScannerLine(line);
        var depth = 0;
        var opened = false;

        for (var index = Math.Max(0, startColumn); index < maskedLine.Length; index++)
        {
            var ch = maskedLine[index];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }

        return -1;
    }

    private static readonly Regex CssFontFaceDeclarationRegex = new(@"(?:^|[;{])\s*font-family\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CssInlineCustomPropertyRegex = new(@"(?<name>--[\w-]+)\s*:", RegexOptions.Compiled);
    private static readonly Regex CssMediaFeatureNameRegex = new(@"^\s*(?:not\s+)?(?<name>--[\w-]+|[A-Za-z_][\w-]*)(?=\s*(?::|[<>]=?|=|$))|[<>]=?\s*(?<name>--[\w-]+|[A-Za-z_][\w-]*)(?=\s*(?:[<>]=?|$))", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string ResolveCssSymbolName(string matchLine, string name, string[] lines, int startIndex, int endLine)
    {
        if (!matchLine.TrimStart().StartsWith("@font-face", StringComparison.OrdinalIgnoreCase))
            return name;

        return TryGetCssFontFaceFamilyName(lines, startIndex, endLine, out var fontFamily)
            ? fontFamily
            : string.Empty;
    }

    private static void TryAddCssMediaFeatureSymbols(
        long fileId,
        string rawLine,
        string maskedLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        var trimmedMaskedLine = maskedLine.TrimStart();
        if (!trimmedMaskedLine.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
            return;

        var blockStart = maskedLine.IndexOf('{');
        if (blockStart < 0)
            blockStart = maskedLine.Length;

        var query = maskedLine[..blockStart];
        for (var index = 0; index < query.Length; index++)
        {
            if (query[index] != '(')
                continue;

            var featureStart = index + 1;
            var depth = 1;
            index++;
            while (index < query.Length && depth > 0)
            {
                if (query[index] == '(')
                    depth++;
                else if (query[index] == ')')
                    depth--;

                index++;
            }

            if (depth != 0)
                break;

            var featureText = query[featureStart..(index - 1)];
            if (string.IsNullOrWhiteSpace(featureText))
                continue;

            var match = CssMediaFeatureNameRegex.Match(featureText);
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                if (string.Equals(name, "and", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "or", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "not", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var featureColumn = featureStart + match.Groups["name"].Index;
                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols,
                    lineIndex + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "property",
                        Name = name,
                        Line = lineIndex + 1,
                        StartLine = lineIndex + 1,
                        StartColumn = featureColumn,
                        EndLine = lineIndex + 1,
                        Signature = rawLine.Trim(),
                    });
            }
        }
    }

    private static bool TryGetCssFontFaceFamilyName(string[] lines, int startIndex, int endLine, out string fontFamily)
    {
        fontFamily = string.Empty;
        var blockLineCount = Math.Max(1, endLine - startIndex);
        if (!CssBlockContains(lines, startIndex, blockLineCount, "font-family", StringComparison.OrdinalIgnoreCase))
            return false;

        var maskedBlockLines = MaskCssScannerLines(lines, startIndex, blockLineCount);
        var maskedBlockText = JoinCssLineRange(maskedBlockLines, 0, maskedBlockLines.Length);
        var match = CssFontFaceDeclarationRegex.Match(maskedBlockText);
        if (!match.Success)
            return false;

        var valueStart = match.Index + match.Length;
        var valueEnd = valueStart;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        while (valueEnd < maskedBlockText.Length)
        {
            var ch = maskedBlockText[valueEnd];
            if (ch == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (ch == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;
            else if (!inSingleQuote && !inDoubleQuote && ch is ';' or '}')
                break;

            valueEnd++;
        }

        if (valueEnd == valueStart)
            return false;

        var rawBlockText = JoinCssLineRange(lines, startIndex, blockLineCount);
        var rawName = valueEnd <= rawBlockText.Length
            ? rawBlockText[valueStart..valueEnd]
            : rawBlockText[valueStart..];
        rawName = RemoveCssBlockComments(rawName).Trim();
        if (rawName.Length == 0)
            return false;

        if ((rawName[0] == '"' && rawName[^1] == '"') || (rawName[0] == '\'' && rawName[^1] == '\''))
            rawName = rawName[1..^1].Trim();

        if (rawName.Length == 0)
            return false;

        fontFamily = rawName;
        return true;
    }

    private static string JoinCssLineRange(IReadOnlyList<string> lines, int startIndex, int lineCount)
    {
        var start = Math.Max(0, startIndex);
        var end = Math.Min(lines.Count, start + Math.Max(0, lineCount));
        var count = end - start;
        if (count <= 0)
            return string.Empty;

        if (count == 1)
            return lines[start];

        var capacity = count - 1;
        for (var index = start; index < end; index++)
            capacity += lines[index].Length;

        var builder = new StringBuilder(capacity);
        builder.Append(lines[start]);
        for (var index = start + 1; index < end; index++)
        {
            builder.Append('\n');
            builder.Append(lines[index]);
        }

        return builder.ToString();
    }

    private static bool CssBlockContains(
        IReadOnlyList<string> lines,
        int startIndex,
        int lineCount,
        string value,
        StringComparison comparison)
    {
        var endIndex = Math.Min(lines.Count, startIndex + lineCount);
        for (var index = Math.Max(0, startIndex); index < endIndex; index++)
        {
            if (lines[index].IndexOf(value, comparison) >= 0)
                return true;
        }

        return false;
    }

    private static string RemoveCssBlockComments(string value)
    {
        if (value.Length == 0)
            return value;

        if (!value.Contains("/*", StringComparison.Ordinal))
            return value;

        var builder = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (i + 1 < value.Length && value[i] == '/' && value[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < value.Length && !(value[i] == '*' && value[i + 1] == '/'))
                    i++;

                if (i + 1 < value.Length)
                    i++;

                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static bool ShouldSkipCssNestedSelectorCandidate(
        string? lang,
        SymbolPattern pattern,
        string matchLine,
        bool[]? cssQualifiedRuleAncestors,
        int lineIndex) =>
        lang == "css"
        && cssQualifiedRuleAncestors != null
        && cssQualifiedRuleAncestors[lineIndex]
        && pattern.Kind == "class"
        && !matchLine.TrimStart().StartsWith('@');

}
