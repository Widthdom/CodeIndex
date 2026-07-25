using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static void AddTypeReferenceSegments(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string arg,
        int argStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language)
    {
        int offset = 0;
        var segmentStart = 0;
        while (segmentStart <= arg.Length)
        {
            var dotIndex = arg.IndexOf('.', segmentStart);
            var segmentLength = dotIndex < 0 ? arg.Length - segmentStart : dotIndex - segmentStart;
            if (segmentLength == 0)
            {
                offset += 1; // '.' separator / ドット区切り分
                if (dotIndex < 0)
                    break;
                segmentStart = dotIndex + 1;
                continue;
            }

            var segment = arg.Substring(segmentStart, segmentLength);
            var normalizedSegment = language == "csharp" ? NormalizeCSharpIdentifier(segment) : segment;
            var isEscapedCSharpIdentifier = language == "csharp" && segment[0] == '@';
            if (!IsIgnoredTypeReferenceSegment(language, normalizedSegment, isEscapedCSharpIdentifier))
            {
                int column = argStartInLine + offset + 1; // 1-based / 1始まり
                var dedupeKey = CreateReferenceDedupeKey(fileId, language, lineNumber, column, "type_reference", normalizedSegment, container);
                if (seen.Add(dedupeKey))
                {
                    if (!TryAddReference(
                            references,
                            new ReferenceRecord
                            {
                                FileId = fileId,
                                SymbolName = normalizedSegment,
                                ReferenceKind = "type_reference",
                                Line = lineNumber,
                                Column = column,
                                Context = context,
                                ContainerKind = container?.Kind,
                                ContainerName = container?.Name,
                            }))
                    {
                        return;
                    }
                }
            }

            offset += segment.Length + 1; // segment + '.'
            if (dotIndex < 0)
                break;
            segmentStart = dotIndex + 1;
        }
    }

    private static bool IsIgnoredTypeReferenceSegment(string language, string segment, bool isEscapedCSharpIdentifier = false, IReadOnlySet<string>? ignoredSegments = null)
    {
        if (isEscapedCSharpIdentifier)
            return false;
        if (ignoredSegments != null && ignoredSegments.Contains(segment))
            return true;
        if (IsIgnoredCallName(language, segment))
            return true;
        if (language == "java" && JavaPrimitiveTypeNames.Contains(segment))
            return true;
        if (language == "csharp" && CSharpBuiltInTypeNames.Contains(segment))
            return true;
        if (LanguageBuiltInTypeNames.TryGetValue(language, out var builtInTypes)
            && builtInTypes.Contains(segment))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Walk the argument list of a C# nameof/typeof/sizeof/default starting at
    /// <paramref name="startIndex"/> (the char right after `(`). Emits one `type_reference` row
    /// per identifier segment while handling generic `&lt;...&gt;`, array `[...]`,
    /// parenthesized/tuple groups `(...)`, and `global::` / `Alias::` qualifier skipping so nested
    /// paths like `nameof(List&lt;int&gt;.Count)`, `nameof(global::System.String)`,
    /// and `typeof((Foo, Bar))` are indexed correctly.
    /// C# の nameof/typeof/sizeof/default の引数を `(` 直後から lexer で走査し、
    /// generic `&lt;...&gt;`・配列 `[...]`・タプル `(...)` 群・`global::` / `Alias::` 修飾子を
    /// 跨ぎながら識別子セグメントごとに type_reference を発行する。
    /// </summary>
    private static void ExtractCSharpTypeKeywordSegments(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string line,
        int startIndex,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        int i = startIndex;
        int parenDepth = 0;
        int angleDepth = 0;
        bool expectSegment = true;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == ')')
            {
                if (parenDepth == 0)
                    return;
                parenDepth--;
                i++;
                expectSegment = false;
                continue;
            }

            if (c == ',')
            {
                if (parenDepth == 0 && angleDepth == 0)
                    return;
                // Tuple or generic argument separator inside `typeof((Foo, Bar))` /
                // `typeof(List<Foo, Bar>)` — keep scanning.
                // `typeof((Foo, Bar))` のタプル要素区切りや `typeof(List<Foo, Bar>)`
                // の generic 引数区切りは続けて走査する。
                i++;
                expectSegment = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (expectSegment && IsCSharpIdentifierStart(c))
            {
                int segStart = i;
                if (line[i] == '@')
                    i++;
                while (i < line.Length && IsCSharpIdentifierPart(line[i]))
                    i++;
                var rawSegment = line.Substring(segStart, i - segStart);
                var segment = NormalizeCSharpIdentifier(rawSegment);
                var isEscapedCSharpIdentifier = rawSegment.Length > 0 && rawSegment[0] == '@';
                // `Alias::Member` — the left-hand side is a namespace alias, not an indexed
                // type. Drop it instead of emitting it, and treat what follows the `::` as a
                // fresh segment head.
                // `Alias::Member` の左辺はエイリアスであり型シンボルではないため発行せず、
                // `::` の右側を新しいセグメント先頭として読み直す。
                if (i + 1 < line.Length && line[i] == ':' && line[i + 1] == ':')
                {
                    i += 2;
                    expectSegment = true;
                    continue;
                }

                if (ignoredSegments?.Contains(segment) == true)
                {
                    expectSegment = false;
                    continue;
                }

                AddTypeReferenceSegment(references, seen, fileId, segment, segStart, context, lineNumber, container, language, isEscapedCSharpIdentifier);
                expectSegment = false;
                continue;
            }

            if (c == '.')
            {
                i++;
                expectSegment = true;
                continue;
            }

            if (c == '<')
            {
                angleDepth++;
                i++;
                expectSegment = true;
                continue;
            }

            if (c == '>')
            {
                if (angleDepth == 0)
                    return;
                angleDepth--;
                i++;
                expectSegment = false;
                continue;
            }

            if (c == '[')
            {
                i = SkipBalanced(line, i, '[', ']');
                continue;
            }

            if (c == '(')
            {
                // Track paren depth instead of skipping the body so tuple/parenthesized
                // type groups like `typeof((Foo, Bar))` still yield inner segments.
                // タプル型 `typeof((Foo, Bar))` の中身も拾えるよう、括弧はスキップせず
                // 深さだけ追跡する。
                parenDepth++;
                i++;
                expectSegment = true;
                continue;
            }

            // Unknown token (operator, string start, etc.) — stop scanning this argument.
            // 解釈できないトークンが来たら、このキーワード引数の走査を打ち切る。
            return;
        }
    }

    private static void ExtractCSharpReflectionNameLiteralReferences(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string preparedLine,
        string originalLine,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("Get", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0
            || !CSharpReflectionNameApiIntroRegex.IsMatch(preparedLine))
        {
            return;
        }

        var codeLine = SanitizeCSharpCommentsForReflectionNameScan(originalLine);
        foreach (Match match in CSharpReflectionNameApiIntroRegex.Matches(codeLine))
        {
            if (IsInsideCSharpStringLiteral(codeLine, match.Index))
                continue;
            if (!preparedLine.Contains(match.Groups["name"].Value, StringComparison.Ordinal))
                continue;

            var argStart = match.Index + match.Length;
            if (!TryReadCSharpReflectionNameLiteral(originalLine, argStart, out var symbolName, out var nameIndex))
                continue;
            if (!IsValidCSharpReflectionSymbolName(symbolName))
                continue;

            AddReference(references, seen, fileId, symbolName, nameIndex, "type_reference", context, lineNumber, container, "csharp");
        }
    }

    private static bool TryReadCSharpReflectionNameLiteral(string line, int startIndex, out string symbolName, out int nameIndex)
    {
        symbolName = string.Empty;
        nameIndex = -1;
        var builder = new StringBuilder(Math.Min(256, Math.Max(0, line.Length - startIndex)));
        var i = startIndex;
        var sawLiteral = false;
        var firstLiteralIndex = -1;

        while (i < line.Length)
        {
            SkipWhitespace(line, ref i);
            if (!TryReadCSharpStringLiteral(line, ref i, out var value, out var literalContentIndex))
                return false;

            if (!sawLiteral)
                firstLiteralIndex = literalContentIndex;
            sawLiteral = true;
            builder.Append(value);

            SkipWhitespace(line, ref i);
            if (i >= line.Length)
                return false;
            if (line[i] == ',' || line[i] == ')')
            {
                symbolName = builder.ToString();
                nameIndex = firstLiteralIndex;
                return sawLiteral && symbolName.Length > 0;
            }
            if (line[i] != '+')
                return false;

            i++;
        }

        return false;
    }

    private static string SanitizeCSharpCommentsForReflectionNameScan(string line)
    {
        char[]? chars = null;
        var inRegularString = false;
        var inVerbatimString = false;
        var inChar = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inRegularString)
            {
                if (c == '\\' && i + 1 < line.Length)
                    i++;
                else if (c == '"')
                    inRegularString = false;
                continue;
            }
            if (inVerbatimString)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    i++;
                else if (c == '"')
                    inVerbatimString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\' && i + 1 < line.Length)
                    i++;
                else if (c == '\'')
                    inChar = false;
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                return line[..i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                chars ??= line.ToCharArray();
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i += 2;
                while (i < line.Length)
                {
                    chars[i] = ' ';
                    if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        chars[i + 1] = ' ';
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                inVerbatimString = true;
                i++;
                continue;
            }
            if (c == '$' && i + 1 < line.Length && line[i + 1] == '"')
            {
                inRegularString = true;
                i++;
                continue;
            }
            if (c == '"')
                inRegularString = true;
            else if (c == '\'')
                inChar = true;
        }

        return chars == null ? line : new string(chars);
    }

    private static bool IsInsideCSharpStringLiteral(string line, int targetIndex)
    {
        var inRegularString = false;
        var inVerbatimString = false;
        for (var i = 0; i < line.Length && i < targetIndex; i++)
        {
            var c = line[i];
            if (inRegularString)
            {
                if (c == '\\' && i + 1 < line.Length)
                    i++;
                else if (c == '"')
                    inRegularString = false;
                continue;
            }
            if (inVerbatimString)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    i++;
                else if (c == '"')
                    inVerbatimString = false;
                continue;
            }

            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                inVerbatimString = true;
                i++;
            }
            else if (c == '$' && i + 1 < line.Length && line[i + 1] == '"')
            {
                inRegularString = true;
                i++;
            }
            else if (c == '"')
            {
                inRegularString = true;
            }
        }

        return inRegularString || inVerbatimString;
    }

    private static bool TryReadCSharpStringLiteral(string line, ref int index, out string value, out int contentIndex)
    {
        value = string.Empty;
        contentIndex = -1;
        var verbatim = false;
        if (index + 1 < line.Length && line[index] == '@' && line[index + 1] == '"')
        {
            verbatim = true;
            index++;
        }
        else if (index < line.Length && line[index] == '$')
        {
            return false;
        }

        if (index >= line.Length || line[index] != '"')
            return false;

        contentIndex = index + 1;
        index++;
        var builder = new StringBuilder(Math.Min(256, line.Length - contentIndex));
        while (index < line.Length)
        {
            var c = line[index];
            if (c == '"')
            {
                if (verbatim && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index += 2;
                    continue;
                }

                index++;
                value = builder.ToString();
                return true;
            }

            if (!verbatim && c == '\\' && index + 1 < line.Length)
            {
                builder.Append(line[index + 1]);
                index += 2;
                continue;
            }

            builder.Append(c);
            index++;
        }

        return false;
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static bool IsValidCSharpReflectionSymbolName(string symbolName)
    {
        if (symbolName.Length == 0 || !IsCSharpIdentifierStart(symbolName[0]))
            return false;
        for (var i = 1; i < symbolName.Length; i++)
        {
            if (!IsCSharpIdentifierPart(symbolName[i]))
                return false;
        }
        return true;
    }

}
