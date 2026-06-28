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
        var maskedLine = MaskCssScannerLines([line])[0];
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
        var blockLines = lines.Skip(startIndex).Take(Math.Max(1, endLine - startIndex)).ToArray();
        var maskedBlockText = string.Join('\n', MaskCssScannerLines(blockLines));
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

        var rawBlockText = string.Join('\n', blockLines);
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

    private static string RemoveCssBlockComments(string value)
    {
        if (value.Length == 0)
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
    {
        var maskedLines = new string[originalLines.Length];
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inUrlToken = false;
        var urlParenDepth = 0;

        for (int lineIndex = 0; lineIndex < originalLines.Length; lineIndex++)
        {
            var line = originalLines[lineIndex];
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
                        inSingleQuote = !inSingleQuote;
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
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (inSingleQuote || inDoubleQuote)
                    chars[i] = ' ';
            }

            maskedLines[lineIndex] = new string(chars);
        }

        return maskedLines;
    }

}
