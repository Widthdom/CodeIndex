using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class DynamicDeclarativeReferenceExtractor
{
    private static IReadOnlyDictionary<int, IReadOnlyList<PrologGoalCall>> BuildPrologGoalCalls(
        IReadOnlyList<string> lines,
        IReadOnlyDictionary<int, SymbolRecord> containersByLine,
        IReadOnlySet<string> callableNames)
    {
        var result = new Dictionary<int, IReadOnlyList<PrologGoalCall>>();
        var frames = new Stack<PrologLexicalFrame>();
        var expectGoal = true;
        SymbolRecord? activeContainer = null;
        var scanningMultilineHead = false;
        var multilineHeadParenthesisDepth = 0;
        var multilineHeadParenthesesClosed = false;
        var scanningDirective = false;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var lineNumber = lineIndex + 1;
            if (!containersByLine.TryGetValue(lineNumber, out var container))
            {
                activeContainer = null;
                scanningMultilineHead = false;
                multilineHeadParenthesisDepth = 0;
                multilineHeadParenthesesClosed = false;
                if (!scanningDirective
                    && !StartsWithPrologGoalDirective(lines[lineIndex]))
                {
                    frames.Clear();
                    expectGoal = true;
                    continue;
                }

                if (!scanningDirective)
                {
                    frames.Clear();
                    expectGoal = true;
                    scanningDirective = true;
                }

                var directiveCalls = ScanPrologGoalLine(
                    lines,
                    lineIndex,
                    lines[lineIndex],
                    callableNames,
                    frames,
                    ref expectGoal);
                if (directiveCalls != null)
                {
                    for (var callIndex = 0; callIndex < directiveCalls.Count; callIndex++)
                        directiveCalls[callIndex] = directiveCalls[callIndex] with { IsTopLevelDirective = true };
                    result[lineNumber] = directiveCalls;
                }
                if (ContainsPrologClauseTerminator(lines[lineIndex]))
                {
                    frames.Clear();
                    expectGoal = true;
                    scanningDirective = false;
                }
                continue;
            }

            scanningDirective = false;
            if (activeContainer == null
                || activeContainer.StartLine != container.StartLine
                || !string.Equals(activeContainer.Name, container.Name, StringComparison.Ordinal))
            {
                frames.Clear();
                activeContainer = container;
                expectGoal = true;
                multilineHeadParenthesisDepth = 0;
                multilineHeadParenthesesClosed = false;
                scanningMultilineHead = TryInitializePrologMultilineHeadScan(
                    lines,
                    container,
                    lineIndex,
                    ref multilineHeadParenthesisDepth,
                    ref multilineHeadParenthesesClosed);
            }

            string callScanLine;
            if (scanningMultilineHead)
            {
                var multilineHeadLine = lineNumber == container.StartLine
                    ? MaskLineBeforeColumn(lines[lineIndex], container.StartColumn ?? 0)
                    : lines[lineIndex];
                callScanLine = PreparePrologMultilineHeadScanLine(
                    multilineHeadLine,
                    ref multilineHeadParenthesisDepth,
                    ref multilineHeadParenthesesClosed,
                    out var headEnded);
                scanningMultilineHead = !headEnded;
            }
            else
            {
                callScanLine = PreparePrologCallScanLine(
                    "prolog",
                    lines[lineIndex],
                    container.StartLine < lineNumber);
            }
            var lineCalls = ScanPrologGoalLine(
                lines,
                lineIndex,
                callScanLine,
                callableNames,
                frames,
                ref expectGoal);
            if (lineCalls != null)
            {
                for (var callIndex = 0; callIndex < lineCalls.Count; callIndex++)
                {
                    var call = lineCalls[callIndex];
                    if (IsTopLevelPrologDirectiveGoal(lines[lineIndex], call.Column))
                        lineCalls[callIndex] = call with { IsTopLevelDirective = true };
                }
                result[lineNumber] = lineCalls;
            }

            if (ContainsPrologClauseTerminator(callScanLine))
            {
                frames.Clear();
                activeContainer = null;
                expectGoal = true;
            }
        }

        return result;
    }

    private static bool StartsWithPrologGoalDirective(string line)
    {
        var column = 0;
        while (column < line.Length && char.IsWhiteSpace(line[column]))
            column++;
        return line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal);
    }

    private static IReadOnlySet<int> BuildPrologDirectiveLines(
        IReadOnlyList<string> lines)
    {
        var directiveLines = new HashSet<int>();
        var scanningDirective = false;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (!scanningDirective && !StartsWithPrologGoalDirective(lines[lineIndex]))
                continue;

            scanningDirective = true;
            directiveLines.Add(lineIndex + 1);
            if (ContainsPrologClauseTerminator(lines[lineIndex]))
                scanningDirective = false;
        }

        return directiveLines;
    }

    private static bool IsTopLevelPrologDirectiveGoal(string line, int goalColumn)
    {
        var segmentStartColumn = 0;
        for (var column = 0; column < goalColumn; column++)
        {
            if (IsPrologClauseTerminator(line, column))
                segmentStartColumn = column + 1;
        }

        segmentStartColumn = SkipWhitespace(line, segmentStartColumn);
        return segmentStartColumn + 2 <= goalColumn
            && line.AsSpan(segmentStartColumn).StartsWith(":-", StringComparison.Ordinal);
    }

    private static bool TryInitializePrologMultilineHeadScan(
        IReadOnlyList<string> lines,
        SymbolRecord container,
        int currentLineIndex,
        ref int parenthesisDepth,
        ref bool parenthesesClosed)
    {
        var startLineIndex = container.StartLine - 1;
        if (startLineIndex < 0 || startLineIndex >= lines.Count || startLineIndex > currentLineIndex)
            return false;

        var startColumn = Math.Clamp(
            container.StartColumn ?? 0,
            0,
            lines[startLineIndex].Length);
        var headLine = lines[startLineIndex][startColumn..];
        var multilineHeadMatch = PrologMultilineHeadRegex.Match(headLine);
        if (PrologHeadRegex.IsMatch(headLine) || !multilineHeadMatch.Success)
            return false;
        parenthesesClosed = !multilineHeadMatch.Groups["open"].Success;

        for (var lineIndex = startLineIndex; lineIndex < currentLineIndex; lineIndex++)
        {
            var line = lineIndex == startLineIndex
                ? MaskLineBeforeColumn(lines[lineIndex], startColumn)
                : lines[lineIndex];
            _ = PreparePrologMultilineHeadScanLine(
                line,
                ref parenthesisDepth,
                ref parenthesesClosed,
                out var headEnded);
            if (headEnded)
                return false;
        }

        return true;
    }

    private static string MaskLineBeforeColumn(string line, int startColumn)
    {
        startColumn = Math.Clamp(startColumn, 0, line.Length);
        if (startColumn == 0)
            return line;

        var masked = line.ToCharArray();
        FillWithSpaces(masked, 0, startColumn);
        return new string(masked);
    }

    private static string PreparePrologMultilineHeadScanLine(
        string line,
        ref int parenthesisDepth,
        ref bool parenthesesClosed,
        out bool headEnded)
    {
        headEnded = false;
        for (var column = 0; column < line.Length; column++)
        {
            var ch = line[column];
            if (ch is '\'' or '"')
            {
                column = SkipQuotedToken(line, column, ch) - 1;
                continue;
            }

            if (!parenthesesClosed)
            {
                if (ch == '(')
                {
                    parenthesisDepth++;
                }
                else if (ch == ')' && parenthesisDepth > 0)
                {
                    parenthesisDepth--;
                    parenthesesClosed = parenthesisDepth == 0;
                }
                continue;
            }

            if (line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal))
            {
                var masked = line.ToCharArray();
                FillWithSpaces(masked, 0, column + 3);
                headEnded = true;
                return new string(masked);
            }
            if (line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal))
            {
                var masked = line.ToCharArray();
                FillWithSpaces(masked, 0, column + 2);
                headEnded = true;
                return new string(masked);
            }
            if (IsPrologClauseTerminator(line, column))
            {
                headEnded = true;
                return new string(' ', line.Length);
            }
        }

        return new string(' ', line.Length);
    }

    private static List<PrologGoalCall>? ScanPrologGoalLine(
        IReadOnlyList<string> lines,
        int lineIndex,
        string line,
        IReadOnlySet<string> callableNames,
        Stack<PrologLexicalFrame> frames,
        ref bool expectGoal)
    {
        List<PrologGoalCall>? calls = null;
        for (var column = 0; column < line.Length;)
        {
            var ch = line[column];
            if (char.IsWhiteSpace(ch))
            {
                column++;
                continue;
            }

            if (ch is '\'' or '"')
            {
                column = SkipQuotedToken(line, column, ch);
                if (expectGoal)
                    expectGoal = false;
                continue;
            }
            if (IsPrologClauseTerminator(line, column))
            {
                frames.Clear();
                expectGoal = true;
                column++;
                continue;
            }

            if (expectGoal)
            {
                if (line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal))
                {
                    column += 3;
                    continue;
                }
                if (line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal)
                    || line.AsSpan(column).StartsWith(@"\+", StringComparison.Ordinal))
                {
                    column += 2;
                    continue;
                }
                if (ch is ',' or ';')
                {
                    column++;
                    continue;
                }
                if (line.AsSpan(column).StartsWith("->", StringComparison.Ordinal))
                {
                    column += 2;
                    continue;
                }
                if (ch == '(')
                {
                    frames.Push(new PrologLexicalFrame(PrologLexicalFrameKind.GoalGroup));
                    column++;
                    continue;
                }
                if (ch == '{')
                {
                    frames.Push(new PrologLexicalFrame(
                        PrologLexicalFrameKind.GoalGroup,
                        terminator: '}'));
                    column++;
                    continue;
                }
                if (ch == '[')
                {
                    frames.Push(new PrologLexicalFrame(
                        PrologLexicalFrameKind.TermGroup,
                        terminator: ']'));
                    expectGoal = false;
                    column++;
                    continue;
                }
                if (ch == '!')
                {
                    expectGoal = false;
                    column++;
                    continue;
                }
                if (char.IsLower(ch))
                {
                    var nameStart = column;
                    column++;
                    while (column < line.Length
                        && (char.IsLetterOrDigit(line[column]) || line[column] == '_'))
                    {
                        column++;
                    }

                    var name = line[nameStart..column];
                    var nextColumn = column;
                    while (nextColumn < line.Length && char.IsWhiteSpace(line[nextColumn]))
                        nextColumn++;
                    if (nextColumn < line.Length
                        && line[nextColumn] == ':'
                        && (nextColumn + 1 >= line.Length || line[nextColumn + 1] != '-'))
                    {
                        column = nextColumn + 1;
                        expectGoal = true;
                        continue;
                    }
                    if (callableNames.Contains(name)
                        && !IsPrologTermBeforeInfixOperator(
                            lines,
                            lineIndex,
                            column,
                            nextColumn))
                    {
                        (calls ??= []).Add(new PrologGoalCall(name, nameStart));
                    }

                    if (nextColumn < line.Length && line[nextColumn] == '(')
                    {
                        if (PrologMetaGoalArguments.TryGetValue(name, out var goalArgumentIndices))
                        {
                            var metaFrame = new PrologLexicalFrame(
                                PrologLexicalFrameKind.MetaArguments,
                                goalArgumentIndices);
                            frames.Push(metaFrame);
                            expectGoal = metaFrame.CurrentArgumentIsGoal;
                        }
                        else
                        {
                            frames.Push(new PrologLexicalFrame(
                                PrologLexicalFrameKind.PredicateArguments));
                            expectGoal = false;
                        }

                        column = nextColumn + 1;
                    }
                    else
                    {
                        expectGoal = false;
                    }

                    continue;
                }

                expectGoal = false;
                column++;
                continue;
            }

            if (ch == '(')
            {
                frames.Push(new PrologLexicalFrame(PrologLexicalFrameKind.PredicateArguments));
                column++;
                continue;
            }
            if (ch is '[' or '{')
            {
                frames.Push(new PrologLexicalFrame(
                    PrologLexicalFrameKind.TermGroup,
                    terminator: ch == '[' ? ']' : '}'));
                column++;
                continue;
            }
            if (ch is ')' or ']' or '}')
            {
                if (frames.TryPeek(out var closingFrame)
                    && closingFrame.Terminator == ch)
                {
                    frames.Pop();
                }
                expectGoal = false;
                column++;
                continue;
            }
            if (ch == ',')
            {
                if (frames.TryPeek(out var frame)
                    && frame.Kind == PrologLexicalFrameKind.MetaArguments)
                {
                    frame.ArgumentIndex++;
                    expectGoal = frame.CurrentArgumentIsGoal;
                }
                else if (CanStartNextPrologGoal(frames))
                {
                    expectGoal = true;
                }

                column++;
                continue;
            }
            if (ch == ';'
                || line.AsSpan(column).StartsWith("->", StringComparison.Ordinal))
            {
                if (CanStartNextPrologGoal(frames))
                    expectGoal = true;
                column += ch == ';' ? 1 : 2;
                continue;
            }

            column++;
        }

        return calls;
    }

    private static bool IsPrologTermBeforeInfixOperator(
        IReadOnlyList<string> lines,
        int lineIndex,
        int nameEndColumn,
        int nextColumn)
    {
        const int lookaheadLineLimit = 256;
        var line = lines[lineIndex];
        var afterTermLine = lineIndex;
        var afterTermColumn = nextColumn;
        if (nextColumn < line.Length && line[nextColumn] == '(')
        {
            var depth = 0;
            var termClosed = false;
            var endLineExclusive = Math.Min(lines.Count, lineIndex + lookaheadLineLimit);
            for (var scanLineIndex = lineIndex;
                scanLineIndex < endLineExclusive && !termClosed;
                scanLineIndex++)
            {
                var scanLine = lines[scanLineIndex];
                var startColumn = scanLineIndex == lineIndex ? nextColumn : 0;
                for (var column = startColumn; column < scanLine.Length; column++)
                {
                    var ch = scanLine[column];
                    if (ch is '\'' or '"')
                    {
                        column = SkipQuotedToken(scanLine, column, ch) - 1;
                        continue;
                    }

                    if (ch == '(')
                    {
                        depth++;
                    }
                    else if (ch == ')' && --depth == 0)
                    {
                        afterTermLine = scanLineIndex;
                        afterTermColumn = column + 1;
                        termClosed = true;
                        break;
                    }
                }
            }

            // An unterminated compound term is not authoritative evidence of a call.
            // 未終端の compound term は call と判断できる根拠にならない。
            if (!termClosed)
                return true;
        }
        else
        {
            afterTermColumn = nameEndColumn;
        }

        if (!TryFindNextPrologToken(
                lines,
                afterTermLine,
                afterTermColumn,
                lookaheadLineLimit,
                out var operatorLine,
                out var operatorColumn))
        {
            return false;
        }

        var operatorSourceLine = lines[operatorLine];
        var remaining = operatorSourceLine.AsSpan(operatorColumn);
        if (remaining.StartsWith("->", StringComparison.Ordinal)
            || remaining.StartsWith("*->", StringComparison.Ordinal))
        {
            return false;
        }

        if (operatorSourceLine[operatorColumn] is '=' or '\\' or '<' or '>' or '@' or '#'
            or ':' or '+' or '-' or '*' or '/' or '^')
        {
            return true;
        }

        foreach (var operatorName in PrologInfixOperatorNames)
        {
            if (!remaining.StartsWith(operatorName, StringComparison.Ordinal))
                continue;
            var operatorEnd = operatorColumn + operatorName.Length;
            if (operatorEnd >= operatorSourceLine.Length
                || !char.IsLetterOrDigit(operatorSourceLine[operatorEnd])
                    && operatorSourceLine[operatorEnd] != '_')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindNextPrologToken(
        IReadOnlyList<string> lines,
        int startLine,
        int startColumn,
        int lookaheadLineLimit,
        out int tokenLine,
        out int tokenColumn)
    {
        var endLineExclusive = Math.Min(lines.Count, startLine + lookaheadLineLimit);
        for (var lineIndex = startLine; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = lines[lineIndex];
            var column = lineIndex == startLine ? startColumn : 0;
            while (column < line.Length && char.IsWhiteSpace(line[column]))
                column++;
            if (column < line.Length)
            {
                tokenLine = lineIndex;
                tokenColumn = column;
                return true;
            }
        }

        tokenLine = -1;
        tokenColumn = -1;
        return false;
    }

    private static readonly string[] PrologInfixOperatorNames =
        ["is", "mod", "rem", "xor", "div", "rdiv"];

    private static bool CanStartNextPrologGoal(
        IEnumerable<PrologLexicalFrame> frames)
    {
        foreach (var frame in frames)
        {
            if (frame.Kind == PrologLexicalFrameKind.PredicateArguments)
                return false;
            if (frame.Kind == PrologLexicalFrameKind.TermGroup)
                return false;
            if (frame.Kind == PrologLexicalFrameKind.MetaArguments)
                return frame.CurrentArgumentIsGoal;
        }

        return true;
    }

    private static bool ContainsPrologClauseTerminator(string line)
    {
        for (var column = 0; column < line.Length; column++)
        {
            if (IsPrologClauseTerminator(line, column))
                return true;
        }

        return false;
    }

    private static void AddPrologContainers(
        IReadOnlyList<string> lines,
        IReadOnlyList<SymbolRecord> symbols,
        Dictionary<int, SymbolRecord> containersByLine,
        Dictionary<int, List<SymbolRecord>> declarationsByLine)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || symbol.StartLine < 1 || symbol.StartLine > lines.Count)
                continue;

            var startLineIndex = symbol.StartLine - 1;
            var startColumn = Math.Clamp(
                symbol.StartColumn ?? 0,
                0,
                lines[startLineIndex].Length);
            var headLine = lines[startLineIndex][startColumn..];
            var headMatch = PrologHeadRegex.Match(headLine);
            if (!headMatch.Success)
                headMatch = PrologMultilineHeadRegex.Match(headLine);
            if (!headMatch.Success
                || !string.Equals(headMatch.Groups["name"].Value, symbol.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!declarationsByLine.TryGetValue(symbol.StartLine, out var declarations))
            {
                declarations = [];
                declarationsByLine[symbol.StartLine] = declarations;
            }
            declarations.Add(symbol);

            var endLineIndex = FindPrologClauseEnd(lines, startLineIndex, startColumn);
            for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
                containersByLine.TryAdd(lineIndex + 1, symbol);
        }

        foreach (var declarations in declarationsByLine.Values)
        {
            declarations.Sort(static (left, right) =>
                (left.StartColumn ?? 0).CompareTo(right.StartColumn ?? 0));
        }
    }

    private static int FindPrologClauseEnd(
        IReadOnlyList<string> lines,
        int startLineIndex,
        int startColumn)
    {
        for (var lineIndex = startLineIndex; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var firstColumn = lineIndex == startLineIndex ? startColumn : 0;
            for (var column = firstColumn; column < line.Length; column++)
            {
                if (IsPrologClauseTerminator(line, column))
                    return lineIndex;
            }
        }

        return startLineIndex;
    }
}
