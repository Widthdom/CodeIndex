using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class DynamicDeclarativeReferenceExtractor
{
    private static void AddTclContainers(
        IReadOnlyList<string> lines,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyDictionary<long, TclBraceEnd> braceEnds,
        List<TclContainerScope> scopes,
        HashSet<long> scriptBodyOpenings)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || symbol.StartLine < 1 || symbol.StartLine > lines.Count)
                continue;

            var startLineIndex = symbol.StartLine - 1;
            var declarationMatch = FindTclProcDeclaration(lines[startLineIndex], symbol);
            if (declarationMatch == null
                || !TryFindTclBodyEnd(
                    lines,
                    braceEnds,
                    startLineIndex,
                    declarationMatch.Index + declarationMatch.Length,
                    out var bodyStartLineIndex,
                    out var bodyStartColumn,
                    out var bodyEnd))
            {
                continue;
            }

            scopes.Add(new TclContainerScope(
                symbol,
                bodyStartLineIndex + 1,
                lines[bodyStartLineIndex][bodyStartColumn] is '{' or '"'
                    ? bodyStartColumn
                    : bodyStartColumn - 1,
                bodyEnd.Line + 1,
                lines[bodyStartLineIndex][bodyStartColumn] is '{' or '"'
                    ? bodyEnd.Column
                    : bodyEnd.Column + 1));
            if (lines[bodyStartLineIndex][bodyStartColumn] == '{')
                scriptBodyOpenings.Add(GetTclPositionKey(bodyStartLineIndex, bodyStartColumn));
        }

        scopes.Sort(static (left, right) =>
        {
            var startComparison = left.BodyStartLine.CompareTo(right.BodyStartLine);
            return startComparison != 0
                ? startComparison
                : left.BodyStartColumn.CompareTo(right.BodyStartColumn);
        });
    }

    private static Match? FindTclProcDeclaration(string line, SymbolRecord symbol)
    {
        Match? fallback = null;
        foreach (Match match in BoundedRegex.EnumerateMatches(TclProcRegex, line))
        {
            var nameGroup = match.Groups["name"];
            if (!string.Equals(nameGroup.Value, symbol.Name, StringComparison.Ordinal))
                continue;
            if (symbol.StartColumn == nameGroup.Index)
                return match;
            fallback ??= match;
        }

        return fallback;
    }

    private static bool TryFindTclBodyEnd(
        IReadOnlyList<string> lines,
        IReadOnlyDictionary<long, TclBraceEnd> braceEnds,
        int startLineIndex,
        int searchColumn,
        out int bodyStartLineIndex,
        out int bodyStartColumn,
        out TclBraceEnd bodyEnd)
    {
        bodyStartLineIndex = startLineIndex;
        bodyStartColumn = -1;
        bodyEnd = default;
        if (!TryFindNextNonWhitespace(lines[startLineIndex], searchColumn, out var argsColumn)
            || !TryFindTclWordEnd(
                lines,
                braceEnds,
                startLineIndex,
                argsColumn,
                out var argsEndLine,
                out var argsEndColumn)
            || !TryFindNextNonWhitespace(
                lines,
                argsEndLine,
                argsEndColumn + 1,
                out bodyStartLineIndex,
                out bodyStartColumn)
            || !TryFindTclWordEnd(
                lines,
                braceEnds,
                bodyStartLineIndex,
                bodyStartColumn,
                out var bodyEndLine,
                out var bodyEndColumn))
        {
            return false;
        }

        bodyEnd = new TclBraceEnd(bodyEndLine, bodyEndColumn);
        return true;
    }

    private static bool TryFindTclWordEnd(
        IReadOnlyList<string> lines,
        IReadOnlyDictionary<long, TclBraceEnd> braceEnds,
        int startLine,
        int startColumn,
        out int endLine,
        out int endColumn)
    {
        var line = lines[startLine];
        if (line[startColumn] == '{')
        {
            if (braceEnds.TryGetValue(GetTclPositionKey(startLine, startColumn), out var braceEnd))
            {
                endLine = braceEnd.Line;
                endColumn = braceEnd.Column;
                return true;
            }

            endLine = -1;
            endColumn = -1;
            return false;
        }

        if (line[startColumn] == '"')
        {
            for (var lineIndex = startLine; lineIndex < lines.Count; lineIndex++)
            {
                line = lines[lineIndex];
                var firstColumn = lineIndex == startLine ? startColumn + 1 : 0;
                for (var column = firstColumn; column < line.Length; column++)
                {
                    if (line[column] == '\\')
                    {
                        column++;
                        continue;
                    }
                    if (line[column] == '"')
                    {
                        endLine = lineIndex;
                        endColumn = column;
                        return true;
                    }
                }
            }

            endLine = -1;
            endColumn = -1;
            return false;
        }

        var wordEnd = startColumn;
        while (wordEnd + 1 < line.Length && !char.IsWhiteSpace(line[wordEnd + 1]))
            wordEnd++;
        endLine = startLine;
        endColumn = wordEnd;
        return true;
    }

    private static bool TryFindNextNonWhitespace(
        string line,
        int startColumn,
        out int foundColumn)
    {
        for (var column = startColumn; column < line.Length; column++)
        {
            if (!char.IsWhiteSpace(line[column]))
            {
                foundColumn = column;
                return true;
            }
        }

        foundColumn = -1;
        return false;
    }

    private static bool TryFindNextNonWhitespace(
        IReadOnlyList<string> lines,
        int startLine,
        int startColumn,
        out int foundLine,
        out int foundColumn)
    {
        for (var lineIndex = startLine; lineIndex < lines.Count; lineIndex++)
        {
            var column = lineIndex == startLine ? startColumn : 0;
            if (TryFindNextNonWhitespace(lines[lineIndex], column, out foundColumn))
            {
                if (foundColumn == lines[lineIndex].Length - 1
                    && lines[lineIndex][foundColumn] == '\\')
                {
                    continue;
                }

                foundLine = lineIndex;
                return true;
            }
        }

        foundLine = -1;
        foundColumn = -1;
        return false;
    }

    private static Dictionary<long, TclBraceEnd> BuildTclBraceEndPositions(IReadOnlyList<string> lines)
    {
        var result = new Dictionary<long, TclBraceEnd>();
        var openings = new Stack<(int Line, int Column)>();
        var commandStart = true;
        var wordStart = true;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                var ch = line[column];
                if (openings.Count > 0)
                {
                    if (ch == '\\')
                    {
                        column++;
                        continue;
                    }
                    if (ch == '{')
                    {
                        openings.Push((lineIndex, column));
                    }
                    else if (ch == '}')
                    {
                        var opening = openings.Pop();
                        result[GetTclPositionKey(opening.Line, opening.Column)] =
                            new TclBraceEnd(lineIndex, column);
                    }
                    continue;
                }

                if (ch == '\\')
                {
                    column++;
                    commandStart = false;
                    wordStart = false;
                    continue;
                }

                if (ch == '"')
                {
                    column = SkipQuotedToken(line, column, ch) - 1;
                    commandStart = false;
                    wordStart = false;
                    continue;
                }

                if (ch == '#' && commandStart)
                    break;
                if (ch == ';' || ch == '[')
                {
                    commandStart = true;
                    wordStart = true;
                    continue;
                }
                if (char.IsWhiteSpace(ch))
                {
                    wordStart = true;
                    continue;
                }
                if (ch == '{' && wordStart)
                {
                    openings.Push((lineIndex, column));
                    commandStart = false;
                    wordStart = false;
                    continue;
                }

                commandStart = false;
                wordStart = false;
            }

            if (openings.Count == 0)
            {
                commandStart = true;
                wordStart = true;
            }
        }

        return result;
    }

    private static string[] BuildTclCallLines(
        IReadOnlyList<string> lines,
        IReadOnlyDictionary<long, TclBraceEnd> braceEnds,
        IReadOnlySet<long> scriptBodyOpenings,
        IDictionary<int, int>? commentColumns = null)
    {
        var result = new string[lines.Count];
        var frames = new Stack<TclLexicalFrame>();
        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script));
        var commentContinued = false;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (commentContinued)
            {
                result[lineIndex] = new string(' ', line.Length);
                commentColumns?.TryAdd(lineIndex, 0);
                commentContinued = HasTclEscapedNewline(line);
                continue;
            }

            var buffer = line.ToCharArray();
            var lineContinued = false;
            var suppressLeadingContinuedWord = frames.Peek().Kind != TclLexicalFrameKind.Script
                || !frames.Peek().CommandStart;
            for (var column = 0; column < line.Length;)
            {
                var frame = frames.Peek();
                var ch = line[column];
                if (frame.Kind == TclLexicalFrameKind.SwitchTable)
                {
                    buffer[column] = ' ';
                    if (ch == frame.Terminator)
                    {
                        frames.Pop();
                        column++;
                    }
                    else if (ch == '\\' && column + 1 < line.Length)
                    {
                        buffer[column + 1] = ' ';
                        frame.WordStart = false;
                        column += 2;
                    }
                    else if (char.IsWhiteSpace(ch))
                    {
                        frame.WordStart = true;
                        column++;
                    }
                    else if (ch == '{' && frame.WordStart)
                    {
                        var wordIndex = frame.WordIndex++;
                        if (wordIndex % 2 == 1)
                        {
                            buffer[column] = ';';
                            frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script, '}'));
                            suppressLeadingContinuedWord = false;
                        }
                        else
                        {
                            frames.Push(new TclLexicalFrame(TclLexicalFrameKind.BracedWord, '}'));
                        }

                        frame.WordStart = false;
                        column++;
                    }
                    else if (ch == '"' && frame.WordStart)
                    {
                        var wordIndex = frame.WordIndex++;
                        if (wordIndex % 2 == 1)
                        {
                            buffer[column] = ';';
                            frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script, '"'));
                            suppressLeadingContinuedWord = false;
                        }
                        else
                        {
                            var endColumn = SkipQuotedToken(line, column, '"');
                            FillWithSpaces(buffer, column, endColumn);
                            column = endColumn - 1;
                        }
                        frame.WordStart = false;
                        column++;
                    }
                    else
                    {
                        if (frame.WordStart)
                        {
                            var wordIndex = frame.WordIndex++;
                            if (wordIndex % 2 == 1)
                            {
                                var endColumn = column;
                                while (endColumn < line.Length
                                    && !char.IsWhiteSpace(line[endColumn])
                                    && line[endColumn] != frame.Terminator)
                                {
                                    buffer[endColumn] = line[endColumn];
                                    endColumn++;
                                }
                                MarkTclBareScriptCommandBoundary(buffer, column);
                                column = endColumn - 1;
                            }
                        }
                        frame.WordStart = false;
                        column++;
                    }

                    continue;
                }

                if (frame.Kind == TclLexicalFrameKind.BracedWord)
                {
                    buffer[column] = ' ';
                    if (ch == '\\' && column + 1 < line.Length)
                    {
                        buffer[column + 1] = ' ';
                        column += 2;
                    }
                    else if (ch == '{')
                    {
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.BracedWord, '}'));
                        column++;
                    }
                    else if (ch == frame.Terminator)
                    {
                        frames.Pop();
                        column++;
                    }
                    else
                    {
                        column++;
                    }
                    continue;
                }

                if (frame.Kind == TclLexicalFrameKind.ExpressionWord)
                {
                    buffer[column] = ' ';
                    if (ch == '\\' && column + 1 < line.Length)
                    {
                        buffer[column + 1] = ' ';
                        column += 2;
                    }
                    else if (ch == '{')
                    {
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.ExpressionWord, '}'));
                        column++;
                    }
                    else if (ch == frame.Terminator)
                    {
                        frames.Pop();
                        column++;
                    }
                    else if (ch == '[')
                    {
                        buffer[column] = '[';
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script, ']'));
                        suppressLeadingContinuedWord = false;
                        column++;
                    }
                    else
                    {
                        column++;
                    }
                    continue;
                }

                if (frame.Kind == TclLexicalFrameKind.Quote)
                {
                    buffer[column] = ' ';
                    if (ch == '\\' && column + 1 < line.Length)
                    {
                        buffer[column + 1] = ' ';
                        column += 2;
                    }
                    else if (ch == frame.Terminator)
                    {
                        frames.Pop();
                        column++;
                    }
                    else if (ch == '[')
                    {
                        buffer[column] = '[';
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script, ']'));
                        suppressLeadingContinuedWord = false;
                        column++;
                    }
                    else
                    {
                        column++;
                    }
                    continue;
                }

                if (frame.Terminator != '\0' && ch == frame.Terminator)
                {
                    PersistTclConcatenatedScriptState(frame);
                    frames.Pop();
                    buffer[column] = frame.Terminator == '}' ? ' ' : ch;
                    column++;
                    continue;
                }
                if (ch == '\\')
                {
                    buffer[column] = ' ';
                    if (column + 1 >= line.Length)
                    {
                        lineContinued = true;
                        frame.WordStart = true;
                        column++;
                        continue;
                    }

                    if (frame.WordStart)
                        frame.WordIndex++;
                    buffer[column + 1] = ' ';
                    column += 2;
                    frame.CommandStart = false;
                    frame.WordStart = false;
                    continue;
                }
                if (ch == '#' && frame.CommandStart)
                {
                    FillWithSpaces(buffer, column);
                    commentColumns?.TryAdd(lineIndex, column);
                    commentContinued = HasTclEscapedNewline(line);
                    break;
                }
                if (ch == '"')
                {
                    var isScriptArgument = false;
                    var isConcatenatedScriptArgument = false;
                    if (frame.WordStart)
                    {
                        var wordIndex = frame.WordIndex++;
                        var token = GetTclQuotedWordToken(line, column);
                        isConcatenatedScriptArgument = IsTclConcatenatedScriptArgument(
                            frame,
                            wordIndex,
                            token);
                        isScriptArgument = isConcatenatedScriptArgument
                            || IsTclScriptArgument(
                                frame,
                                wordIndex,
                                lines,
                                braceEnd: null);
                        UpdateTclFirstArgument(frame, wordIndex, token);
                        UpdateTclDictArgumentState(frame, wordIndex, token);
                        UpdateTclSwitchArgumentState(frame, wordIndex, string.Empty);
                        UpdateTclTryArgumentState(frame, wordIndex, string.Empty, isScriptArgument);
                    }
                    var quotedFrame = isScriptArgument
                        ? CreateTclScriptFrame(frame, '"', isConcatenatedScriptArgument)
                        : new TclLexicalFrame(TclLexicalFrameKind.Quote, '"');
                    buffer[column] = isScriptArgument
                        && (!isConcatenatedScriptArgument || quotedFrame.CommandStart)
                            ? ';'
                            : ' ';
                    frames.Push(quotedFrame);
                    frame.CommandStart = false;
                    frame.WordStart = false;
                    if (isScriptArgument)
                        suppressLeadingContinuedWord = false;
                    column++;
                    continue;
                }
                if (ch == '[')
                {
                    if (frame.WordStart)
                    {
                        var wordIndex = frame.WordIndex++;
                        UpdateTclDictArgumentState(frame, wordIndex, token: null);
                        UpdateTclSwitchArgumentState(frame, wordIndex, string.Empty);
                        UpdateTclTryArgumentState(
                            frame,
                            wordIndex,
                            string.Empty,
                            isScriptArgument: false);
                    }
                    frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script, ']'));
                    frame.CommandStart = false;
                    frame.WordStart = false;
                    suppressLeadingContinuedWord = false;
                    column++;
                    continue;
                }
                if (ch == '{' && frame.WordStart)
                {
                    var wordIndex = frame.WordIndex++;
                    var positionKey = GetTclPositionKey(lineIndex, column);
                    TclBraceEnd? braceEnd = braceEnds.TryGetValue(positionKey, out var foundBraceEnd)
                        ? foundBraceEnd
                        : null;
                    var isSwitchTable = IsTclSwitchTableArgument(
                        frame,
                        wordIndex,
                        lines,
                        braceEnd);
                    var token = GetTclBracedWordToken(
                        lines,
                        lineIndex,
                        column,
                        braceEnd);
                    var isConcatenatedScriptArgument = !isSwitchTable
                        && IsTclConcatenatedScriptArgument(
                            frame,
                            wordIndex,
                            token);
                    var isExpressionArgument = !isSwitchTable
                        && IsTclExpressionArgument(frame, wordIndex);
                    var isScriptArgument = !isSwitchTable
                        && (isConcatenatedScriptArgument
                        || scriptBodyOpenings.Contains(positionKey)
                        || IsTclScriptArgument(
                            frame,
                            wordIndex,
                            lines,
                            braceEnd));
                    UpdateTclFirstArgument(frame, wordIndex, token);
                    UpdateTclDictArgumentState(frame, wordIndex, token);
                    UpdateTclSwitchArgumentState(frame, wordIndex, string.Empty);
                    UpdateTclTryArgumentState(
                        frame,
                        wordIndex,
                        string.Empty,
                        isScriptArgument);
                    if (isSwitchTable)
                    {
                        buffer[column] = ' ';
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.SwitchTable, '}'));
                    }
                    else if (isScriptArgument)
                    {
                        var scriptFrame = CreateTclScriptFrame(
                            frame,
                            '}',
                            isConcatenatedScriptArgument);
                        buffer[column] = !isConcatenatedScriptArgument || scriptFrame.CommandStart
                            ? ';'
                            : ' ';
                        frames.Push(scriptFrame);
                        suppressLeadingContinuedWord = false;
                    }
                    else if (isExpressionArgument)
                    {
                        buffer[column] = ' ';
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.ExpressionWord, '}'));
                    }
                    else
                    {
                        buffer[column] = ' ';
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.BracedWord, '}'));
                    }
                    frame.CommandStart = false;
                    frame.WordStart = false;
                    column++;
                    continue;
                }
                if (ch == ';')
                {
                    frame.ResetCommand();
                    suppressLeadingContinuedWord = false;
                    column++;
                    continue;
                }
                if (char.IsWhiteSpace(ch))
                {
                    frame.WordStart = true;
                    column++;
                    continue;
                }

                if (frame.WordStart)
                {
                    var wordIndex = frame.WordIndex++;
                    var token = ReadTclBareWord(line, column);
                    var isConcatenatedScriptArgument = token.Length > 0
                        && IsTclConcatenatedScriptArgument(frame, wordIndex, token);
                    var isScriptCommand = token.Length > 0
                        && (isConcatenatedScriptArgument
                            ? ProcessTclConcatenatedBareWord(frame, token)
                            : IsTclBareScriptCommandArgument(
                                frame,
                                wordIndex,
                                token,
                                lines,
                                new TclBraceEnd(lineIndex, column + token.Length - 1)));
                    if (wordIndex == 0)
                        frame.CommandName = token;
                    else
                    {
                        UpdateTclFirstArgument(frame, wordIndex, token);
                        UpdateTclDictArgumentState(frame, wordIndex, token);
                        UpdateTclSwitchArgumentState(frame, wordIndex, token);
                        UpdateTclTryArgumentState(
                            frame,
                            wordIndex,
                            token,
                            isScriptCommand);
                    }
                    if (token.Length > 0)
                    {
                        frame.LastBareWord = token;
                        frame.LastBareWordIndex = wordIndex;
                    }

                    if (suppressLeadingContinuedWord)
                    {
                        FillWithSpaces(buffer, column, column + token.Length);
                        suppressLeadingContinuedWord = false;
                    }
                    else if (isScriptCommand)
                    {
                        MarkTclBareScriptCommandBoundary(buffer, column);
                    }
                }

                frame.CommandStart = false;
                frame.WordStart = false;
                column++;
            }

            result[lineIndex] = new string(buffer);
            if (!lineContinued && frames.Peek().Kind == TclLexicalFrameKind.Script)
                frames.Peek().ResetCommand();
        }

        return result;
    }

}
