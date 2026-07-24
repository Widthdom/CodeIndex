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
        int? openingTokenLineIndex = null,
        bool[]? matlabExplicitOuterClosureByLine = null)
    {
        var tokenRegex = language == "julia"
            ? JuliaScientificBlockTokenRegex
            : MatlabScientificBlockTokenRegex;
        var depth = 0;
        int? bodyStartLine = null;
        var delimiterFrames = new Stack<(char ClosingDelimiter, int BlockDepth)>();

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
                && delimiterFrames.Count == 0
                && IsMatlabPeerDeclaration(
                    lineIndex,
                    code,
                    matches,
                    matlabExplicitOuterClosureByLine))
            {
                return bodyStartLine == null
                    ? (lineIndex, null, null)
                    : (lineIndex, bodyStartLine, lineIndex);
            }

            if (!skipDeclarationToken)
                bodyStartLine ??= lineIndex + 1;

            var delimiterScanIndex = 0;
            foreach (Match match in matches)
            {
                ScanScientificDelimiterFrames(
                    code,
                    delimiterScanIndex,
                    match.Index,
                    depth,
                    delimiterFrames);
                delimiterScanIndex = match.Index + match.Length;

                if (skipDeclarationToken)
                {
                    skipDeclarationToken = false;
                    depth++;
                    continue;
                }

                var keyword = match.Groups["keyword"].Value;
                if (!IsScientificBlockTokenAtStatementBoundary(code, match.Index, keyword, language))
                    continue;

                if (keyword.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    if (delimiterFrames.TryPeek(out var delimiterFrame)
                        && depth <= delimiterFrame.BlockDepth)
                    {
                        continue;
                    }

                    depth--;
                    if (depth == 0)
                        return (lineIndex + 1, bodyStartLine ?? lineIndex + 1, lineIndex + 1);
                    continue;
                }

                // Julia comprehension clauses inside delimiters do not open `end`-terminated
                // blocks. Julia の区切り記号内にある内包表記句は `end` 終端ブロックを開かない。
                if (language == "julia"
                    && delimiterFrames.Count > 0
                    && keyword is "for" or "if")
                {
                    continue;
                }

                depth++;
            }

            ScanScientificDelimiterFrames(
                code,
                delimiterScanIndex,
                code.Length,
                depth,
                delimiterFrames);
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
        {
            if (TryFindJuliaShortDelimitedExpressionEnd(scannerLines, startIndex, out var expressionEndLine))
            {
                return (expressionEndLine, startIndex + 1, expressionEndLine);
            }

            if (TryFindJuliaShortContinuationEnd(scannerLines, startIndex, out expressionEndLine))
            {
                return (expressionEndLine, startIndex + 1, expressionEndLine);
            }

            return (startIndex + 1, startIndex + 1, startIndex + 1);
        }

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

    private static bool TryFindJuliaShortDelimitedExpressionEnd(
        string[] scannerLines,
        int startIndex,
        out int expressionEndLine)
    {
        expressionEndLine = startIndex + 1;
        var startLine = scannerLines[startIndex];
        var parameterEnd = startLine.IndexOf(')');
        var assignmentIndex = parameterEnd >= 0
            ? startLine.IndexOf('=', parameterEnd + 1)
            : -1;
        if (assignmentIndex < 0)
            return false;

        var delimiters = new Stack<char>();
        var sawExpressionContent = false;
        var sawUnclosedDelimiter = false;
        for (var lineIndex = startIndex; lineIndex < scannerLines.Length; lineIndex++)
        {
            var line = scannerLines[lineIndex];
            var cursor = lineIndex == startIndex ? assignmentIndex + 1 : 0;
            for (; cursor < line.Length; cursor++)
            {
                var value = line[cursor];
                if (!char.IsWhiteSpace(value))
                    sawExpressionContent = true;
                var closingDelimiter = value switch
                {
                    '(' => ')',
                    '[' => ']',
                    '{' => '}',
                    _ => '\0',
                };
                if (closingDelimiter != '\0')
                {
                    delimiters.Push(closingDelimiter);
                    continue;
                }

                if (delimiters.TryPeek(out var expectedClosingDelimiter)
                    && value == expectedClosingDelimiter)
                {
                    delimiters.Pop();
                }
            }

            if (delimiters.Count > 0)
            {
                sawUnclosedDelimiter = true;
                continue;
            }

            if (sawUnclosedDelimiter)
            {
                expressionEndLine = lineIndex + 1;
                return true;
            }

            if (sawExpressionContent)
                return false;
        }

        return false;
    }

    private static bool TryFindJuliaShortContinuationEnd(
        string[] scannerLines,
        int startIndex,
        out int expressionEndLine)
    {
        expressionEndLine = startIndex + 1;
        var startLine = scannerLines[startIndex];
        var parameterEnd = startLine.IndexOf(')');
        var assignmentIndex = parameterEnd >= 0
            ? startLine.IndexOf('=', parameterEnd + 1)
            : -1;
        if (assignmentIndex < 0)
        {
            return false;
        }

        var initialExpression = startLine[(assignmentIndex + 1)..];
        if (!string.IsNullOrWhiteSpace(initialExpression)
            && !EndsWithJuliaContinuationOperator(initialExpression))
        {
            return false;
        }

        var declarationColumn = GetFirstNonWhitespaceColumn(startLine);
        for (var lineIndex = startIndex + 1; lineIndex < scannerLines.Length; lineIndex++)
        {
            var line = scannerLines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (GetFirstNonWhitespaceColumn(line) <= declarationColumn)
                return expressionEndLine > startIndex + 1;

            expressionEndLine = lineIndex + 1;
            if (!EndsWithJuliaContinuationOperator(line))
                return true;
        }

        return expressionEndLine > startIndex + 1;
    }

    private static bool EndsWithJuliaContinuationOperator(string expression)
    {
        var trimmed = expression.AsSpan().TrimEnd();
        if (trimmed.IsEmpty)
            return false;

        return trimmed[^1] is '+'
            or '-'
            or '*'
            or '/'
            or '\\'
            or '^'
            or '%'
            or '&'
            or '|'
            or '<'
            or '>'
            or '='
            or '?'
            or ':'
            or ','
            or '÷'
            or '⊻';
    }

    private static bool IsMatlabPeerDeclaration(
        int lineIndex,
        string code,
        MatchCollection matches,
        bool[]? matlabExplicitOuterClosureByLine)
    {
        foreach (Match match in matches)
        {
            var keyword = match.Groups["keyword"].Value;
            if (!keyword.Equals("function", StringComparison.OrdinalIgnoreCase)
                && !keyword.Equals("classdef", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return matlabExplicitOuterClosureByLine == null
                || lineIndex >= matlabExplicitOuterClosureByLine.Length
                || !matlabExplicitOuterClosureByLine[lineIndex];
        }

        return false;
    }

    private static bool[] BuildMatlabExplicitOuterClosureMap(string[] scannerLines)
    {
        var result = new bool[scannerLines.Length];
        var tokens = new List<(int LineIndex, int BalanceAfter, bool IsDeclaration)>();
        var balance = 0;
        var delimiterFrames = new Stack<(char ClosingDelimiter, int BlockDepth)>();
        for (var lineIndex = 0; lineIndex < scannerLines.Length; lineIndex++)
        {
            var code = scannerLines[lineIndex];
            var delimiterScanIndex = 0;
            foreach (Match match in MatlabScientificBlockTokenRegex.Matches(code))
            {
                ScanScientificDelimiterFrames(
                    code,
                    delimiterScanIndex,
                    match.Index,
                    balance,
                    delimiterFrames);
                delimiterScanIndex = match.Index + match.Length;

                var keyword = match.Groups["keyword"].Value;
                if (!IsScientificBlockTokenAtStatementBoundary(code, match.Index, keyword, "matlab"))
                    continue;

                if (keyword.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    if (delimiterFrames.TryPeek(out var delimiterFrame)
                        && balance <= delimiterFrame.BlockDepth)
                    {
                        continue;
                    }

                    balance--;
                }
                else
                {
                    balance++;
                }

                tokens.Add((
                    lineIndex,
                    balance,
                    keyword.Equals("function", StringComparison.OrdinalIgnoreCase)
                        || keyword.Equals("classdef", StringComparison.OrdinalIgnoreCase)));
            }

            ScanScientificDelimiterFrames(
                code,
                delimiterScanIndex,
                code.Length,
                balance,
                delimiterFrames);
        }

        var suffixMinimumBalance = int.MaxValue;
        for (var tokenIndex = tokens.Count - 1; tokenIndex >= 0; tokenIndex--)
        {
            var token = tokens[tokenIndex];
            if (token.IsDeclaration)
            {
                result[token.LineIndex] =
                    suffixMinimumBalance <= token.BalanceAfter - 2;
            }

            suffixMinimumBalance = Math.Min(suffixMinimumBalance, token.BalanceAfter);
        }

        return result;
    }

    private static void ScanScientificDelimiterFrames(
        string line,
        int startIndex,
        int endIndex,
        int blockDepth,
        Stack<(char ClosingDelimiter, int BlockDepth)> delimiterFrames)
    {
        for (var index = startIndex; index < endIndex; index++)
        {
            var closingDelimiter = line[index] switch
            {
                '(' => ')',
                '[' => ']',
                '{' => '}',
                _ => '\0',
            };
            if (closingDelimiter != '\0')
            {
                delimiterFrames.Push((closingDelimiter, blockDepth));
                continue;
            }

            if (delimiterFrames.TryPeek(out var delimiterFrame)
                && line[index] == delimiterFrame.ClosingDelimiter)
            {
                delimiterFrames.Pop();
            }
        }
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
            useMatlabStringRules: language == "matlab",
            useBackslashEscapes: language == "julia");
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
