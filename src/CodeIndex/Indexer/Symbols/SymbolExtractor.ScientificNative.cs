using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex JuliaScientificBlockTokenRegex = new(
        @"\b(?<keyword>baremodule|module|mutable\s+struct|struct|abstract\s+type|primitive\s+type|function|macro|if|for|while|try|begin|let|quote|do|end)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MatlabScientificBlockTokenRegex = new(
        @"\b(?<keyword>function|classdef|methods|properties|events|enumeration|arguments|if|for|parfor|while|switch|try|spmd|end)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindScientificEndRange(
        string[] scannerLines,
        int startIndex,
        string language)
    {
        var tokenRegex = language == "julia"
            ? JuliaScientificBlockTokenRegex
            : MatlabScientificBlockTokenRegex;
        var depth = 1;
        int? bodyStartLine = null;

        for (var lineIndex = startIndex + 1; lineIndex < scannerLines.Length; lineIndex++)
        {
            var code = scannerLines[lineIndex];
            if (string.IsNullOrWhiteSpace(code))
                continue;

            bodyStartLine ??= lineIndex + 1;
            foreach (Match match in tokenRegex.Matches(code))
            {
                var keyword = match.Groups["keyword"].Value;
                if (!IsScientificBlockTokenAtStatementBoundary(code, match.Index, keyword, language))
                    continue;

                if (keyword.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    depth--;
                    if (depth == 0)
                        return (lineIndex + 1, bodyStartLine, lineIndex + 1);
                    continue;
                }

                depth++;
            }
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (scannerLines.Length, bodyStartLine, scannerLines.Length);
    }

    private static string[] PrepareScientificBodyScannerLines(
        string[] lines,
        string language)
    {
        var blockMaskedLines = ScientificNativeCommentMasker.MaskBlockComments(language, lines);
        var scannerLines = new string[blockMaskedLines.Length];
        for (var lineIndex = 0; lineIndex < blockMaskedLines.Length; lineIndex++)
            scannerLines[lineIndex] = MaskScientificBodyScanLine(blockMaskedLines[lineIndex], language);

        return scannerLines;
    }

    private static string MaskScientificBodyScanLine(string line, string language)
    {
        var masked = ScientificNativeCommentMasker.MaskLineStringLiteralsPreservingPostfixSingleQuotes(
            line,
            useMatlabStringRules: language == "matlab");
        var commentMarker = language == "julia" ? '#' : '%';
        var commentIndex = masked.IndexOf(commentMarker);
        return commentIndex >= 0 ? masked[..commentIndex] : masked;
    }

    private static bool IsScientificBlockTokenAtStatementBoundary(
        string line,
        int tokenIndex,
        string keyword,
        string language)
    {
        if (language == "julia" && keyword == "do")
            return true;

        for (var index = tokenIndex - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(line[index]))
                continue;

            if (line[index] == ';'
                || (language == "matlab" && line[index] == ','))
                return true;

            return language == "julia" && line[index] == '=';
        }

        return true;
    }
}
