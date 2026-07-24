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

        for (var lineIndex = startIndex; lineIndex < scannerLines.Length; lineIndex++)
        {
            var code = scannerLines[lineIndex];
            if (string.IsNullOrWhiteSpace(code))
                continue;

            var skipDeclarationToken = lineIndex == startIndex;
            if (!skipDeclarationToken)
                bodyStartLine ??= lineIndex + 1;

            foreach (Match match in tokenRegex.Matches(code))
            {
                if (skipDeclarationToken)
                {
                    skipDeclarationToken = false;
                    continue;
                }

                var keyword = match.Groups["keyword"].Value;
                if (!IsScientificBlockTokenAtStatementBoundary(code, match.Index, keyword, language))
                    continue;

                if (keyword.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    depth--;
                    if (depth == 0)
                        return (lineIndex + 1, bodyStartLine ?? lineIndex + 1, lineIndex + 1);
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
            {
                return IsScientificStatementSeparatorAtTopLevel(line, index);
            }

            return language == "julia"
                && (line[index] == '=' || IsJuliaMacroBlockPrefix(line, tokenIndex));
        }

        return true;
    }

    private static bool IsScientificStatementSeparatorAtTopLevel(string line, int separatorIndex)
    {
        var parentheses = 0;
        var brackets = 0;
        var braces = 0;
        for (var index = 0; index < separatorIndex; index++)
        {
            switch (line[index])
            {
                case '(':
                    parentheses++;
                    break;
                case ')':
                    parentheses = Math.Max(0, parentheses - 1);
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets = Math.Max(0, brackets - 1);
                    break;
                case '{':
                    braces++;
                    break;
                case '}':
                    braces = Math.Max(0, braces - 1);
                    break;
            }
        }

        return parentheses == 0 && brackets == 0 && braces == 0;
    }

    private static bool IsJuliaMacroBlockPrefix(string line, int tokenIndex)
    {
        var index = tokenIndex - 1;
        while (index >= 0 && char.IsWhiteSpace(line[index]))
            index--;
        if (index < 0)
            return false;

        var tokenEnd = index + 1;
        while (index >= 0
            && (char.IsLetterOrDigit(line[index]) || line[index] is '_' or '.' or '@'))
        {
            index--;
        }

        var token = line.AsSpan(index + 1, tokenEnd - index - 1);
        var atIndex = token.LastIndexOf('@');
        if (atIndex < 0 || atIndex == token.Length - 1)
            return false;

        for (var tokenIndexOffset = 0; tokenIndexOffset < token.Length; tokenIndexOffset++)
        {
            if (!(char.IsLetterOrDigit(token[tokenIndexOffset])
                || token[tokenIndexOffset] is '_' or '.' or '@'))
            {
                return false;
            }
        }

        return true;
    }
}
