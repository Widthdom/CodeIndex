using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class DynamicDeclarativeReferenceExtractor
{
    private static bool HasTclEscapedNewline(string line)
    {
        var backslashCount = 0;
        for (var column = line.Length - 1; column >= 0 && line[column] == '\\'; column--)
            backslashCount++;
        return backslashCount % 2 == 1;
    }

    private static int FindTclCommentStart(string line)
    {
        var commandStart = true;
        for (var column = 0; column < line.Length; column++)
        {
            var ch = line[column];
            if (ch is '\'' or '"')
            {
                column = SkipQuotedToken(line, column, ch) - 1;
                commandStart = false;
                continue;
            }
            if (ch == '\\')
            {
                column++;
                commandStart = false;
                continue;
            }
            if (ch == '#' && commandStart)
                return column;
            if (ch is ';' or '[')
            {
                commandStart = true;
                continue;
            }
            if (char.IsWhiteSpace(ch))
                continue;
            commandStart = false;
        }

        return -1;
    }

    private static string ReadTclBareWord(string line, int startColumn)
    {
        var endColumn = startColumn;
        while (endColumn < line.Length
            && (char.IsLetterOrDigit(line[endColumn])
                || line[endColumn] is '_' or ':' or '.' or '-' or '#'))
        {
            endColumn++;
        }

        return endColumn == startColumn
            ? string.Empty
            : line[startColumn..endColumn];
    }

    private static bool IsTclScriptArgument(
        TclLexicalFrame frame,
        int wordIndex,
        IReadOnlyList<string> lines,
        TclBraceEnd? braceEnd)
    {
        var isLastCommandWord = braceEnd is { } end
            && IsTclLastCommandWord(lines, end);
        return frame.CommandName switch
        {
            "if" => wordIndex == 2
                || (frame.LastBareWord == "then"
                    && wordIndex == frame.LastBareWordIndex + 1)
                || (frame.LastBareWord == "elseif"
                    && wordIndex == frame.LastBareWordIndex + 2)
                || (frame.LastBareWord == "else"
                    && wordIndex == frame.LastBareWordIndex + 1),
            "foreach" or "lmap" => wordIndex >= 3
                && wordIndex % 2 == 1
                && isLastCommandWord,
            "while" => wordIndex == 2,
            "catch" => wordIndex == 1,
            "for" => wordIndex is 1 or 3 or 4,
            "proc" => wordIndex == 3,
            "try" => wordIndex == frame.TryScriptWordIndex,
            "dict" => wordIndex == frame.DictScriptWordIndex,
            "switch" => frame.SwitchStringWordIndex >= 0
                && wordIndex - frame.SwitchStringWordIndex >= 2
                && (wordIndex - frame.SwitchStringWordIndex) % 2 == 0,
            _ => false,
        };
    }

    private static bool IsTclExpressionArgument(TclLexicalFrame frame, int wordIndex) =>
        frame.CommandName switch
        {
            "if" => wordIndex == 1
                || (frame.LastBareWord == "elseif"
                    && wordIndex == frame.LastBareWordIndex + 1),
            "while" => wordIndex == 1,
            "for" => wordIndex == 2,
            "expr" => wordIndex >= 1,
            _ => false,
        };

    private static bool IsTclBareScriptCommandArgument(
        TclLexicalFrame frame,
        int wordIndex,
        string token,
        IReadOnlyList<string> lines,
        TclBraceEnd wordEnd)
    {
        if (frame.CommandName == "if" && token == "then")
            return false;

        return IsTclScriptArgument(
            frame,
            wordIndex,
            lines,
            wordEnd);
    }

    private static bool IsTclConcatenatedScriptArgument(
        TclLexicalFrame frame,
        int wordIndex,
        string? token)
    {
        if (frame.CommandName == "eval")
            return wordIndex >= 1;
        if (frame.CommandName == "after")
        {
            return wordIndex >= 2
                && frame.FirstArgument is not null
                && frame.FirstArgument is not ("cancel" or "info");
        }
        if (frame.CommandName == "namespace")
            return wordIndex >= 3 && frame.FirstArgument == "eval";
        if (frame.CommandName != "uplevel")
            return false;
        if (frame.UplevelScriptWordIndex >= 0)
            return wordIndex >= frame.UplevelScriptWordIndex;
        if (wordIndex < 1)
            return false;

        if (wordIndex == 1 && IsTclUplevelLevelToken(token))
        {
            frame.UplevelScriptWordIndex = 2;
            return false;
        }

        frame.UplevelScriptWordIndex = wordIndex;
        return true;
    }

    private static bool IsTclUplevelLevelToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var span = token.AsSpan().Trim();
        if (span.Length > 1 && span[0] == '#')
            span = span[1..];
        if (span.IsEmpty)
            return false;

        var start = span[0] is '+' or '-' ? 1 : 0;
        if (start == span.Length)
            return false;
        for (var index = start; index < span.Length; index++)
        {
            if (!char.IsDigit(span[index]))
                return false;
        }

        return true;
    }

    private static TclLexicalFrame CreateTclScriptFrame(
        TclLexicalFrame owner,
        char terminator,
        bool concatenateArguments)
    {
        var frame = new TclLexicalFrame(
            TclLexicalFrameKind.Script,
            terminator,
            concatenateArguments ? owner : null);
        if (!concatenateArguments)
            return frame;

        if (owner.ConcatenatedScriptState != null)
            frame.CopyCommandStateFrom(owner.ConcatenatedScriptState);
        // Tcl inserts a separating space while concatenating eval/uplevel arguments.
        // eval/uplevel の引数連結では引数間に空白が入るため、次は word boundary。
        frame.WordStart = true;
        return frame;
    }

    private static void PersistTclConcatenatedScriptState(TclLexicalFrame frame)
    {
        if (frame.ConcatenationOwner is not { } owner)
            return;

        owner.ConcatenatedScriptState ??= new TclLexicalFrame(TclLexicalFrameKind.Script);
        owner.ConcatenatedScriptState.CopyCommandStateFrom(frame);
        owner.ConcatenatedScriptState.WordStart = true;
    }

    private static bool ProcessTclConcatenatedBareWord(
        TclLexicalFrame owner,
        string token)
    {
        owner.ConcatenatedScriptState ??= new TclLexicalFrame(TclLexicalFrameKind.Script);
        var state = owner.ConcatenatedScriptState;
        var isCommand = state.CommandStart;
        var wordIndex = state.WordIndex++;
        if (wordIndex == 0)
        {
            state.CommandName = token;
        }
        else
        {
            UpdateTclFirstArgument(state, wordIndex, token);
            UpdateTclDictArgumentState(state, wordIndex, token);
            UpdateTclSwitchArgumentState(state, wordIndex, token);
            UpdateTclTryArgumentState(
                state,
                wordIndex,
                token,
                isScriptArgument: false);
        }

        state.LastBareWord = token;
        state.LastBareWordIndex = wordIndex;
        state.CommandStart = false;
        state.WordStart = false;
        return isCommand;
    }

    private static string? GetTclBracedWordToken(
        IReadOnlyList<string> lines,
        int startLine,
        int startColumn,
        TclBraceEnd? braceEnd)
    {
        if (braceEnd is not { } end || end.Line != startLine)
            return null;
        var length = end.Column - startColumn - 1;
        return length < 0 ? null : lines[startLine].Substring(startColumn + 1, length);
    }

    private static string? GetTclQuotedWordToken(string line, int startColumn)
    {
        var endColumn = SkipQuotedToken(line, startColumn, '"');
        return endColumn <= startColumn + 1
            || endColumn > line.Length
            || line[endColumn - 1] != '"'
                ? null
                : line.Substring(startColumn + 1, endColumn - startColumn - 2);
    }

    private static string NormalizeTclQualifiedName(string name)
    {
        while (name.StartsWith("::", StringComparison.Ordinal))
            name = name[2..];
        return name;
    }

    private static void UpdateTclFirstArgument(
        TclLexicalFrame frame,
        int wordIndex,
        string? token)
    {
        if (wordIndex == 1 && token != null)
            frame.FirstArgument = token;
    }

    private static void UpdateTclDictArgumentState(
        TclLexicalFrame frame,
        int wordIndex,
        string? token)
    {
        if (frame.CommandName == "dict"
            && wordIndex == 1
            && token == "for")
        {
            frame.DictScriptWordIndex = wordIndex + 3;
        }
    }

    private static void MarkTclBareScriptCommandBoundary(char[] buffer, int commandColumn)
    {
        var boundaryColumn = commandColumn - 1;
        if (boundaryColumn >= 0 && char.IsWhiteSpace(buffer[boundaryColumn]))
            buffer[boundaryColumn] = ';';
    }

    private static bool IsTclSwitchTableArgument(
        TclLexicalFrame frame,
        int wordIndex,
        IReadOnlyList<string> lines,
        TclBraceEnd? braceEnd)
    {
        return frame.CommandName == "switch"
            && frame.SwitchStringWordIndex >= 0
            && wordIndex == frame.SwitchStringWordIndex + 1
            && braceEnd is { } end
            && IsTclLastCommandWord(lines, end);
    }

    private static void UpdateTclSwitchArgumentState(
        TclLexicalFrame frame,
        int wordIndex,
        string token)
    {
        if (frame.CommandName != "switch"
            || wordIndex == 0
            || frame.SwitchStringWordIndex >= 0)
        {
            return;
        }

        if (frame.SwitchOptionValuePending)
        {
            frame.SwitchOptionValuePending = false;
            return;
        }

        if (!frame.SwitchOptionsEnded && token.StartsWith("-", StringComparison.Ordinal))
        {
            if (token == "--")
                frame.SwitchOptionsEnded = true;
            else if (token is "-matchvar" or "-indexvar")
                frame.SwitchOptionValuePending = true;
            return;
        }

        frame.SwitchStringWordIndex = wordIndex;
    }

    private static void UpdateTclTryArgumentState(
        TclLexicalFrame frame,
        int wordIndex,
        string token,
        bool isScriptArgument)
    {
        if (frame.CommandName != "try")
            return;

        if (isScriptArgument)
        {
            frame.TryClauseWordIndex = wordIndex + 1;
            frame.TryScriptWordIndex = -1;
            return;
        }

        if (wordIndex != frame.TryClauseWordIndex)
            return;

        frame.TryScriptWordIndex = token switch
        {
            "on" or "trap" => wordIndex + 3,
            "finally" => wordIndex + 1,
            _ => -1,
        };
    }

    private static bool IsTclLastCommandWord(
        IReadOnlyList<string> lines,
        TclBraceEnd braceEnd)
    {
        var line = lines[braceEnd.Line];
        for (var column = braceEnd.Column + 1; column < line.Length; column++)
        {
            if (char.IsWhiteSpace(line[column]))
                continue;
            return line[column] is ';' or ']' or '}';
        }

        return true;
    }

    private static long GetTclPositionKey(int line, int column) =>
        ((long)line << 32) | (uint)column;

}
