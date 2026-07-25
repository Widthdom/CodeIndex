using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct PythonLogicalHeaderReferenceLine(
        string Text,
        int SinglePhysicalLine,
        int SinglePhysicalColumn,
        int[]? PhysicalLines,
        int[]? PhysicalColumns);

    private static bool TryBuildPythonLogicalHeaderReferenceLine(
        string[] lines,
        int startLineIndex,
        int startColumn,
        out PythonLogicalHeaderReferenceLine header)
    {
        var builder = new StringBuilder(GetPythonLogicalLineInitialCapacity(lines, startLineIndex, startColumn));
        List<int>? physicalLines = null;
        List<int>? physicalColumns = null;
        var singlePhysicalLine = -1;
        var singlePhysicalColumn = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var inString = false;
        var quote = '\0';

        for (var lineIndex = startLineIndex; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var column = lineIndex == startLineIndex ? startColumn : FindFirstNonWhitespaceColumn(line);
            var fragmentEndColumn = FindPythonCommentColumn(line, column);
            if (column < fragmentEndColumn)
            {
                if (builder.Length > 0)
                {
                    if (!TryAppendPythonLogicalReferenceChar(builder, ref singlePhysicalLine, ref singlePhysicalColumn, ref physicalLines, ref physicalColumns, ' ', lineIndex, column, out header))
                        return false;
                }

                for (var fragmentColumn = column; fragmentColumn < fragmentEndColumn; fragmentColumn++)
                {
                    var fragmentChar = line[fragmentColumn];
                    if (fragmentChar == '\\' && fragmentColumn == fragmentEndColumn - 1)
                        break;

                    if (!TryAppendPythonLogicalReferenceChar(builder, ref singlePhysicalLine, ref singlePhysicalColumn, ref physicalLines, ref physicalColumns, fragmentChar, lineIndex, fragmentColumn, out header))
                        return false;
                }
            }

            for (var scan = column; scan < line.Length; scan++)
            {
                var ch = line[scan];
                if (inString)
                {
                    if (ch == '\\')
                    {
                        scan++;
                        continue;
                    }

                    if (ch == quote)
                        inString = false;
                    continue;
                }

                if (ch is '\'' or '"')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }

                if (ch == '#')
                    break;
                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (ch == ':' && parenDepth == 0 && bracketDepth == 0)
                {
                    header = CreatePythonLogicalHeaderReferenceLine(builder, singlePhysicalLine, singlePhysicalColumn, physicalLines, physicalColumns);
                    return header.Text.Length > 0;
                }
            }

            if (parenDepth == 0 && bracketDepth == 0 && !HasPythonLineContinuationBackslash(line))
                break;
        }

        header = CreatePythonLogicalHeaderReferenceLine(builder, singlePhysicalLine, singlePhysicalColumn, physicalLines, physicalColumns);
        return header.Text.Length > 0;
    }

    private static bool TryBuildPythonLogicalStatementReferenceLine(
        string[] lines,
        int startLineIndex,
        int startColumn,
        out PythonLogicalHeaderReferenceLine header)
    {
        var builder = new StringBuilder(GetPythonLogicalLineInitialCapacity(lines, startLineIndex, startColumn));
        List<int>? physicalLines = null;
        List<int>? physicalColumns = null;
        var singlePhysicalLine = -1;
        var singlePhysicalColumn = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var inString = false;
        var quote = '\0';

        for (var lineIndex = startLineIndex; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var column = lineIndex == startLineIndex ? startColumn : FindFirstNonWhitespaceColumn(line);
            var fragmentEndColumn = FindPythonCommentColumn(line, column);
            if (column < fragmentEndColumn)
            {
                if (builder.Length > 0)
                {
                    if (!TryAppendPythonLogicalReferenceChar(builder, ref singlePhysicalLine, ref singlePhysicalColumn, ref physicalLines, ref physicalColumns, ' ', lineIndex, column, out header))
                        return false;
                }

                for (var fragmentColumn = column; fragmentColumn < fragmentEndColumn; fragmentColumn++)
                {
                    var fragmentChar = line[fragmentColumn];
                    if (fragmentChar == '\\' && fragmentColumn == fragmentEndColumn - 1)
                        break;

                    if (!TryAppendPythonLogicalReferenceChar(builder, ref singlePhysicalLine, ref singlePhysicalColumn, ref physicalLines, ref physicalColumns, fragmentChar, lineIndex, fragmentColumn, out header))
                        return false;
                }
            }

            for (var scan = column; scan < line.Length; scan++)
            {
                var ch = line[scan];
                if (inString)
                {
                    if (ch == '\\')
                    {
                        scan++;
                        continue;
                    }

                    if (ch == quote)
                        inString = false;
                    continue;
                }

                if (ch is '\'' or '"')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }

                if (ch == '#')
                    break;
                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
            }

            if (parenDepth == 0 && bracketDepth == 0 && !HasPythonLineContinuationBackslash(line))
                break;
        }

        header = CreatePythonLogicalHeaderReferenceLine(builder, singlePhysicalLine, singlePhysicalColumn, physicalLines, physicalColumns);
        return header.Text.Length > 0;
    }

    private static PythonLogicalHeaderReferenceLine CreatePythonLogicalHeaderReferenceLine(
        StringBuilder builder,
        int singlePhysicalLine,
        int singlePhysicalColumn,
        List<int>? physicalLines,
        List<int>? physicalColumns)
    {
        if (physicalLines == null || physicalColumns == null)
            return new PythonLogicalHeaderReferenceLine(builder.ToString(), singlePhysicalLine, singlePhysicalColumn, null, null);

        return new PythonLogicalHeaderReferenceLine(
            builder.ToString(),
            singlePhysicalLine,
            singlePhysicalColumn,
            physicalLines.ToArray(),
            physicalColumns.ToArray());
    }

    private static int GetPythonLogicalLineInitialCapacity(string[] lines, int startLineIndex, int startColumn)
    {
        if (startLineIndex < 0 || startLineIndex >= lines.Length)
            return 0;

        return Math.Min(256, Math.Max(0, lines[startLineIndex].Length - startColumn));
    }

    private static bool HasPythonLineContinuationBackslash(string line)
    {
        for (var index = line.Length - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(line[index]))
                continue;

            return line[index] == '\\';
        }

        return false;
    }

    private static bool TryAppendPythonLogicalReferenceChar(
        StringBuilder builder,
        ref int singlePhysicalLine,
        ref int singlePhysicalColumn,
        ref List<int>? physicalLines,
        ref List<int>? physicalColumns,
        char value,
        int physicalLine,
        int physicalColumn,
        out PythonLogicalHeaderReferenceLine header)
    {
        if (builder.Length >= MaxPythonLogicalReferenceLineLength)
        {
            header = default;
            return false;
        }

        if (builder.Length == 0)
        {
            singlePhysicalLine = physicalLine;
            singlePhysicalColumn = physicalColumn;
        }
        else if (physicalLines == null
            && (physicalLine != singlePhysicalLine
                || physicalColumn != singlePhysicalColumn + builder.Length))
        {
            physicalLines = new List<int>(builder.Length + 1);
            physicalColumns = new List<int>(builder.Length + 1);
            for (var index = 0; index < builder.Length; index++)
            {
                physicalLines.Add(singlePhysicalLine);
                physicalColumns.Add(singlePhysicalColumn + index);
            }
        }

        builder.Append(value);
        if (physicalLines != null)
        {
            physicalLines.Add(physicalLine);
            physicalColumns!.Add(physicalColumn);
        }

        header = default;
        return true;
    }

    private static int FindPythonCommentColumn(string line, int startColumn)
    {
        var inString = false;
        var quote = '\0';
        for (var index = startColumn; index < line.Length; index++)
        {
            var ch = line[index];
            if (inString)
            {
                if (ch == '\\')
                {
                    index++;
                    continue;
                }

                if (ch == quote)
                    inString = false;
                continue;
            }

            if (ch is '\'' or '"')
            {
                inString = true;
                quote = ch;
                continue;
            }

            if (ch == '#')
                return index;
        }

        return line.Length;
    }

    private static int FindFirstNonWhitespaceColumn(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        return index;
    }

    private static void RemapPythonLogicalHeaderReferences(
        List<ReferenceRecord> references,
        int startIndex,
        PythonLogicalHeaderReferenceLine header,
        string[] lines)
    {
        for (var i = startIndex; i < references.Count; i++)
        {
            var logicalIndex = references[i].Column - 1;
            var logicalLength = header.PhysicalLines?.Length ?? header.Text.Length;
            if (logicalIndex < 0 || logicalIndex >= logicalLength)
                continue;

            var physicalLineIndex = header.SinglePhysicalLine;
            var physicalColumn = header.SinglePhysicalColumn + logicalIndex;
            if (header.PhysicalLines is { } physicalLines && header.PhysicalColumns is { } physicalColumns)
            {
                physicalLineIndex = physicalLines[logicalIndex];
                physicalColumn = physicalColumns[logicalIndex];
            }

            if (physicalLineIndex < 0)
                continue;

            references[i].Line = physicalLineIndex + 1;
            references[i].Column = physicalColumn + 1;
            references[i].Context = lines[physicalLineIndex].Trim();
        }
    }

    private static (
        IReadOnlyDictionary<(int Line, string Kind), SymbolRecord>? DefinitionContainersByLineAndKind,
        IReadOnlyDictionary<int, SymbolRecord>? HeaderSymbolsByLine) BuildPythonSymbolLookups(IReadOnlyList<SymbolRecord> symbols)
    {
        Dictionary<(int Line, string Kind), SymbolRecord>? containers = null;
        Dictionary<int, SymbolRecord>? symbolsByLine = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is "class" or "function")
                (containers ??= []).TryAdd((symbol.Line, symbol.Kind), symbol);

            if (symbol.Signature == null
                || symbol.Kind is not ("function" or "class" or "property" or "class_hook"))
                continue;

            (symbolsByLine ??= []).TryAdd(symbol.Line, symbol);
        }

        return (containers, symbolsByLine);
    }

    private static bool IsJsxFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".jsx".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tsx".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySkipTypeScriptJsxTypeArguments(string preparedLine, ref int scan)
    {
        if (scan >= preparedLine.Length || preparedLine[scan] != '<')
            return false;

        var depth = 0;
        while (scan < preparedLine.Length)
        {
            var ch = preparedLine[scan++];
            if (ch == '\'' || ch == '"')
            {
                while (scan < preparedLine.Length)
                {
                    var quoted = preparedLine[scan++];
                    if (quoted == '\\')
                    {
                        scan = Math.Min(scan + 1, preparedLine.Length);
                        continue;
                    }

                    if (quoted == ch)
                        break;
                }

                continue;
            }

            if (ch == '=' && scan < preparedLine.Length && preparedLine[scan] == '>')
            {
                scan++;
                continue;
            }

            if (ch == '<')
            {
                depth++;
            }
            else if (ch == '>')
            {
                depth--;
                if (depth == 0)
                    return true;
                if (depth < 0)
                    return false;
            }
        }

        return false;
    }

    private static bool IsRazorFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".razor".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cshtml".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObjCSelectorLiteralCall(string line, string name, int nameIndex) =>
        string.Equals(NormalizeAtPrefixedIdentifier(name), "selector", StringComparison.Ordinal)
        && (name.StartsWith('@') || nameIndex > 0 && line[nameIndex - 1] == '@');

    /// <summary>
    /// Emit one `type_reference` row per dot-segment of a captured argument. Columns are
    /// computed relative to the original line so tooling can jump to the exact identifier.
    /// 捕捉した引数の dot-segment ごとに `type_reference` 行を発行する。列位置は元の行基準で計算する。
    /// </summary>
}
