using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    internal static string[] MaskLispCodeLines(IReadOnlyList<string> lines)
    {
        var maskedLines = new string[lines.Count];
        var blockCommentDepth = 0;
        var inString = false;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var chars = lines[lineIndex].ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (blockCommentDepth > 0)
                {
                    if (chars[i] == '#' && i + 1 < chars.Length && chars[i + 1] == '|')
                    {
                        chars[i++] = ' ';
                        chars[i] = ' ';
                        blockCommentDepth++;
                        continue;
                    }

                    if (chars[i] == '|' && i + 1 < chars.Length && chars[i + 1] == '#')
                    {
                        chars[i++] = ' ';
                        chars[i] = ' ';
                        blockCommentDepth--;
                        continue;
                    }

                    chars[i] = ' ';
                    continue;
                }

                if (inString)
                {
                    if (chars[i] == '\\' && i + 1 < chars.Length)
                    {
                        chars[i++] = ' ';
                        chars[i] = ' ';
                        continue;
                    }

                    var closesString = chars[i] == '"';
                    chars[i] = ' ';
                    if (closesString)
                        inString = false;
                    continue;
                }

                if (chars[i] == ';')
                {
                    for (var j = i; j < chars.Length; j++)
                        chars[j] = ' ';
                    break;
                }

                if (chars[i] == '#' && i + 1 < chars.Length && chars[i + 1] == '|')
                {
                    chars[i++] = ' ';
                    chars[i] = ' ';
                    blockCommentDepth++;
                    continue;
                }

                if (chars[i] == '"')
                {
                    chars[i] = ' ';
                    inString = true;
                }
            }

            maskedLines[lineIndex] = new string(chars);
        }

        return maskedLines;
    }

    private static List<SymbolRecord> ExtractLispSymbols(long fileId, string language, string[] lines)
    {
        var maskedLines = MaskLispCodeLines(lines);
        var symbols = new List<SymbolRecord>();

        for (var lineIndex = 0; lineIndex < maskedLines.Length; lineIndex++)
        {
            var maskedLine = maskedLines[lineIndex];
            for (var cursor = 0; cursor < maskedLine.Length; cursor++)
            {
                if (maskedLine[cursor] != '(' || IsQuotedLispForm(maskedLine, cursor))
                    continue;

                if (!TryReadLispListHead(maskedLine, cursor, out var head, out _, out var afterHead))
                    continue;

                if (!TryCreateLispSymbol(language, maskedLine, head, afterHead, out var kind, out var name, out var nameIndex))
                    continue;

                var lineNumber = lineIndex + 1;
                var (endLine, bodyStartLine, bodyEndLine) = FindLispFormRange(maskedLines, lineIndex, cursor, kind);
                AddSymbolRecord(
                    symbols,
                    null,
                    lineNumber,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = kind,
                        Name = name,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        StartColumn = nameIndex,
                        EndLine = endLine,
                        BodyStartLine = bodyStartLine,
                        BodyEndLine = bodyEndLine,
                        Signature = lines[lineIndex].Trim(),
                    },
                    lines[lineIndex]);
            }
        }

        AssignContainers(symbols, lines, null);
        PopulateDeclaredContainerQualifiedNames(symbols);
        return symbols;
    }

    private static bool TryCreateLispSymbol(
        string language,
        string line,
        string head,
        int afterHead,
        out string kind,
        out string name,
        out int nameIndex)
    {
        kind = string.Empty;
        name = string.Empty;
        nameIndex = -1;

        if (language == "commonlisp")
            return TryCreateCommonLispSymbol(line, head, afterHead, out kind, out name, out nameIndex);
        if (language == "racket")
            return TryCreateRacketSymbol(line, head, afterHead, out kind, out name, out nameIndex);
        return false;
    }

    private static bool TryCreateCommonLispSymbol(
        string line,
        string head,
        int afterHead,
        out string kind,
        out string name,
        out int nameIndex)
    {
        kind = head.ToLowerInvariant() switch
        {
            "defpackage" => "namespace",
            "in-package" or "use-package" or "import" or "shadowing-import" => "import",
            "defclass" or "define-condition" => "class",
            "defstruct" => "struct",
            "defparameter" or "defvar" or "defconstant" => "property",
            "defun" or "defmacro" or "defgeneric" or "defmethod" or "define-compiler-macro"
                or "define-modify-macro" or "defsetf" => "function",
            _ => string.Empty,
        };

        if (kind.Length == 0)
        {
            name = string.Empty;
            nameIndex = -1;
            return false;
        }

        return TryReadLispDefinitionName(line, afterHead, out name, out nameIndex);
    }

    private static bool TryCreateRacketSymbol(
        string line,
        string head,
        int afterHead,
        out string kind,
        out string name,
        out int nameIndex)
    {
        var normalizedHead = head.ToLowerInvariant();
        kind = normalizedHead switch
        {
            "module" or "module*" or "module+" => "namespace",
            "require" or "provide" => "import",
            "struct" or "define-struct" => "struct",
            "class" => "class",
            _ => string.Empty,
        };

        if (kind.Length == 0 && normalizedHead.StartsWith("define", StringComparison.Ordinal))
        {
            kind = IsRacketFunctionDefinition(line, afterHead) ? "function" : "property";
        }

        if (kind.Length == 0)
        {
            name = string.Empty;
            nameIndex = -1;
            return false;
        }

        return TryReadLispDefinitionName(line, afterHead, out name, out nameIndex);
    }

    private static bool IsRacketFunctionDefinition(string line, int cursor)
    {
        cursor = SkipLispWhitespace(line, cursor);
        cursor = SkipLispQuotePrefixes(line, cursor);
        return cursor < line.Length && line[cursor] == '(';
    }

    private static bool TryReadLispDefinitionName(string line, int cursor, out string name, out int nameIndex)
    {
        name = string.Empty;
        nameIndex = -1;

        cursor = SkipLispWhitespace(line, cursor);
        cursor = SkipLispQuotePrefixes(line, cursor);
        if (cursor >= line.Length)
            return false;

        if (line[cursor] == '(')
        {
            if (!TryReadLispListHead(line, cursor, out var innerHead, out var innerHeadIndex, out var afterInnerHead))
                return false;

            if (string.Equals(innerHead, "setf", StringComparison.OrdinalIgnoreCase)
                && TryReadLispSymbolToken(line, afterInnerHead, out var setfName, out var setfNameIndex, out _))
            {
                name = setfName;
                nameIndex = setfNameIndex;
                return true;
            }

            name = innerHead;
            nameIndex = innerHeadIndex;
            return true;
        }

        return TryReadLispSymbolToken(line, cursor, out name, out nameIndex, out _);
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindLispFormRange(
        IReadOnlyList<string> maskedLines,
        int startLineIndex,
        int startColumn,
        string kind)
    {
        var depth = 0;
        var started = false;

        for (var lineIndex = startLineIndex; lineIndex < maskedLines.Count; lineIndex++)
        {
            var line = maskedLines[lineIndex];
            var column = lineIndex == startLineIndex ? startColumn : 0;
            for (; column < line.Length; column++)
            {
                if (line[column] == '(')
                {
                    depth++;
                    started = true;
                }
                else if (line[column] == ')' && started)
                {
                    depth--;
                    if (depth == 0)
                        return BuildLispRange(startLineIndex, lineIndex, kind);
                }
            }
        }

        return BuildLispRange(startLineIndex, maskedLines.Count - 1, kind);
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) BuildLispRange(
        int startLineIndex,
        int endLineIndex,
        string kind)
    {
        var startLine = startLineIndex + 1;
        var endLine = Math.Max(startLine, endLineIndex + 1);
        if (kind is "namespace" or "class" or "struct" or "function")
            return (endLine, startLine, endLine);
        return (endLine, null, null);
    }

    internal static bool TryReadLispListHead(string line, int openParenIndex, out string head, out int headIndex, out int afterHead)
    {
        head = string.Empty;
        headIndex = -1;
        afterHead = openParenIndex;

        if (openParenIndex < 0 || openParenIndex >= line.Length || line[openParenIndex] != '(')
            return false;

        var cursor = SkipLispWhitespace(line, openParenIndex + 1);
        if (!TryReadLispSymbolToken(line, cursor, out head, out headIndex, out afterHead))
            return false;

        return true;
    }

    internal static bool TryReadLispSymbolToken(string line, int cursor, out string token, out int tokenIndex, out int afterToken)
    {
        token = string.Empty;
        tokenIndex = -1;
        afterToken = cursor;

        cursor = SkipLispWhitespace(line, cursor);
        cursor = SkipLispQuotePrefixes(line, cursor);
        if (cursor >= line.Length || !IsLispSymbolChar(line[cursor]))
            return false;

        var start = cursor;
        while (cursor < line.Length && IsLispSymbolChar(line[cursor]))
            cursor++;

        token = line[start..cursor];
        tokenIndex = start;
        afterToken = cursor;
        return token.Length > 0;
    }

    internal static int SkipLispWhitespace(string line, int cursor)
    {
        while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
            cursor++;
        return cursor;
    }

    private static int SkipLispQuotePrefixes(string line, int cursor)
    {
        while (cursor < line.Length)
        {
            if (line[cursor] is '\'' or '`')
            {
                cursor++;
                continue;
            }

            if (line[cursor] == ',')
            {
                cursor++;
                if (cursor < line.Length && line[cursor] == '@')
                    cursor++;
                continue;
            }

            if (line[cursor] == '#' && cursor + 1 < line.Length && line[cursor + 1] == '\'')
            {
                cursor += 2;
                continue;
            }

            break;
        }

        return cursor;
    }

    private static bool IsQuotedLispForm(string line, int openParenIndex)
    {
        var cursor = openParenIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(line[cursor]))
            cursor--;

        if (cursor < 0)
            return false;
        if (line[cursor] is '\'' or '`' or ',')
            return true;
        return line[cursor] == '\'' || (line[cursor] == '#' && cursor + 1 == openParenIndex);
    }

    private static bool IsLispSymbolChar(char ch)
        => !char.IsWhiteSpace(ch)
           && ch is not '(' and not ')' and not '"' and not ';';
}
