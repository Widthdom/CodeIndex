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
        string language,
        int? openingTokenLineIndex = null)
    {
        var tokenRegex = language == "julia"
            ? JuliaScientificBlockTokenRegex
            : MatlabScientificBlockTokenRegex;
        var depth = 1;
        int? bodyStartLine = null;
        var declarationColumn = GetFirstNonWhitespaceColumn(scannerLines[startIndex]);

        for (var lineIndex = startIndex; lineIndex < scannerLines.Length; lineIndex++)
        {
            var code = scannerLines[lineIndex];
            if (string.IsNullOrWhiteSpace(code))
                continue;

            var skipDeclarationToken = lineIndex == (openingTokenLineIndex ?? startIndex);
            var matches = tokenRegex.Matches(code);
            if (language == "matlab"
                && lineIndex > startIndex
                && depth == 1
                && IsMatlabPeerDeclaration(
                    scannerLines,
                    lineIndex,
                    code,
                    matches,
                    declarationColumn))
            {
                return bodyStartLine == null
                    ? (lineIndex, null, null)
                    : (lineIndex, bodyStartLine, lineIndex);
            }

            if (!skipDeclarationToken)
                bodyStartLine ??= lineIndex + 1;

            foreach (Match match in matches)
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

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindJuliaShortFunctionRange(
        string[] scannerLines,
        int startIndex)
    {
        if (!TryGetJuliaShortBlockExpressionStartLine(scannerLines, startIndex, out var blockStartLine))
            return (startIndex + 1, startIndex + 1, startIndex + 1);

        var blockRange = FindScientificEndRange(
            scannerLines,
            startIndex,
            "julia",
            openingTokenLineIndex: blockStartLine);
        return blockRange.BodyStartLine == null
            ? (startIndex + 1, startIndex + 1, startIndex + 1)
            : (blockRange.EndLine, startIndex + 1, blockRange.BodyEndLine);
    }

    private static bool TryGetJuliaShortBlockExpressionStartLine(
        string[] scannerLines,
        int startIndex,
        out int blockStartLine)
    {
        blockStartLine = startIndex;
        var startLine = scannerLines[startIndex];
        var parameterEnd = startLine.IndexOf(')');
        var assignmentIndex = parameterEnd >= 0
            ? startLine.IndexOf('=', parameterEnd + 1)
            : -1;
        if (assignmentIndex < 0)
            return false;

        var expression = startLine[(assignmentIndex + 1)..];
        if (string.IsNullOrWhiteSpace(expression))
        {
            for (var lineIndex = startIndex + 1; lineIndex < scannerLines.Length; lineIndex++)
            {
                expression = scannerLines[lineIndex];
                if (!string.IsNullOrWhiteSpace(expression))
                {
                    blockStartLine = lineIndex;
                    break;
                }
            }
        }

        foreach (Match match in JuliaScientificBlockTokenRegex.Matches(expression))
        {
            var keyword = match.Groups["keyword"].Value;
            if (IsJuliaExpressionPositionBlockOpener(expression, match.Index, keyword)
                || (keyword == "for" && IsJuliaShortForBlockStart(expression, match.Index)))
                return true;
        }

        return false;
    }

    private static bool IsMatlabPeerDeclaration(
        string[] scannerLines,
        int lineIndex,
        string code,
        MatchCollection matches,
        int declarationColumn)
    {
        foreach (Match match in matches)
        {
            if (match.Index > declarationColumn)
                return false;

            var keyword = match.Groups["keyword"].Value;
            if (!keyword.Equals("function", StringComparison.OrdinalIgnoreCase)
                && !keyword.Equals("classdef", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !HasMatlabExplicitOuterClosure(scannerLines, lineIndex);
        }

        return false;
    }

    private static bool HasMatlabExplicitOuterClosure(string[] scannerLines, int nestedDeclarationLineIndex)
    {
        var depth = 1;
        for (var lineIndex = nestedDeclarationLineIndex; lineIndex < scannerLines.Length; lineIndex++)
        {
            var code = scannerLines[lineIndex];
            var skipNestedDeclaration = lineIndex == nestedDeclarationLineIndex;
            foreach (Match match in MatlabScientificBlockTokenRegex.Matches(code))
            {
                if (skipNestedDeclaration)
                {
                    skipNestedDeclaration = false;
                    depth++;
                    continue;
                }

                var keyword = match.Groups["keyword"].Value;
                if (!IsScientificBlockTokenAtStatementBoundary(code, match.Index, keyword, "matlab"))
                    continue;

                if (keyword.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    depth--;
                    if (depth == 0)
                        return true;
                }
                else
                {
                    depth++;
                }
            }
        }

        return false;
    }

    private static int GetFirstNonWhitespaceColumn(string line)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (!char.IsWhiteSpace(line[index]))
                return index;
        }

        return 0;
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
        if (language == "julia" && IsJuliaExpressionPositionBlockOpener(line, tokenIndex, keyword))
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

    private static bool IsJuliaExpressionPositionBlockOpener(
        string line,
        int tokenIndex,
        string keyword)
    {
        if (keyword is "begin" or "do" or "function" or "let" or "quote" or "try" or "while")
            return true;
        if (keyword != "if")
            return false;

        var index = tokenIndex - 1;
        while (index >= 0 && char.IsWhiteSpace(line[index]))
            index--;
        if (index < 0 || line[index] is '(' or ',' or '=' or ';')
            return true;

        var tokenEnd = index + 1;
        while (index >= 0 && (char.IsLetterOrDigit(line[index]) || line[index] == '_'))
            index--;
        var precedingToken = line.AsSpan(index + 1, tokenEnd - index - 1);
        return precedingToken.Equals("else", StringComparison.Ordinal)
            || precedingToken.Equals("return", StringComparison.Ordinal);
    }

    private static bool IsJuliaShortForBlockStart(string expression, int tokenIndex)
    {
        for (var index = 0; index < tokenIndex; index++)
        {
            if (!char.IsWhiteSpace(expression[index]) && expression[index] != '(')
                return false;
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
