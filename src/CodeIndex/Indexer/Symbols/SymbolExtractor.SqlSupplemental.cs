using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{

    private static void ExtractSqlCteSymbols(
        long fileId,
        string content,
        string[] lines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        if (!LinesContain(lines, "WITH", StringComparison.OrdinalIgnoreCase))
            return;
        if (!LinesContain(lines, "AS", StringComparison.OrdinalIgnoreCase))
            return;

        List<int>? lineStarts = null;
        foreach (Match match in Regex.EnumerateMatches(SqlCteDefinitionRegex, content))
        {
            var nameGroup = match.Groups["name"];
            var name = NormalizeSqlIdentifierSegment(nameGroup.Value);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var currentLineStarts = lineStarts ??= BuildLineStartList(lines);
            var lineNumber = GetLineNumberFromOffset(currentLineStarts, nameGroup.Index);
            AddSymbolRecord(
                symbols,
                extractionState,
                null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = name,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = nameGroup.Index - currentLineStarts[lineNumber - 1],
                    EndLine = lineNumber,
                    Signature = lines[lineNumber - 1].Trim(),
                });
        }
    }

    private static bool LinesContain(IReadOnlyList<string> lines, string value, StringComparison comparison)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].IndexOf(value, comparison) >= 0)
                return true;
        }

        return false;
    }

    private static bool LinesContain(IReadOnlyList<string> lines, char value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].IndexOf(value) >= 0)
                return true;
        }

        return false;
    }

    private static bool LinesContainAny(
        IReadOnlyList<string> lines,
        string value1,
        string value2,
        string value3,
        StringComparison comparison)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IndexOf(value1, comparison) >= 0
                || line.IndexOf(value2, comparison) >= 0
                || line.IndexOf(value3, comparison) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LinesContainAny(
        IReadOnlyList<string> lines,
        string value1,
        string value2,
        string value3,
        string value4,
        StringComparison comparison)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IndexOf(value1, comparison) >= 0
                || line.IndexOf(value2, comparison) >= 0
                || line.IndexOf(value3, comparison) >= 0
                || line.IndexOf(value4, comparison) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LinesContainAny(
        IReadOnlyList<string> lines,
        char value1,
        string value2,
        StringComparison comparison)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IndexOf(value1) >= 0
                || line.IndexOf(value2, comparison) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static List<int> BuildLineStartList(IReadOnlyList<string> lines)
    {
        if (lines.Count <= 1)
            return [0];

        var starts = new List<int>(Math.Max(1, lines.Count)) { 0 };
        var offset = 0;
        for (var i = 0; i < lines.Count - 1; i++)
        {
            offset += lines[i].Length + 1;
            starts.Add(offset);
        }

        return starts;
    }

    private static int GetLineNumberFromOffset(List<int> lineStarts, int offset)
    {
        var index = lineStarts.BinarySearch(offset);
        if (index >= 0)
            return index + 1;

        return ~index;
    }

    private static void ExtractSqlGeneratedColumnSymbols(
        long fileId,
        string[] lines,
        string[] structuralLines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        if (!LinesContainAny(
            structuralLines,
            "GENERATED",
            "NEXT VALUE FOR",
            " AS ",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TryGetSqlGeneratedColumnContainerMarkers(
            structuralLines,
            out var hasAlterAdd,
            out var hasCreateTable))
        {
            return;
        }

        var structuralContent = string.Join('\n', structuralLines);
        List<int>? lineStarts = null;
        if (hasAlterAdd)
        {
            foreach (Match match in Regex.EnumerateMatches(SqlAlterTableAddGeneratedColumnRegex, structuralContent))
            {
                var nameGroup = match.Groups["name"];
                var currentLineStarts = lineStarts ??= BuildLineStartList(structuralLines);
                AddSqlGeneratedColumnSymbol(
                    fileId,
                    lines,
                    currentLineStarts,
                    new GroupProxy(nameGroup.Value, nameGroup.Index),
                    match.Groups["table"].Value,
                    symbols,
                    extractionState);
            }
        }

        if (!hasCreateTable)
            return;

        foreach (Match tableMatch in Regex.EnumerateMatches(SqlCreateTableBodyRegex, structuralContent))
        {
            var tableName = tableMatch.Groups["table"].Value;
            var bodyGroup = tableMatch.Groups["body"];
            foreach (var column in EnumerateSqlColumnDefinitions(bodyGroup.Value, bodyGroup.Index))
            {
                if (!SqlGeneratedColumnDefinitionMarkerRegex.IsMatch(column.Text))
                    continue;

                var nameMatch = SqlColumnDefinitionNameRegex.Match(column.Text);
                if (!nameMatch.Success)
                    continue;

                var currentLineStarts = lineStarts ??= BuildLineStartList(structuralLines);
                AddSqlGeneratedColumnSymbol(
                    fileId,
                    lines,
                    currentLineStarts,
                    new GroupProxy(nameMatch.Groups["name"].Value, column.StartIndex + nameMatch.Groups["name"].Index),
                    tableName,
                    symbols,
                    extractionState);
            }
        }
    }

    private static bool TryGetSqlGeneratedColumnContainerMarkers(
        IReadOnlyList<string> lines,
        out bool hasAlterAdd,
        out bool hasCreateTable)
    {
        var hasAlter = false;
        var hasAdd = false;
        var hasCreate = false;
        var hasTable = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            hasAlter |= line.IndexOf("ALTER", StringComparison.OrdinalIgnoreCase) >= 0;
            hasAdd |= line.IndexOf("ADD", StringComparison.OrdinalIgnoreCase) >= 0;
            hasCreate |= line.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) >= 0;
            hasTable |= line.IndexOf("TABLE", StringComparison.OrdinalIgnoreCase) >= 0;
            if (hasAlter && hasAdd && hasCreate && hasTable)
                break;
        }

        hasAlterAdd = hasAlter && hasAdd;
        hasCreateTable = hasCreate && hasTable;
        return hasAlterAdd || hasCreateTable;
    }

    private static void AddSqlGeneratedColumnSymbol(
        long fileId,
        string[] lines,
        List<int> lineStarts,
        IGroupLike nameGroup,
        string rawTableName,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        var name = NormalizeSqlIdentifierSegment(nameGroup.Value);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var lineNumber = GetLineNumberFromOffset(lineStarts, nameGroup.Index);
        AddSymbolRecord(
            symbols,
            extractionState,
            null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                SubKind = "generated_column",
                Name = name,
                Line = lineNumber,
                StartLine = lineNumber,
                StartColumn = nameGroup.Index - lineStarts[lineNumber - 1],
                EndLine = lineNumber,
                Signature = lines[lineNumber - 1].Trim(),
                ContainerKind = "class",
                ContainerName = NormalizeSqlIdentifierSegment(SqlNameResolver.GetLeafName(rawTableName)),
            },
            lines[lineNumber - 1]);
    }

    private interface IGroupLike
    {
        string Value { get; }
        int Index { get; }
    }

    private readonly record struct GroupProxy(string Value, int Index) : IGroupLike;

    private readonly record struct SqlColumnDefinitionSlice(string Text, int StartIndex);

    private static IEnumerable<SqlColumnDefinitionSlice> EnumerateSqlColumnDefinitions(string body, int bodyStartIndex)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i <= body.Length; i++)
        {
            if (i == body.Length || (body[i] == ',' && depth == 0))
            {
                var text = body[start..i].Trim();
                if (text.Length > 0)
                    yield return new SqlColumnDefinitionSlice(text, bodyStartIndex + start + body[start..i].Length - body[start..i].TrimStart().Length);
                start = i + 1;
                continue;
            }

            if (body[i] == '(')
                depth++;
            else if (body[i] == ')' && depth > 0)
                depth--;
        }
    }

    private static string NormalizeSqlIdentifierSegment(string value)
    {
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
            return value[1..^1].Replace("]]", "]", StringComparison.Ordinal);
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        if (value.Length >= 2 && value[0] == '`' && value[^1] == '`')
            return value[1..^1];

        return value;
    }

    private static void ExtractSqlDefinerSymbols(
        long fileId,
        string[] lines,
        string[] structuralLines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (structuralLines[i].IndexOf("DEFINER", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (!SqlDefinerMarkerRegex.IsMatch(structuralLines[i]))
                continue;

            if (structuralLines[i].IndexOf('@') < 0)
                continue;

            var match = SqlDefinerRegex.Match(lines[i]);
            if (!match.Success)
                continue;

            var user = FirstSuccessfulGroupValue(match, "user1", "user2", "user3");
            var host = FirstSuccessfulGroupValue(match, "host1", "host2", "host3");
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(host))
                continue;

            var name = $"{user}@{host}";
            var lineNumber = i + 1;
            AddSymbolRecord(
                symbols,
                extractionState,
                null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "definer",
                    Name = name,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = match.Index,
                    EndLine = lineNumber,
                    Signature = lines[i].Trim(),
                },
                lines[i]);
        }
    }

    private static string[] MaskSqlSyntheticSymbolLines(string[] lines)
    {
        string[]? masked = null;
        var inBlockComment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var maskedLine = MaskSqlSyntheticSymbolLine(lines[i], ref inBlockComment);
            if (masked != null)
            {
                masked[i] = maskedLine;
                continue;
            }

            if (ReferenceEquals(maskedLine, lines[i]))
                continue;

            masked = (string[])lines.Clone();
            masked[i] = maskedLine;
        }

        return masked ?? lines;
    }

    private static string MaskSqlSyntheticSymbolLine(string line, ref bool inBlockComment)
    {
        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= line.ToCharArray())[index] = ' ';

        void MaskToEnd(int start)
        {
            var masked = chars ??= line.ToCharArray();
            for (var index = start; index < line.Length; index++)
                masked[index] = ' ';
        }

        var inSingleQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    MaskAt(i);
                    MaskAt(i + 1);
                    i++;
                    inBlockComment = false;
                }
                else
                {
                    MaskAt(i);
                }
                continue;
            }

            if (inSingleQuote)
            {
                if (line[i] == '\'' && i + 1 < line.Length && line[i + 1] == '\'')
                {
                    MaskAt(i);
                    MaskAt(i + 1);
                    i++;
                    continue;
                }

                if (line[i] == '\'')
                    inSingleQuote = false;
                MaskAt(i);
                continue;
            }

            if (line[i] == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                MaskToEnd(i);
                break;
            }

            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                MaskAt(i);
                MaskAt(i + 1);
                i++;
                inBlockComment = true;
                continue;
            }

            if (line[i] == '\'')
            {
                MaskAt(i);
                inSingleQuote = true;
            }
        }

        return chars is null ? line : new string(chars);
    }

    private static void ExtractSqlRoutineResultColumnSymbols(
        long fileId,
        string[] lines,
        string[] structuralLines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (structuralLines[i].IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (!SqlCreateRoutineHeaderRegex.IsMatch(structuralLines[i]))
                continue;

            var headerEnd = FindSqlRoutineHeaderEndLine(structuralLines, i);
            var header = LineRangeText.Join(structuralLines, i, headerEnd);
            var owner = FindSqlRoutineOwnerSymbol(symbols, i + 1, headerEnd + 1);
            var ownerName = owner?.Name;
            var ownerBodyStart = owner?.BodyStartLine;
            var ownerBodyEnd = owner?.BodyEndLine;
            var lineNumber = i + 1;

            if (header.Contains("RETURNS", StringComparison.OrdinalIgnoreCase)
                && header.Contains("TABLE", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var columns in EnumerateSqlReturnsTableColumnLists(header))
                {
                    foreach (var column in EnumerateSqlColumnDefinitions(columns))
                        AddSqlRoutineFieldSymbol(fileId, lines, symbols, extractionState, lineNumber, column.Name, column.Type, ownerName, ownerBodyStart, ownerBodyEnd);
                }
            }

            var parameterList = ExtractSqlRoutineParameterList(header);
            if (parameterList != null
                && parameterList.Contains("OUT", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in Regex.EnumerateMatches(SqlOutParameterRegex, parameterList))
                {
                    var rawName = match.Groups["name"].Value;
                    var name = NormalizeSqlSymbolSegment(rawName);
                    if (name.Length > 0)
                        AddSqlRoutineFieldSymbol(fileId, lines, symbols, extractionState, lineNumber, name, null, ownerName, ownerBodyStart, ownerBodyEnd);
                }
            }
        }
    }

    private static SymbolRecord? FindSqlRoutineOwnerSymbol(List<SymbolRecord> symbols, int startLine, int endLine)
    {
        SymbolRecord? owner = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || symbol.Line < startLine || symbol.Line > endLine)
                continue;

            if (owner == null || symbol.Line < owner.Line)
                owner = symbol;
        }

        return owner;
    }

    private static int FindSqlRoutineHeaderEndLine(string[] lines, int startLineIndex)
    {
        for (var i = startLineIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains(" AS ", StringComparison.OrdinalIgnoreCase)
                || line.Contains(" LANGUAGE ", StringComparison.OrdinalIgnoreCase)
                || line.Contains(';'))
            {
                return i;
            }
        }

        return startLineIndex;
    }

    private static string? ExtractSqlRoutineParameterList(string header)
    {
        var open = header.IndexOf('(');
        if (open < 0)
            return null;

        var depth = 0;
        for (var i = open; i < header.Length; i++)
        {
            if (header[i] == '(')
                depth++;
            else if (header[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return header[(open + 1)..i];
            }
        }

        return null;
    }

    private static IEnumerable<(string Name, string? Type)> EnumerateSqlColumnDefinitions(string columns)
    {
        foreach (var part in SplitSqlTopLevelComma(columns))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;

            var nameEnd = ScanSqlIdentifierEnd(trimmed, 0);
            if (nameEnd <= 0)
                continue;

            var rawName = trimmed[..nameEnd];
            var name = NormalizeSqlSymbolSegment(rawName);
            if (name.Length == 0)
                continue;

            var type = trimmed[nameEnd..].Trim();
            yield return (name, type.Length == 0 ? null : type);
        }
    }

    private static IEnumerable<string> EnumerateSqlReturnsTableColumnLists(string header)
    {
        foreach (Match marker in Regex.EnumerateMatches(SqlReturnsTableMarkerRegex, header))
        {
            var openParen = marker.Index + marker.Length - 1;
            if (TryFindSqlClosingParen(header, openParen, out var closeParen))
                yield return header[(openParen + 1)..closeParen];
        }
    }

    private static bool TryFindSqlClosingParen(string value, int openParen, out int closeParen)
    {
        closeParen = -1;
        var depth = 0;
        char quote = '\0';
        for (var i = openParen; i < value.Length; i++)
        {
            var current = value[i];
            if (quote != '\0')
            {
                var quoteEnd = quote == '[' ? ']' : quote;
                if (current != quoteEnd)
                    continue;

                if (i + 1 < value.Length && value[i + 1] == quoteEnd)
                {
                    i++;
                    continue;
                }

                quote = '\0';
                continue;
            }

            if (current is '\'' or '"' or '`' or '[')
            {
                quote = current;
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && --depth == 0)
            {
                closeParen = i;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SplitSqlTopLevelComma(string value)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '(')
                depth++;
            else if (value[i] == ')' && depth > 0)
                depth--;
            else if (value[i] == ',' && depth == 0)
            {
                yield return value[start..i];
                start = i + 1;
            }
        }

        yield return value[start..];
    }

    private static int ScanSqlIdentifierEnd(string value, int start)
    {
        if (start >= value.Length)
            return start;

        if (value[start] == '[')
        {
            for (var i = start + 1; i < value.Length; i++)
            {
                if (value[i] == ']' && (i + 1 >= value.Length || value[i + 1] != ']'))
                    return i + 1;
                if (value[i] == ']' && i + 1 < value.Length && value[i + 1] == ']')
                    i++;
            }
        }
        else if (value[start] is '"' or '`')
        {
            var quote = value[start];
            for (var i = start + 1; i < value.Length; i++)
            {
                if (value[i] == quote && (i + 1 >= value.Length || value[i + 1] != quote))
                    return i + 1;
                if (value[i] == quote && i + 1 < value.Length && value[i + 1] == quote)
                    i++;
            }
        }
        else
        {
            var i = start;
            while (i < value.Length
                   && (char.IsLetterOrDigit(value[i]) || value[i] == '_' || value[i] == '$'))
            {
                i++;
            }

            return i;
        }

        return value.Length;
    }

    private static string NormalizeSqlSymbolSegment(string rawName)
    {
        var normalized = SqlSymbolNameNormalizer.Normalize(rawName).Trim();
        if (normalized.Length >= 2
            && ((normalized[0] == '[' && normalized[^1] == ']')
                || (normalized[0] == '`' && normalized[^1] == '`')
                || (normalized[0] == '"' && normalized[^1] == '"')))
        {
            normalized = normalized[1..^1];
        }

        return normalized
            .Replace("]]", "]", StringComparison.Ordinal)
            .Replace("\"\"", "\"", StringComparison.Ordinal)
            .Replace("``", "`", StringComparison.Ordinal);
    }

    private static void AddSqlRoutineFieldSymbol(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState,
        int lineNumber,
        string name,
        string? returnType,
        string? ownerName,
        int? ownerBodyStart,
        int? ownerBodyEnd)
    {
        AddSymbolRecord(
            symbols,
            extractionState,
            null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "field",
                Name = name,
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = ownerBodyEnd ?? lineNumber,
                BodyStartLine = ownerBodyStart,
                BodyEndLine = ownerBodyEnd,
                Signature = lines[lineNumber - 1].Trim(),
                ContainerKind = ownerName == null ? null : "function",
                ContainerName = ownerName,
                ReturnType = NormalizeMetadata(returnType),
            },
            lines[lineNumber - 1]);
    }

    private static string? FirstSuccessfulGroupValue(Match match, params string[] names)
    {
        foreach (var name in names)
        {
            var group = match.Groups[name];
            if (group.Success)
                return group.Value;
        }

        return null;
    }

}
