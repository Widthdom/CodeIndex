using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class LuaReferenceExtractor
{
    private static readonly Regex LuaCommandCallRegex = new(
        @"^\s*(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)?)\s+(?=[""'{A-Za-z_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaColonCallRegex = new(
        @"(?<![\w.])(?:[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*):(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaTableFieldReferenceRegex = new(
        @"(?<![\w.])(?:[A-Za-z_]\w*\.)+(?<name>[A-Za-z_]\w*)\b(?!\s*(?:=|function\b|\())",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string[] MaskLongCommentAndStringLines(IReadOnlyList<string> originalLines)
    {
        if (!MayContainLongBracket(originalLines))
            return originalLines as string[] ?? originalLines.ToArray();

        var result = new string[originalLines.Count];
        var longTextEqualsCount = -1;

        for (var lineIndex = 0; lineIndex < originalLines.Count; lineIndex++)
        {
            var line = originalLines[lineIndex];
            var chars = line.ToCharArray();
            for (var cursor = 0; cursor < chars.Length; cursor++)
            {
                if (longTextEqualsCount >= 0)
                {
                    if (TryGetLuaLongBracketClose(line, cursor, longTextEqualsCount, out var closeLength))
                    {
                        MaskRange(chars, cursor, cursor + closeLength);
                        cursor += closeLength - 1;
                        longTextEqualsCount = -1;
                        continue;
                    }

                    chars[cursor] = ' ';
                    continue;
                }

                if (chars[cursor] is '"' or '\'')
                {
                    cursor = SkipQuotedLiteral(line, cursor);
                    continue;
                }

                if (chars[cursor] == '-'
                    && cursor + 2 < chars.Length
                    && chars[cursor + 1] == '-'
                    && TryGetLuaLongBracketOpen(line, cursor + 2, out var commentEqualsCount, out var commentOpenLength))
                {
                    MaskRange(chars, cursor, cursor + 2 + commentOpenLength);
                    cursor += 1 + commentOpenLength;
                    longTextEqualsCount = commentEqualsCount;
                    continue;
                }

                if (chars[cursor] == '-' && cursor + 1 < chars.Length && chars[cursor + 1] == '-')
                    break;

                if (TryGetLuaLongBracketOpen(line, cursor, out var stringEqualsCount, out var stringOpenLength))
                {
                    MaskRange(chars, cursor, cursor + stringOpenLength);
                    cursor += stringOpenLength - 1;
                    longTextEqualsCount = stringEqualsCount;
                }
            }

            result[lineIndex] = new string(chars);
        }

        return result;
    }

    private static bool MayContainLongBracket(IReadOnlyList<string> originalLines)
    {
        foreach (var line in originalLines)
        {
            if (line.Contains('[', StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static void EmitTypePositionReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (var (name, index) in EnumerateLuaRequireReferences(originalLine))
            ReferenceExtractor.AddReference(references, seen, fileId, name, index, "type_reference", context, lineNumber, container);
    }

    public static void EmitAdditionalCallReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? definitionNames)
    {
        if (preparedLine.IndexOf(' ') >= 0 || preparedLine.IndexOf('\t') >= 0)
        {
            var match = LuaCommandCallRegex.Match(preparedLine);
            if (match.Success)
            {
                var name = LastQualifiedSegment(match.Groups["name"].Value);
                if (definitionNames?.Contains(name) != true)
                    addCallLikeReference(name, match.Groups["name"].Index + match.Groups["name"].Value.LastIndexOf(name, StringComparison.Ordinal));
            }
        }

        if (preparedLine.IndexOf(':') >= 0)
        {
            foreach (Match colonMatch in LuaColonCallRegex.Matches(preparedLine))
            {
                var name = colonMatch.Groups["name"].Value;
                if (definitionNames?.Contains(name) == true)
                    continue;
                addCallLikeReference(name, colonMatch.Groups["name"].Index);
            }
        }

        if (preparedLine.IndexOf('.') < 0)
            return;

        foreach (Match fieldMatch in LuaTableFieldReferenceRegex.Matches(preparedLine))
        {
            var trimmed = preparedLine.TrimStart();
            if (trimmed.StartsWith("function ", StringComparison.Ordinal)
                || trimmed.StartsWith("local function ", StringComparison.Ordinal))
            {
                break;
            }

            var name = fieldMatch.Groups["name"].Value;
            if (definitionNames?.Contains(name) == true)
                continue;
            var index = fieldMatch.Groups["name"].Index;
            ReferenceExtractor.AddReference(references, seen, fileId, name, index, "reference", context, lineNumber, resolveContainerForColumn(index));
        }
    }

    private static bool TryGetLuaLongBracketOpen(string line, int start, out int equalsCount, out int length)
    {
        equalsCount = 0;
        length = 0;
        if (start < 0 || start >= line.Length || line[start] != '[')
            return false;

        var cursor = start + 1;
        while (cursor < line.Length && line[cursor] == '=')
        {
            equalsCount++;
            cursor++;
        }

        if (cursor >= line.Length || line[cursor] != '[')
            return false;

        length = cursor - start + 1;
        return true;
    }

    private static bool TryGetLuaLongBracketClose(string line, int start, int equalsCount, out int length)
    {
        length = 0;
        if (start < 0 || start >= line.Length || line[start] != ']')
            return false;

        var cursor = start + 1;
        for (var i = 0; i < equalsCount; i++)
        {
            if (cursor >= line.Length || line[cursor] != '=')
                return false;
            cursor++;
        }

        if (cursor >= line.Length || line[cursor] != ']')
            return false;

        length = cursor - start + 1;
        return true;
    }

    private static IEnumerable<(string Name, int Index)> EnumerateLuaRequireReferences(string line)
    {
        for (var cursor = 0; cursor < line.Length; cursor++)
        {
            if (line[cursor] == '-' && cursor + 1 < line.Length && line[cursor + 1] == '-')
                yield break;

            if (line[cursor] is '"' or '\'')
            {
                cursor = SkipQuotedLiteral(line, cursor);
                continue;
            }

            if (line[cursor] == '[' && cursor + 1 < line.Length && line[cursor + 1] == '[')
            {
                var close = line.IndexOf("]]", cursor + 2, StringComparison.Ordinal);
                cursor = close < 0 ? line.Length : close + 1;
                continue;
            }

            if (!IsLuaIdentifierAt(line, cursor, "require"))
                continue;

            var argStart = cursor + "require".Length;
            while (argStart < line.Length && char.IsWhiteSpace(line[argStart]))
                argStart++;
            if (argStart < line.Length && line[argStart] == '(')
            {
                argStart++;
                while (argStart < line.Length && char.IsWhiteSpace(line[argStart]))
                    argStart++;
            }

            if (argStart >= line.Length || line[argStart] is not ('"' or '\''))
                continue;

            var quote = line[argStart++];
            var nameStart = argStart;
            while (argStart < line.Length)
            {
                if (line[argStart] == '\\' && argStart + 1 < line.Length)
                {
                    argStart += 2;
                    continue;
                }

                if (line[argStart] == quote)
                    break;
                argStart++;
            }

            if (argStart > nameStart)
                yield return (line[nameStart..argStart], nameStart);
            cursor = argStart;
        }
    }

    private static int SkipQuotedLiteral(string line, int start)
    {
        var quote = line[start];
        var cursor = start + 1;
        while (cursor < line.Length)
        {
            if (line[cursor] == '\\' && cursor + 1 < line.Length)
            {
                cursor += 2;
                continue;
            }

            if (line[cursor] == quote)
                return cursor;
            cursor++;
        }

        return line.Length;
    }

    private static bool IsLuaIdentifierAt(string line, int index, string identifier)
    {
        if (index < 0 || index + identifier.Length > line.Length)
            return false;
        if (string.CompareOrdinal(line, index, identifier, 0, identifier.Length) != 0)
            return false;
        if (index > 0 && IsLuaIdentifierPart(line[index - 1]))
            return false;

        var after = index + identifier.Length;
        return after >= line.Length || !IsLuaIdentifierPart(line[after]);
    }

    private static bool IsLuaIdentifierPart(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);

    private static string LastQualifiedSegment(string value)
    {
        var dot = value.LastIndexOf('.');
        return dot >= 0 && dot + 1 < value.Length ? value[(dot + 1)..] : value;
    }

    private static void MaskRange(char[] chars, int start, int end)
    {
        for (var i = Math.Max(0, start); i < end && i < chars.Length; i++)
            chars[i] = ' ';
    }
}
