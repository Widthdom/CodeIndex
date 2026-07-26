using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class RustReferenceExtractor
{
    private const string RustIdentifierPattern = @"(?:r#)?[_\p{L}][\w$]*";
    private static readonly string[] ConstStaticKeywords = ["const", "static"];
    private static readonly Regex DeriveAttributeRegex = new(
        @"#\s*!?\s*\[\s*derive\s*\((?<types>[^\)]*)\)",
        RegexOptions.Compiled);
    private static readonly Regex CfgAttrDeriveAttributeRegex = new(
        @"#\s*!?\s*\[\s*cfg_attr\s*\(.*?\bderive\s*\((?<types>[^\)]*)\)",
        RegexOptions.Compiled);
    private static readonly Regex AttributeHeadRegex = new(
        $@"#\s*!?\s*\[\s*(?<name>{RustIdentifierPattern}(?:::{RustIdentifierPattern})*)",
        RegexOptions.Compiled);
    private static readonly Regex ExternCrateRegex = new(
        $@"^\s*(?:pub\s+)?extern\s+crate\s+(?<name>{RustIdentifierPattern})(?:\s+as\s+{RustIdentifierPattern})?\s*;",
        RegexOptions.Compiled);
    private static readonly Regex ModuleDeclarationRegex = new(
        $@"^\s*(?:pub(?:\s*\([^\)]*\))?\s+)?mod\s+(?<name>{RustIdentifierPattern})\s*;",
        RegexOptions.Compiled);
    private static readonly Regex ConstGenericParameterRegex = new(
        $@"^\s*const\s+(?<name>{RustIdentifierPattern})\s*:\s*(?<type>.+?)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex ConstGenericTypeHeadRegex = new(
        $@"^\s*(?<name>{RustIdentifierPattern}(?:::{RustIdentifierPattern})*)",
        RegexOptions.Compiled);
    private static readonly Regex UseStatementRegex = new(
        @"^\s*(?:pub(?:\s*\([^\)]*\))?\s+)?use\s+(?<body>.+);",
        RegexOptions.Compiled);
    private static readonly Regex AssociatedCallReceiverRegex = new(
        $@"(?<![\w$])(?<receiver>{RustIdentifierPattern}(?:::{RustIdentifierPattern})*)(?:::\s*<(?<args>[^>\n]+)>)?::\s*{RustIdentifierPattern}(?:<[^>\n]+>)?\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex AssociatedValueReceiverRegex = new(
        $@"(?<![\w$])(?<receiver>{RustIdentifierPattern}(?:::{RustIdentifierPattern})*)(?:::\s*<(?<args>[^>\n]+)>)?::\s*{RustIdentifierPattern}(?!\s*::\s*<)(?!\s*[\(<])",
        RegexOptions.Compiled);
    private static readonly Regex StructLiteralRegex = new(
        $@"(?<![\w$])(?<name>{RustIdentifierPattern}(?:::{RustIdentifierPattern})*)(?:::\s*<(?<args>[^>\n]+)>)?\s*\{{",
        RegexOptions.Compiled);
    private static readonly Regex MutableReferenceTypeRegex = new(
        @"&\s*mut\b",
        RegexOptions.Compiled);

    // Rust macro calls use `!` plus one of `()`, `[]`, or `{}` instead of the shared trailing `(`.
    // Capture path-qualified macro names so `std::println!`, `log::info!`, and `my_macro!`
    // surface as references. `macro_rules` declarations are filtered by the Rust ignore list.
    // Rust の macro 呼び出しは共通の末尾 `(` ではなく `!` の後に `()` / `[]` / `{}` を取る。
    private static readonly Regex MacroCallRegex = new(
        $@"(?<![\w$])(?<name>{RustIdentifierPattern}(?:::{RustIdentifierPattern})*)(?:<[^>\n]+>)?!\s*[\(\[\{{]",
        RegexOptions.Compiled);

    // Rust raw identifiers such as `r#type()` are stored without the `r#` prefix, but the shared
    // call regex cannot see them because `#` is not an identifier character.
    // Rust の raw identifier (`r#type()`) は保存時に `r#` を外す。
    private static readonly Regex RawIdentifierCallRegex = new(
        @"(?<![\w$])(?<name>(?:(?:r#)?\w+::)*r#\w+(?:::(?:r#)?\w+)*)(?:<[^>\n]+>)?\s*\(",
        RegexOptions.Compiled);

    public static void EmitMultilineAttributeReferences(
        string[] preparedLines,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, int, SymbolRecord?> resolveContainer)
    {
        for (var lineIndex = 0; lineIndex < preparedLines.Length; lineIndex++)
        {
            var line = preparedLines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                if (line[column] != '#')
                    continue;

                var openBracket = FindRustAttributeOpenBracket(line, column);
                if (openBracket < 0)
                    continue;

                var (attribute, endLineIndex, endColumn) = ReadRustAttribute(preparedLines, lineIndex, openBracket);
                EmitDeriveReferencesFromAttribute(
                    attribute,
                    lineIndex,
                    openBracket,
                    references,
                    seen,
                    fileId,
                    resolveContainer);

                if (endLineIndex == lineIndex)
                    column = Math.Max(column, endColumn);
                else
                    break;
            }
        }
    }

    private static int FindRustAttributeOpenBracket(string line, int hashIndex)
    {
        var cursor = hashIndex + 1;
        while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
            cursor++;
        if (cursor < line.Length && line[cursor] == '!')
        {
            cursor++;
            while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
                cursor++;
        }

        return cursor < line.Length && line[cursor] == '[' ? cursor : -1;
    }

    private static (string Attribute, int EndLineIndex, int EndColumn) ReadRustAttribute(
        string[] lines,
        int startLineIndex,
        int openBracket)
    {
        string? firstPart = null;
        StringBuilder? builder = null;
        var depth = 0;
        for (var lineIndex = startLineIndex; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var startColumn = lineIndex == startLineIndex ? openBracket : 0;
            AppendAttributeLine(line[startColumn..]);

            for (var column = startColumn; column < line.Length; column++)
            {
                var c = line[column];
                if (c == 'r')
                {
                    var rawEnd = TrySkipRawString(line, column);
                    if (rawEnd > column)
                    {
                        column = rawEnd;
                        continue;
                    }
                }

                if (c == '"' || c == '\'')
                {
                    column = SkipQuotedString(line, column, c);
                    continue;
                }

                if (c == '[' || c == '(')
                {
                    depth++;
                    continue;
                }

                if (c != ']' && c != ')')
                    continue;

                depth--;
                if (c == ']' && depth == 0)
                    return (BuildAttributeText(), lineIndex, column);
            }
        }

        return (BuildAttributeText(), lines.Length - 1, lines[^1].Length);

        void AppendAttributeLine(string part)
        {
            if (firstPart == null)
            {
                firstPart = part;
                return;
            }

            if (builder == null)
            {
                builder = new StringBuilder(firstPart.Length + 1 + part.Length);
                builder.Append(firstPart);
            }

            builder.Append('\n');
            builder.Append(part);
        }

        string BuildAttributeText() => builder?.ToString() ?? firstPart ?? string.Empty;
    }

    private static void EmitDeriveReferencesFromAttribute(
        string attribute,
        int startLineIndex,
        int startColumn,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, int, SymbolRecord?> resolveContainer)
    {
        var deriveIndex = FindRustAttributeDeriveIndex(attribute);
        if (deriveIndex < 0)
            return;

        var openParen = SkipWhitespace(attribute, deriveIndex + "derive".Length);
        if (openParen >= attribute.Length || attribute[openParen] != '(')
            return;

        var closeParen = FindMatchingDelimiter(attribute, openParen, '(', ')');
        if (closeParen <= openParen)
            return;

        EmitMultilineDeriveTypeList(
            attribute,
            openParen + 1,
            closeParen,
            startLineIndex,
            startColumn,
            references,
            seen,
            fileId,
            resolveContainer);
    }

    private static int FindRustAttributeDeriveIndex(string attribute)
    {
        for (var index = 0; index < attribute.Length; index++)
        {
            if (!IsIdentifierAt(attribute, index, "derive"))
                continue;

            var cursor = SkipWhitespace(attribute, index + "derive".Length);
            if (cursor < attribute.Length && attribute[cursor] == '(')
                return index;
        }

        return -1;
    }

    public static string MaskAttributeBodies(string line)
    {
        var masked = default(char[]);
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] != '#')
                continue;

            var cursor = index + 1;
            while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
                cursor++;
            if (cursor < line.Length && line[cursor] == '!')
            {
                cursor++;
                while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
                    cursor++;
            }

            if (cursor >= line.Length || line[cursor] != '[')
                continue;

            masked ??= line.ToCharArray();
            var end = FindAttributeEnd(line, cursor);
            ReplaceWithSpaces(masked, index, end > cursor ? end - index + 1 : line.Length - index);
            index = end > cursor ? end : line.Length;
        }

        return masked == null ? line : new string(masked);
    }

    private static int FindAttributeEnd(string line, int openBracket)
    {
        var depth = 0;
        for (var index = openBracket; index < line.Length; index++)
        {
            var c = line[index];
            if (c == 'r')
            {
                var rawEnd = TrySkipRawString(line, index);
                if (rawEnd > index)
                {
                    index = rawEnd;
                    continue;
                }
            }

            if (c == '"' || c == '\'')
            {
                index = SkipQuotedString(line, index, c);
                continue;
            }

            if (c == '[')
            {
                depth++;
                continue;
            }

            if (c != ']')
                continue;

            depth--;
            if (depth == 0)
                return index;
        }

        return -1;
    }

    private static int TrySkipRawString(string line, int start)
    {
        var cursor = start + 1;
        while (cursor < line.Length && line[cursor] == '#')
            cursor++;
        if (cursor >= line.Length || line[cursor] != '"')
            return -1;

        var hashCount = cursor - start - 1;
        for (var index = cursor + 1; index < line.Length; index++)
        {
            if (line[index] == '"' && HasHashRun(line, index + 1, hashCount))
                return index + hashCount;
        }

        return line.Length - 1;
    }

    private static bool HasHashRun(string line, int start, int hashCount)
    {
        if (start + hashCount > line.Length)
            return false;
        for (var offset = 0; offset < hashCount; offset++)
        {
            if (line[start + offset] != '#')
                return false;
        }

        return true;
    }

    private static int SkipQuotedString(string line, int start, char quote)
    {
        for (var index = start + 1; index < line.Length; index++)
        {
            if (line[index] == '\\')
            {
                index++;
                continue;
            }

            if (line[index] == quote)
                return index;
        }

        return line.Length - 1;
    }

    private static void ReplaceWithSpaces(char[] chars, int start, int length)
    {
        var end = Math.Min(chars.Length, start + length);
        for (var index = start; index < end; index++)
            chars[index] = ' ';
    }

    public static void EmitAdditionalCallReferences(string preparedLine, Action<string, int> addCallLikeReference)
    {
        if (preparedLine.IndexOf("r#", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(
                         RawIdentifierCallRegex,
                         preparedLine))
            {
                var name = match.Groups["name"].Value;
                var callIndex = match.Groups["name"].Index;
                addCallLikeReference(name, callIndex);
            }
        }

        if (preparedLine.IndexOf('!') < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(MacroCallRegex, preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }
    }

    public static void EmitAttributeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf('#') < 0)
        {
            return;
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     DeriveAttributeRegex,
                     preparedLine,
                     references))
        {
            EmitDeriveTypeList(match.Groups["types"], references, seen, fileId, context, lineNumber, container);
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     CfgAttrDeriveAttributeRegex,
                     preparedLine,
                     references))
        {
            EmitDeriveTypeList(match.Groups["types"], references, seen, fileId, context, lineNumber, container);
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     AttributeHeadRegex,
                     preparedLine,
                     references))
        {
            var nameGroup = match.Groups["name"];
            var name = NormalizeIdentifier(nameGroup.Value);
            if (string.Equals(name, "derive", StringComparison.Ordinal))
                continue;

            ReferenceExtractor.AddReference(references, seen, fileId, name, nameGroup.Index, "annotation", context, lineNumber, container);
        }
    }

    private static void EmitDeriveTypeList(
        Group typesGroup,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDeriveTypeList(
            typesGroup.Value,
            typesGroup.Index,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    private static void EmitDeriveTypeList(
        string types,
        int typesStartIndex,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(types))
        {
            var fragment = types.Substring(segmentStart, segmentLength);
            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, 0);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                typesStartIndex + segmentStart + typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container);
        }
    }

    private static void EmitMultilineDeriveTypeList(
        string attribute,
        int typesStart,
        int typesEnd,
        int attributeStartLineIndex,
        int attributeStartColumn,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, int, SymbolRecord?> resolveContainer)
    {
        var types = attribute.AsSpan(typesStart, typesEnd - typesStart);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(types))
        {
            var fragment = types.Slice(segmentStart, segmentLength).ToString();
            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, 0);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart);
            if (typeEnd <= typeStart)
                continue;

            var absoluteTypeStart = typesStart + segmentStart + typeStart;
            var (lineNumber, column) = GetLineColumn(attribute, attributeStartLineIndex, attributeStartColumn, absoluteTypeStart);
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                column,
                "rust",
                references,
                seen,
                fileId,
                attribute.Trim(),
                lineNumber,
                resolveContainer(lineNumber, column));
        }
    }

    private static int FindMatchingDelimiter(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var index = openIndex; index < text.Length; index++)
        {
            var c = text[index];
            if (c == 'r')
            {
                var rawEnd = TrySkipRawString(text, index);
                if (rawEnd > index)
                {
                    index = rawEnd;
                    continue;
                }
            }

            if (c == '"' || c == '\'')
            {
                index = SkipQuotedString(text, index, c);
                continue;
            }

            if (c == open)
            {
                depth++;
                continue;
            }

            if (c != close)
                continue;

            depth--;
            if (depth == 0)
                return index;
        }

        return -1;
    }

    private static bool IsIdentifierAt(string text, int index, string identifier)
    {
        if (index > 0 && IsRustIdentifierPart(text[index - 1]))
            return false;
        if (index + identifier.Length > text.Length)
            return false;
        if (!text.AsSpan(index, identifier.Length).SequenceEqual(identifier))
            return false;
        return index + identifier.Length >= text.Length || !IsRustIdentifierPart(text[index + identifier.Length]);
    }

    private static (int LineNumber, int Column) GetLineColumn(
        string text,
        int startLineIndex,
        int startColumn,
        int offset)
    {
        var lineNumber = startLineIndex + 1;
        var column = startColumn;
        for (var index = 0; index < offset && index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                lineNumber++;
                column = 0;
                continue;
            }

            column++;
        }

        return (lineNumber, column);
    }

}
