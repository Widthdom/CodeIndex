using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class RustReferenceExtractor
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
        HashSet<string> seen,
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
        var parts = new List<string>();
        var depth = 0;
        for (var lineIndex = startLineIndex; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var startColumn = lineIndex == startLineIndex ? openBracket : 0;
            parts.Add(line[startColumn..]);

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
                    return (string.Join('\n', parts), lineIndex, column);
            }
        }

        return (string.Join('\n', parts), lines.Length - 1, lines[^1].Length);
    }

    private static void EmitDeriveReferencesFromAttribute(
        string attribute,
        int startLineIndex,
        int startColumn,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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
        foreach (Match match in RawIdentifierCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        foreach (Match match in MacroCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }
    }

    public static void EmitAttributeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in DeriveAttributeRegex.Matches(preparedLine))
        {
            EmitDeriveTypeList(match.Groups["types"], references, seen, fileId, context, lineNumber, container);
        }

        foreach (Match match in CfgAttrDeriveAttributeRegex.Matches(preparedLine))
        {
            EmitDeriveTypeList(match.Groups["types"], references, seen, fileId, context, lineNumber, container);
        }

        foreach (Match match in AttributeHeadRegex.Matches(preparedLine))
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
        long fileId,
        Func<int, int, SymbolRecord?> resolveContainer)
    {
        var types = attribute.Substring(typesStart, typesEnd - typesStart);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(types))
        {
            var fragment = types.Substring(segmentStart, segmentLength);
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

    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container,
        SymbolRecord? enumContainer)
    {
        EmitLifetimeReferences(context, references, seen, fileId, context, lineNumber, container);
        EmitHigherRankedTraitBoundReferences(context, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitUseReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
        EmitExternCrateReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
        EmitModuleDeclarationReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
        EmitFunctionSignatureTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitClosureSignatureTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitLetTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitConstStaticTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitTypeAliasTargetReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitTraitAliasTargetReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitAssociatedTypeBoundReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitTupleStructFieldTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, container);
        EmitStructFieldTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
        EmitEnumVariantTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, enumContainer);
        EmitAsCastTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitQualifiedAssociatedCallReceiverTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitAssociatedCallReceiverTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitAssociatedValueReceiverTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitStructLiteralInstantiationReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, enumContainer);
        EmitImplAndTraitTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitMutableReferenceTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGenericBoundReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitMutableReferenceTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in MutableReferenceTypeRegex.Matches(preparedLine))
        {
            if (!IsMutableReferenceTypeContext(preparedLine, match.Index))
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, match.Index + match.Length);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static bool IsMutableReferenceTypeContext(string preparedLine, int ampersandIndex)
    {
        var cursor = ampersandIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(preparedLine[cursor]))
            cursor--;

        if (cursor < 0)
            return false;
        if (preparedLine[cursor] == ':')
            return true;

        return preparedLine[cursor] == '>'
            && cursor > 0
            && preparedLine[cursor - 1] == '-';
    }

    private static void EmitLifetimeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        for (var index = 0; index + 1 < preparedLine.Length; index++)
        {
            if (preparedLine[index] != '\'' || !IsRustLifetimeStart(preparedLine[index + 1]))
                continue;

            var end = index + 2;
            while (end < preparedLine.Length && IsRustLifetimePart(preparedLine[end]))
                end++;

            if (end < preparedLine.Length && preparedLine[end] == '\'')
            {
                index = end;
                continue;
            }

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                preparedLine.Substring(index, end - index),
                index,
                "lifetime_reference",
                context,
                lineNumber,
                container);
            index = end - 1;
        }
    }

    private static void EmitHigherRankedTraitBoundReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var forIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(line, "for"))
        {
            var openAngle = SkipWhitespace(line, forIndex + "for".Length);
            if (openAngle >= line.Length || line[openAngle] != '<')
                continue;

            var closeAngle = FindRustGenericClose(line, openAngle);
            if (closeAngle <= openAngle)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(line, closeAngle + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(line, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                line.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitUseReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = UseStatementRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var bodyGroup = match.Groups["body"];
        EmitUseBodyReferences(bodyGroup.Value, bodyGroup.Index, references, seen, fileId, context, lineNumber, container, prefix: null);
    }

    private static void EmitUseBodyReferences(
        string body,
        int bodyStart,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string? prefix)
    {
        var text = body.Trim();
        if (text.Length == 0)
            return;

        var textStart = bodyStart + body.IndexOf(text, StringComparison.Ordinal);
        var openBrace = text.IndexOf('{');
        if (openBrace >= 0)
        {
            var closeBrace = text.LastIndexOf('}');
            if (closeBrace > openBrace)
            {
                var groupedPrefix = CombineUsePath(prefix, text[..openBrace].Trim());
                var inner = text.Substring(openBrace + 1, closeBrace - openBrace - 1);
                foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(inner))
                {
                    EmitUseBodyReferences(
                        inner.Substring(segmentStart, segmentLength),
                        textStart + openBrace + 1 + segmentStart,
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        container,
                        groupedPrefix);
                }

                return;
            }
        }

        var aliasIndex = FindTopLevelUseAliasIndex(text);
        var target = aliasIndex >= 0 ? text[..aliasIndex].Trim() : text;
        if (target.Length == 0
            || target is "crate" or "super"
            || target == "*" && string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        if (target == "self" && !string.IsNullOrWhiteSpace(prefix))
            target = prefix;
        else if (target == "self")
            return;
        else if (!string.IsNullOrWhiteSpace(prefix))
            target = CombineUsePath(prefix, target);

        var leafStart = target.LastIndexOf("::", StringComparison.Ordinal);
        var leaf = leafStart >= 0 ? target[(leafStart + 2)..].Trim() : target.Trim();
        if (leaf == "*")
        {
            if (leafStart < 0)
                return;

            var globParent = target[..leafStart].Trim();
            var globParentLeafStart = globParent.LastIndexOf("::", StringComparison.Ordinal);
            leaf = globParentLeafStart >= 0 ? globParent[(globParentLeafStart + 2)..].Trim() : globParent;
        }

        if (leaf.Length == 0 || leaf is "crate" or "self" or "super" or "*")
            return;

        var leafIndex = text.IndexOf(leaf, StringComparison.Ordinal);
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            NormalizeIdentifier(leaf),
            textStart + Math.Max(0, leafIndex),
            "reference",
            context,
            lineNumber,
            container);
    }

    private static int FindTopLevelUseAliasIndex(string text)
    {
        foreach (var asIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(text, "as"))
            return asIndex;

        return -1;
    }

    private static string CombineUsePath(string? prefix, string name)
    {
        var cleanedPrefix = prefix?.Trim().TrimEnd(':');
        var cleanedName = name.Trim().TrimEnd(':');
        if (string.IsNullOrWhiteSpace(cleanedPrefix))
            return cleanedName;
        if (string.IsNullOrWhiteSpace(cleanedName))
            return cleanedPrefix;
        return $"{cleanedPrefix}::{cleanedName}";
    }

    private static void EmitExternCrateReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = ExternCrateRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            NormalizeIdentifier(nameGroup.Value),
            nameGroup.Index,
            "reference",
            context,
            lineNumber,
            container);
    }

    private static void EmitModuleDeclarationReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = ModuleDeclarationRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            NormalizeIdentifier(nameGroup.Value),
            nameGroup.Index,
            "reference",
            context,
            lineNumber,
            container);
    }

    private static void EmitFunctionSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var fnIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "fn");
        if (fnIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', fnIndex + 2);
        if (openParen <= fnIndex)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(preparedLine, "->", closeParen + 1);
        if (arrowIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, arrowIndex + 2);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static void EmitClosureSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var searchIndex = 0;
        while (searchIndex < preparedLine.Length)
        {
            var openPipe = preparedLine.IndexOf('|', searchIndex);
            if (openPipe < 0)
                return;

            var closePipe = preparedLine.IndexOf('|', openPipe + 1);
            if (closePipe < 0)
                return;

            searchIndex = closePipe + 1;
            var parameterList = preparedLine.Substring(openPipe + 1, closePipe - openPipe - 1);
            var hasParameterTypes = TypedLanguageReferenceExtractor.FindTopLevelChar(parameterList, ':') >= 0;
            var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(preparedLine, "->", closePipe + 1);
            var hasImmediateReturnType = arrowIndex >= 0 && HasOnlyWhitespace(preparedLine, closePipe + 1, arrowIndex);
            if (!hasParameterTypes && !hasImmediateReturnType)
                continue;

            if (hasParameterTypes)
            {
                TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
                    preparedLine,
                    openPipe + 1,
                    closePipe,
                    "rust",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn);
            }

            if (!hasImmediateReturnType)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, arrowIndex + 2);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static bool HasOnlyWhitespace(string text, int startIndex, int endIndex)
    {
        for (var index = Math.Max(0, startIndex); index < endIndex && index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
                return false;
        }

        return true;
    }

    private static void EmitLetTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var letIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "let"))
        {
            var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', letIndex + "let".Length);
            if (colonIndex < 0)
                continue;

            var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', letIndex + "let".Length);
            if (assignmentIndex >= 0 && assignmentIndex < colonIndex)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitConstStaticTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var keyword in ConstStaticKeywords)
        {
            foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, keyword))
            {
                var declarationStart = keywordIndex + keyword.Length;
                if (keyword == "static")
                    declarationStart = SkipOptionalRustMut(preparedLine, declarationStart);

                var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', declarationStart);
                if (colonIndex < 0)
                    continue;

                var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', declarationStart);
                if (assignmentIndex >= 0 && assignmentIndex < colonIndex)
                    continue;

                var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
                var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
                if (typeEnd <= typeStart)
                    continue;

                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    preparedLine.Substring(typeStart, typeEnd - typeStart),
                    typeStart,
                    "rust",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(typeStart));
            }
        }
    }

    private static int SkipOptionalRustMut(string line, int startIndex)
    {
        var index = startIndex;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        if (index + "mut".Length > line.Length
            || string.CompareOrdinal(line, index, "mut", 0, "mut".Length) != 0)
        {
            return startIndex;
        }

        var afterMut = index + "mut".Length;
        if (afterMut < line.Length && IsRustIdentifierPart(line[afterMut]))
            return startIndex;

        return afterMut;
    }

    private static void EmitTypeAliasTargetReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var typeIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "type"))
        {
            var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', typeIndex + "type".Length);
            if (assignmentIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, assignmentIndex + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitTraitAliasTargetReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var traitIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "trait"))
        {
            var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', traitIndex + "trait".Length);
            if (assignmentIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, assignmentIndex + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitAssociatedTypeBoundReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var typeIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "type"))
        {
            var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', typeIndex + "type".Length);
            if (colonIndex < 0)
                continue;

            var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', typeIndex + "type".Length);
            if (assignmentIndex >= 0 && assignmentIndex < colonIndex)
                continue;

            var boundsStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
            var boundsEnd = assignmentIndex > colonIndex
                ? assignmentIndex
                : TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, boundsStart);
            if (boundsEnd <= boundsStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(boundsStart, boundsEnd - boundsStart),
                boundsStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(boundsStart));
        }
    }

    private static void EmitTupleStructFieldTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        var structIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "struct");
        if (structIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', structIndex + "struct".Length);
        if (openParen < 0)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen <= openParen)
            return;

        var fieldList = preparedLine.Substring(openParen + 1, closeParen - openParen - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(fieldList))
        {
            var fragment = fieldList.Substring(segmentStart, segmentLength);
            var typeStart = SkipRustTupleFieldPrefix(fragment);
            if (typeStart >= fragment.Length)
                continue;

            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = openParen + 1 + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container ?? resolveContainerForColumn(absoluteStart));
        }
    }

    private static void EmitStructFieldTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (container?.Kind is not "class" and not "struct")
            return;

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':');
        if (colonIndex < 0)
            return;

        var trimmed = preparedLine.TrimStart();
        if (trimmed.StartsWith("fn ", StringComparison.Ordinal)
            || trimmed.StartsWith("let ", StringComparison.Ordinal)
            || trimmed.StartsWith("type ", StringComparison.Ordinal)
            || trimmed.StartsWith("impl ", StringComparison.Ordinal))
        {
            return;
        }

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);
    }

    private static int SkipRustTupleFieldPrefix(string fragment)
    {
        var index = 0;
        while (index < fragment.Length && char.IsWhiteSpace(fragment[index]))
            index++;

        if (index + 3 > fragment.Length
            || string.CompareOrdinal(fragment, index, "pub", 0, "pub".Length) != 0)
        {
            return index;
        }

        var afterPub = index + "pub".Length;
        if (afterPub < fragment.Length && IsRustIdentifierPart(fragment[afterPub]))
            return index;

        index = afterPub;
        while (index < fragment.Length && char.IsWhiteSpace(fragment[index]))
            index++;

        if (index < fragment.Length && fragment[index] == '(')
        {
            var closeParen = ReferenceExtractor.FindMatchingChar(fragment, index, '(', ')');
            if (closeParen < 0)
                return fragment.Length;

            index = closeParen + 1;
            while (index < fragment.Length && char.IsWhiteSpace(fragment[index]))
                index++;
        }

        return index;
    }

    private static bool IsRustIdentifierPart(char ch) =>
        ch == '_' || ch == '$' || char.IsLetterOrDigit(ch);

    private static bool IsRustIdentifierStart(char ch) =>
        ch == '_' || char.IsLetter(ch);

    private static bool IsRustLifetimeStart(char ch) =>
        ch == '_' || char.IsLetter(ch);

    private static bool IsRustLifetimePart(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    private static void EmitEnumVariantTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? enumContainer)
    {
        if (enumContainer?.Kind != "enum")
            return;

        var variantStart = FirstNonWhitespaceIndex(preparedLine);
        if (variantStart >= preparedLine.Length
            || preparedLine[variantStart] is '}' or '#'
            || !IsLikelyRustEnumVariantStart(preparedLine, variantStart))
        {
            return;
        }

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', variantStart);
        var openBrace = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '{', variantStart);
        if (openParen >= 0 && (openBrace < 0 || openParen < openBrace))
        {
            EmitEnumTupleVariantTypeReferences(preparedLine, openParen, references, seen, fileId, context, lineNumber, enumContainer);
        }

        if (openBrace >= 0)
            EmitEnumStructVariantTypeReferences(preparedLine, openBrace, references, seen, fileId, context, lineNumber, enumContainer);
    }

    private static void EmitEnumTupleVariantTypeReferences(
        string preparedLine,
        int openParen,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord enumContainer)
    {
        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen <= openParen)
            return;

        var fieldList = preparedLine.Substring(openParen + 1, closeParen - openParen - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(fieldList))
        {
            var fragment = fieldList.Substring(segmentStart, segmentLength);
            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, 0);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = openParen + 1 + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                enumContainer);
        }
    }

    private static void EmitEnumStructVariantTypeReferences(
        string preparedLine,
        int openBrace,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord enumContainer)
    {
        var closeBrace = ReferenceExtractor.FindMatchingChar(preparedLine, openBrace, '{', '}');
        if (closeBrace <= openBrace)
            return;

        var fieldList = preparedLine.Substring(openBrace + 1, closeBrace - openBrace - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(fieldList))
        {
            var fragment = fieldList.Substring(segmentStart, segmentLength);
            var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(fragment, ':');
            if (colonIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, colonIndex + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = openBrace + 1 + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                enumContainer);
        }
    }

    private static int FirstNonWhitespaceIndex(string text)
    {
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    private static bool IsLikelyRustEnumVariantStart(string line, int startIndex)
    {
        if (startIndex < line.Length && char.IsUpper(line[startIndex]))
            return true;

        return startIndex + 2 < line.Length
            && line[startIndex] == 'r'
            && line[startIndex + 1] == '#'
            && IsRustIdentifierPart(line[startIndex + 2]);
    }

    private static void EmitAsCastTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var trimmed = preparedLine.TrimStart();
        if (trimmed.StartsWith("use ", StringComparison.Ordinal)
            || trimmed.StartsWith("pub use ", StringComparison.Ordinal)
            || trimmed.StartsWith("extern crate ", StringComparison.Ordinal)
            || trimmed.StartsWith("pub extern crate ", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var asIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "as"))
        {
            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, asIndex + "as".Length);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitAssociatedCallReceiverTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in AssociatedCallReceiverRegex.Matches(preparedLine))
        {
            var receiverGroup = match.Groups["receiver"];
            var receiver = receiverGroup.Value;
            var leafStart = receiver.LastIndexOf("::", StringComparison.Ordinal);
            var leaf = leafStart >= 0 ? receiver[(leafStart + 2)..] : receiver;
            var leafOffset = leafStart >= 0 ? leafStart + 2 : 0;
            var normalizedLeaf = NormalizeIdentifier(leaf);
            if (normalizedLeaf == "Self" || !IsLikelyRustTypePathLeaf(leaf))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                normalizedLeaf,
                receiverGroup.Index + leafOffset,
                "type_reference",
                context,
                lineNumber,
                resolveContainerForColumn(receiverGroup.Index));

            var argsGroup = match.Groups["args"];
            if (!argsGroup.Success)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                argsGroup.Value,
                argsGroup.Index,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(argsGroup.Index));
        }
    }

    private static void EmitAssociatedValueReceiverTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in AssociatedValueReceiverRegex.Matches(preparedLine))
        {
            var receiverGroup = match.Groups["receiver"];
            var receiver = receiverGroup.Value;
            var leafStart = receiver.LastIndexOf("::", StringComparison.Ordinal);
            var leaf = leafStart >= 0 ? receiver[(leafStart + 2)..] : receiver;
            var leafOffset = leafStart >= 0 ? leafStart + 2 : 0;
            var normalizedLeaf = NormalizeIdentifier(leaf);
            if (normalizedLeaf == "Self" || !IsLikelyRustTypePathLeaf(leaf))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                normalizedLeaf,
                receiverGroup.Index + leafOffset,
                "type_reference",
                context,
                lineNumber,
                resolveContainerForColumn(receiverGroup.Index));

            var argsGroup = match.Groups["args"];
            if (!argsGroup.Success)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                argsGroup.Value,
                argsGroup.Index,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(argsGroup.Index));
        }
    }

    private static void EmitQualifiedAssociatedCallReceiverTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var searchIndex = 0;
        while (searchIndex < preparedLine.Length)
        {
            var openAngle = preparedLine.IndexOf('<', searchIndex);
            if (openAngle < 0)
                return;

            searchIndex = openAngle + 1;
            var closeAngle = ReferenceExtractor.FindMatchingChar(preparedLine, openAngle, '<', '>');
            if (closeAngle <= openAngle)
                continue;

            var afterClose = SkipWhitespace(preparedLine, closeAngle + 1);
            if (afterClose + 2 > preparedLine.Length
                || preparedLine[afterClose] != ':'
                || preparedLine[afterClose + 1] != ':')
            {
                continue;
            }

            var methodStart = SkipWhitespace(preparedLine, afterClose + 2);
            if (methodStart >= preparedLine.Length || !IsRustIdentifierStart(preparedLine[methodStart]))
                continue;

            var methodEnd = methodStart + 1;
            while (methodEnd < preparedLine.Length && IsRustIdentifierPart(preparedLine[methodEnd]))
                methodEnd++;

            var callOpen = SkipWhitespace(preparedLine, methodEnd);
            if (callOpen >= preparedLine.Length || preparedLine[callOpen] != '(')
                continue;

            var qualified = preparedLine.Substring(openAngle + 1, closeAngle - openAngle - 1);
            foreach (var asIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(qualified, "as"))
            {
                EmitQualifiedAssociatedCallTypePart(
                    qualified,
                    openAngle + 1,
                    0,
                    asIndex,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn);
                EmitQualifiedAssociatedCallTypePart(
                    qualified,
                    openAngle + 1,
                    asIndex + "as".Length,
                    qualified.Length,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn);
                break;
            }
        }
    }

    private static void EmitQualifiedAssociatedCallTypePart(
        string qualified,
        int qualifiedStart,
        int partStart,
        int partEnd,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(qualified, partStart);
        while (partEnd > typeStart && char.IsWhiteSpace(qualified[partEnd - 1]))
            partEnd--;
        if (partEnd <= typeStart)
            return;

        var absoluteStart = qualifiedStart + typeStart;
        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            qualified.Substring(typeStart, partEnd - typeStart),
            absoluteStart,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(absoluteStart));
    }

    private static bool IsLikelyRustTypePathLeaf(string leaf)
    {
        if (leaf.StartsWith("r#", StringComparison.Ordinal))
            return leaf.Length > 2 && IsRustIdentifierPart(leaf[2]);

        return leaf.Length > 0 && char.IsUpper(leaf[0]);
    }

    private static void EmitStructLiteralInstantiationReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? enumContainer)
    {
        if (enumContainer != null || IsRustTypeDeclarationLine(preparedLine))
            return;

        foreach (Match match in StructLiteralRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            var name = nameGroup.Value;
            var leafStart = name.LastIndexOf("::", StringComparison.Ordinal);
            var leaf = leafStart >= 0 ? name[(leafStart + 2)..] : name;
            var leafOffset = leafStart >= 0 ? leafStart + 2 : 0;
            var normalizedLeaf = NormalizeIdentifier(leaf);
            if (normalizedLeaf == "Self" || !IsLikelyRustTypePathLeaf(leaf))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                normalizedLeaf,
                nameGroup.Index + leafOffset,
                "instantiate",
                context,
                lineNumber,
                resolveContainerForColumn(nameGroup.Index));

            var argsGroup = match.Groups["args"];
            if (!argsGroup.Success)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                argsGroup.Value,
                argsGroup.Index,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(argsGroup.Index));
        }
    }

    private static bool IsRustTypeDeclarationLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("pub", StringComparison.Ordinal))
        {
            var afterPub = "pub".Length;
            if (afterPub < trimmed.Length && trimmed[afterPub] == '(')
            {
                var closeParen = ReferenceExtractor.FindMatchingChar(trimmed, afterPub, '(', ')');
                if (closeParen > afterPub)
                    trimmed = trimmed[(closeParen + 1)..].TrimStart();
            }
            else if (afterPub < trimmed.Length && char.IsWhiteSpace(trimmed[afterPub]))
            {
                trimmed = trimmed[afterPub..].TrimStart();
            }
        }

        return trimmed.StartsWith("struct ", StringComparison.Ordinal)
               || trimmed.StartsWith("enum ", StringComparison.Ordinal)
               || trimmed.StartsWith("union ", StringComparison.Ordinal);
    }

    private static void EmitImplAndTraitTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var implIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "impl");
        if (implIndex >= 0)
        {
            var typeListStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, implIndex + "impl".Length);
            var forIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "for");
            var typeListEnd = forIndex >= 0
                ? forIndex
                : TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeListStart);

            if (typeListEnd > typeListStart)
            {
                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    preparedLine.Substring(typeListStart, typeListEnd - typeListStart),
                    typeListStart,
                    "rust",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(typeListStart));
            }

            if (forIndex >= 0)
            {
                var targetStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, forIndex + "for".Length);
                var targetEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, targetStart);
                if (targetEnd > targetStart)
                {
                    TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                        preparedLine.Substring(targetStart, targetEnd - targetStart),
                        targetStart,
                        "rust",
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        resolveContainerForColumn(targetStart));
                }
            }
        }

        var traitIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "trait");
        if (traitIndex < 0)
            return;

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', traitIndex + "trait".Length);
        if (colonIndex < 0)
            return;

        var boundsStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        var boundsEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, boundsStart);
        if (boundsEnd <= boundsStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(boundsStart, boundsEnd - boundsStart),
            boundsStart,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(boundsStart));

        var fullBoundsEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, boundsStart, stopAtArrow: false);
        if (fullBoundsEnd > boundsStart)
        {
            EmitFunctionTraitReturnTypeFromExpression(
                preparedLine.Substring(boundsStart, fullBoundsEnd - boundsStart),
                boundsStart,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
    }

    private static void EmitGenericBoundReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var genericOpenIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '<');
        if (genericOpenIndex >= 0)
        {
            var constGenericNames = EmitConstGenericParameterReferences(
                preparedLine,
                genericOpenIndex,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            EmitConstGenericUsageReferences(
                preparedLine,
                genericOpenIndex,
                constGenericNames,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            TypedLanguageReferenceExtractor.EmitGenericColonBoundReferences(
                preparedLine,
                genericOpenIndex,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            EmitGenericDefaultTypeReferences(
                preparedLine,
                genericOpenIndex,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            EmitGenericFunctionTraitReturnTypeReferences(
                preparedLine,
                genericOpenIndex,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }

        TypedLanguageReferenceExtractor.EmitWhereClauseTypeReferences(
            preparedLine,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        EmitWhereClauseConstGenericReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        EmitWhereClauseFunctionTraitReturnTypeReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static HashSet<string> EmitConstGenericParameterReferences(
        string preparedLine,
        int genericOpenIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var constGenericNames = new HashSet<string>(StringComparer.Ordinal);
        var genericCloseIndex = FindRustGenericClose(preparedLine, genericOpenIndex);
        if (genericCloseIndex <= genericOpenIndex)
            return constGenericNames;

        var clause = preparedLine.Substring(genericOpenIndex + 1, genericCloseIndex - genericOpenIndex - 1);
        EmitConstGenericSegments(
            clause,
            genericOpenIndex + 1,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            constGenericNames);
        return constGenericNames;
    }

    private static void EmitConstGenericUsageReferences(
        string preparedLine,
        int genericOpenIndex,
        HashSet<string> constGenericNames,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (constGenericNames.Count == 0)
            return;

        var genericCloseIndex = FindRustGenericClose(preparedLine, genericOpenIndex);
        if (genericCloseIndex <= genericOpenIndex)
            return;

        for (var index = genericCloseIndex + 1; index < preparedLine.Length; index++)
        {
            if (!IsRustIdentifierStart(preparedLine[index]))
                continue;

            var end = index + 1;
            while (end < preparedLine.Length && IsRustIdentifierPart(preparedLine[end]))
                end++;

            var name = preparedLine.Substring(index, end - index);
            if (constGenericNames.Contains(name))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    name,
                    index,
                    "const_generic_reference",
                    context,
                    lineNumber,
                    resolveContainerForColumn(index));
            }

            index = end - 1;
        }
    }

    private static void EmitWhereClauseConstGenericReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var whereIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "where"))
        {
            var clauseStart = whereIndex + "where".Length;
            var clauseEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, clauseStart, stopAtComma: false, stopAtArrow: false);
            if (clauseEnd <= clauseStart)
                clauseEnd = preparedLine.Length;

            EmitConstGenericSegments(
                preparedLine.Substring(clauseStart, clauseEnd - clauseStart),
                clauseStart,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
    }

    private static void EmitConstGenericSegments(
        string clause,
        int clauseStart,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        HashSet<string>? constGenericNames = null)
    {
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
        {
            var fragment = clause.Substring(segmentStart, segmentLength);
            var match = ConstGenericParameterRegex.Match(fragment);
            if (!match.Success)
                continue;

            var nameGroup = match.Groups["name"];
            var name = NormalizeIdentifier(nameGroup.Value);
            constGenericNames?.Add(name);
            var absoluteNameStart = clauseStart + segmentStart + nameGroup.Index;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                absoluteNameStart,
                "const_generic_reference",
                context,
                lineNumber,
                resolveContainerForColumn(absoluteNameStart));

            var typeGroup = match.Groups["type"];
            var typeMatch = ConstGenericTypeHeadRegex.Match(typeGroup.Value);
            if (!typeMatch.Success)
                continue;

            var typeNameGroup = typeMatch.Groups["name"];
            var absoluteTypeStart = clauseStart + segmentStart + typeGroup.Index + typeNameGroup.Index;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                NormalizeIdentifier(typeNameGroup.Value),
                absoluteTypeStart,
                "annotation",
                context,
                lineNumber,
                resolveContainerForColumn(absoluteTypeStart));
        }
    }

    private static void EmitGenericFunctionTraitReturnTypeReferences(
        string preparedLine,
        int genericOpenIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var genericCloseIndex = FindRustGenericClose(preparedLine, genericOpenIndex);
        if (genericCloseIndex <= genericOpenIndex)
            return;

        var clause = preparedLine.Substring(genericOpenIndex + 1, genericCloseIndex - genericOpenIndex - 1);
        EmitFunctionTraitReturnTypesFromBoundClause(
            clause,
            genericOpenIndex + 1,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitWhereClauseFunctionTraitReturnTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var whereIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "where"))
        {
            var clauseStart = whereIndex + "where".Length;
            var clauseEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, clauseStart, stopAtComma: false, stopAtArrow: false);
            if (clauseEnd <= clauseStart)
                clauseEnd = preparedLine.Length;

            EmitFunctionTraitReturnTypesFromBoundClause(
                preparedLine.Substring(clauseStart, clauseEnd - clauseStart),
                clauseStart,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
    }

    private static void EmitFunctionTraitReturnTypesFromBoundClause(
        string clause,
        int clauseStart,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
        {
            var fragment = clause.Substring(segmentStart, segmentLength);
            var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(fragment, ':');
            if (colonIndex < 0)
                continue;

            var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(fragment, "->", colonIndex + 1);
            if (arrowIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, arrowIndex + 2);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart, stopAtArrow: false);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = clauseStart + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart));
        }
    }

    private static void EmitFunctionTraitReturnTypeFromExpression(
        string expression,
        int expressionStart,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(expression, "->");
        if (arrowIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(expression, arrowIndex + 2);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(expression, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        var absoluteStart = expressionStart + typeStart;
        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            expression.Substring(typeStart, typeEnd - typeStart),
            absoluteStart,
            "rust",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(absoluteStart));
    }

    private static void EmitGenericDefaultTypeReferences(
        string preparedLine,
        int genericOpenIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var genericCloseIndex = FindRustGenericClose(preparedLine, genericOpenIndex);
        if (genericCloseIndex <= genericOpenIndex)
            return;

        var clause = preparedLine.Substring(genericOpenIndex + 1, genericCloseIndex - genericOpenIndex - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
        {
            var fragment = clause.Substring(segmentStart, segmentLength);
            var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(fragment, '=');
            if (assignmentIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, assignmentIndex + 1);
            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = genericOpenIndex + 1 + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "rust",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart));
        }
    }

    private static int FindRustGenericClose(string text, int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < text.Length; index++)
        {
            if (text[index] == '-' && index + 1 < text.Length && text[index + 1] == '>')
            {
                index++;
                continue;
            }

            if (text[index] == '<')
            {
                depth++;
                continue;
            }

            if (text[index] != '>')
                continue;

            depth--;
            if (depth == 0)
                return index;
        }

        return -1;
    }

    public static string NormalizeIdentifier(string identifier)
    {
        if (identifier.Length == 0)
            return identifier;

        var segments = identifier.Split("::");
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.StartsWith("r#", StringComparison.Ordinal))
                segments[i] = segment[2..];
        }

        return string.Join("::", segments);
    }

    public static bool IsFunctionDeclarationCallSite(string line, int callIndex)
    {
        if (callIndex <= 0)
            return false;

        var prefix = line[..callIndex].TrimEnd();
        return prefix.EndsWith("fn", StringComparison.Ordinal);
    }

    public static bool IsDeriveAttributeCallSite(string line, string name, int callIndex)
    {
        if (!string.Equals(name, "derive", StringComparison.Ordinal) || callIndex <= 0)
            return false;

        var index = callIndex - 1;
        while (index >= 0 && char.IsWhiteSpace(line[index]))
            index--;

        if (index < 0 || line[index] != '[')
            return false;

        index--;
        while (index >= 0 && char.IsWhiteSpace(line[index]))
            index--;

        if (index >= 0 && line[index] == '!')
        {
            index--;
            while (index >= 0 && char.IsWhiteSpace(line[index]))
                index--;
        }

        return index >= 0 && line[index] == '#';
    }

    public static bool IsLikelyInstantiationCallName(string originalName, string normalizedName, string line, int callIndex)
    {
        var normalizedLeaf = LastPathSegment(normalizedName);
        var originalLeaf = LastPathSegment(originalName);
        if (!IsLikelyRustTypePathLeaf(originalLeaf) && !IsLikelyRustTypePathLeaf(normalizedLeaf))
            return false;

        var afterName = callIndex + originalName.Length;
        while (afterName < line.Length && char.IsWhiteSpace(line[afterName]))
            afterName++;

        if (afterName >= line.Length)
            return false;

        if (line[afterName] == '!')
            return false;

        return line[afterName] is '(' or '<'
               || (afterName + 1 < line.Length && line[afterName] == ':' && line[afterName + 1] == ':');
    }

    private static string LastPathSegment(string name)
    {
        var leafStart = name.LastIndexOf("::", StringComparison.Ordinal);
        return leafStart >= 0 ? name[(leafStart + 2)..] : name;
    }

    public static bool IsRawIdentifierPrefix(string line, int callIndex) =>
        callIndex >= 2
        && line[callIndex - 2] == 'r'
        && line[callIndex - 1] == '#';
}
