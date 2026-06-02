using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindRubyRange(string[] lines, int startIndex)
    {
        var firstLine = lines[startIndex];
        if (!RubyBlockStartRegex.IsMatch(firstLine))
            return (startIndex + 1, null, null);

        var scanState = new RubyMaskState();
        var maskedFirstLine = MaskRubyLineForBodyScan(firstLine, scanState);
        int depth = 0;
        int? bodyStartLine = null;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var trimmed = i == startIndex
                ? maskedFirstLine.Trim()
                : MaskRubyLineForBodyScan(lines[i], scanState).Trim();
            if (trimmed.Length == 0)
                continue;

            if (i > startIndex && bodyStartLine == null)
                bodyStartLine = i + 1;

            foreach (Match token in RubyBlockTokenRegex.Matches(trimmed))
            {
                if (token.Value == "end")
                    depth--;
                else
                    depth++;
            }

            if (depth <= 0)
            {
                if (bodyStartLine == null || bodyStartLine > i + 1)
                    return (i + 1, null, null);

                return (i + 1, bodyStartLine, i + 1);
            }
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lines.Length, bodyStartLine, lines.Length);
    }

    private static string MaskRubyLineForBodyScan(string line, RubyMaskState state)
    {
        if (line.Length == 0)
            return line;

        var masked = line.ToCharArray();

        if (state.Mode == RubyScanMode.Heredoc)
        {
            for (int i = 0; i < masked.Length; i++)
                masked[i] = ' ';

            if (IsRubyHeredocTerminatorLine(line, state.HeredocTerminator!, state.HeredocAllowsIndentation))
            {
                state.Mode = RubyScanMode.Code;
                state.HeredocTerminator = null;
                state.HeredocAllowsIndentation = false;
            }

            return new string(masked);
        }

        for (int i = 0; i < masked.Length; i++)
        {
            if (state.Mode == RubyScanMode.SingleQuote)
            {
                masked[i] = ' ';
                if (line[i] == '\\' && i + 1 < masked.Length)
                {
                    masked[++i] = ' ';
                    continue;
                }

                if (line[i] == '\'')
                    state.Mode = RubyScanMode.Code;

                continue;
            }

            if (state.Mode == RubyScanMode.DoubleQuote)
            {
                masked[i] = ' ';
                if (line[i] == '\\' && i + 1 < masked.Length)
                {
                    masked[++i] = ' ';
                    continue;
                }

                if (line[i] == '"')
                    state.Mode = RubyScanMode.Code;

                continue;
            }

            if (state.Mode == RubyScanMode.PercentLiteral)
            {
                masked[i] = ' ';
                if (line[i] == '\\' && i + 1 < masked.Length)
                {
                    masked[++i] = ' ';
                    continue;
                }

                if (state.PercentDelimiterIsPaired && line[i] == state.PercentOpenDelimiter)
                {
                    state.PercentDelimiterDepth++;
                    continue;
                }

                if (line[i] == state.PercentCloseDelimiter)
                {
                    if (state.PercentDelimiterIsPaired && state.PercentDelimiterDepth > 0)
                    {
                        state.PercentDelimiterDepth--;
                        continue;
                    }

                    state.Mode = RubyScanMode.Code;
                    state.PercentOpenDelimiter = default;
                    state.PercentCloseDelimiter = default;
                    state.PercentDelimiterIsPaired = false;
                    state.PercentDelimiterDepth = 0;
                }

                continue;
            }

            if (line[i] == '#')
            {
                for (int j = i; j < masked.Length; j++)
                    masked[j] = ' ';
                break;
            }

            if (line[i] == '\'')
            {
                masked[i] = ' ';
                state.Mode = RubyScanMode.SingleQuote;
                continue;
            }

            if (line[i] == '"')
            {
                masked[i] = ' ';
                state.Mode = RubyScanMode.DoubleQuote;
                continue;
            }

            if (TryStartRubyPercentLiteral(line, i, out var consumedChars, out var openDelimiter, out var closeDelimiter, out var isPaired))
            {
                for (int j = 0; j < consumedChars && i + j < masked.Length; j++)
                    masked[i + j] = ' ';

                state.Mode = RubyScanMode.PercentLiteral;
                state.PercentOpenDelimiter = openDelimiter;
                state.PercentCloseDelimiter = closeDelimiter;
                state.PercentDelimiterIsPaired = isPaired;
                state.PercentDelimiterDepth = 0;
                i += consumedChars - 1;
                continue;
            }

            if (TryStartRubyHeredoc(line, i, out consumedChars, out var heredocTerminator, out var heredocAllowsIndentation))
            {
                for (int j = i; j < masked.Length; j++)
                    masked[j] = ' ';

                state.Mode = RubyScanMode.Heredoc;
                state.HeredocTerminator = heredocTerminator;
                state.HeredocAllowsIndentation = heredocAllowsIndentation;
                return new string(masked);
            }
        }

        return new string(masked);
    }

    private static bool TryStartRubyPercentLiteral(string line, int index, out int consumedChars, out char openDelimiter, out char closeDelimiter, out bool isPaired)
    {
        consumedChars = 0;
        openDelimiter = default;
        closeDelimiter = default;
        isPaired = false;

        if (index + 2 >= line.Length || line[index] != '%' || !IsRubyPercentLiteralKind(line[index + 1]))
            return false;

        var delimiter = line[index + 2];
        if (!TryGetRubyPercentLiteralDelimiterPair(delimiter, out openDelimiter, out closeDelimiter, out isPaired))
            return false;

        consumedChars = 3;
        return true;
    }

    private static bool IsRubyPercentLiteralKind(char ch)
        => ch is 'q' or 'Q' or 'r' or 'w' or 'W' or 'i' or 'I' or 'x' or 'X';

    private static bool TryGetRubyPercentLiteralDelimiterPair(char delimiter, out char openDelimiter, out char closeDelimiter, out bool isPaired)
    {
        if (delimiter == '(')
        {
            openDelimiter = '(';
            closeDelimiter = ')';
            isPaired = true;
            return true;
        }

        if (delimiter == '[')
        {
            openDelimiter = '[';
            closeDelimiter = ']';
            isPaired = true;
            return true;
        }

        if (delimiter == '{')
        {
            openDelimiter = '{';
            closeDelimiter = '}';
            isPaired = true;
            return true;
        }

        if (delimiter == '<')
        {
            openDelimiter = '<';
            closeDelimiter = '>';
            isPaired = true;
            return true;
        }

        openDelimiter = delimiter;
        closeDelimiter = delimiter;
        isPaired = false;
        return true;
    }

    private static bool TryStartRubyHeredoc(string line, int index, out int consumedChars, out string terminator, out bool allowsIndentation)
    {
        consumedChars = 0;
        terminator = string.Empty;
        allowsIndentation = false;

        if (index + 1 >= line.Length || line[index] != '<' || line[index + 1] != '<')
            return false;

        var scanIndex = index + 2;
        if (scanIndex < line.Length && line[scanIndex] is '-' or '~')
        {
            allowsIndentation = true;
            scanIndex++;
        }

        if (scanIndex >= line.Length)
            return false;

        if (line[scanIndex] is '\'' or '"' or '`')
        {
            var quote = line[scanIndex];
            scanIndex++;
            var start = scanIndex;
            while (scanIndex < line.Length && line[scanIndex] != quote)
                scanIndex++;

            if (scanIndex >= line.Length || scanIndex == start)
                return false;

            terminator = line[start..scanIndex];
            consumedChars = scanIndex + 1 - index;
            return true;
        }

        var startIndex = scanIndex;
        while (scanIndex < line.Length && (char.IsLetterOrDigit(line[scanIndex]) || line[scanIndex] == '_'))
            scanIndex++;

        if (scanIndex == startIndex)
            return false;

        terminator = line[startIndex..scanIndex];
        consumedChars = scanIndex - index;
        return true;
    }

    private static bool IsRubyHeredocTerminatorLine(string line, string terminator, bool allowsIndentation)
    {
        var trimmed = allowsIndentation ? line.Trim() : line.TrimEnd();
        return trimmed == terminator;
    }

    private static readonly Regex RubyBlockStartRegex = new(@"^\s*(?:(?:class|module|def|if|unless|case|begin|do|while|until|for)\b|(?:namespace|factory)\s+:\w+\b.*\bdo\b|shared_examples(?:_for)?\s+['""].*['""]\s*do\b|(?:subject|let!?)\s*\(\s*:\w+\s*\)\s*do\b|[A-Z][A-Za-z0-9_]*\s*=\s*(?:Class|Struct)\.new\b.*\bdo\b)", RegexOptions.Compiled);
    private static readonly Regex RubyBlockTokenRegex = new(@"\b(?:class|module|def|if|unless|case|begin|do|while|until|for|end)\b", RegexOptions.Compiled);
    private enum RubyScanMode
    {
        Code,
        SingleQuote,
        DoubleQuote,
        PercentLiteral,
        Heredoc,
    }
    private sealed class RubyMaskState
    {
        public RubyScanMode Mode { get; set; } = RubyScanMode.Code;
        public char PercentOpenDelimiter { get; set; }
        public char PercentCloseDelimiter { get; set; }
        public bool PercentDelimiterIsPaired { get; set; }
        public int PercentDelimiterDepth { get; set; }
        public string? HeredocTerminator { get; set; }
        public bool HeredocAllowsIndentation { get; set; }
    }

}
