using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Mcp;
using CodeIndex.Models;
using CodeIndex.Security;

namespace CodeIndex.Lsp;

internal sealed partial class LspServer : IDisposable
{
    private readonly record struct SemanticToken(int Line, int StartCharacter, int Length, int TokenType, int TokenModifiers);

    private static IEnumerable<SemanticToken> RemoveOverlappingSemanticTokens(IEnumerable<SemanticToken> candidates)
    {
        var selected = new List<SemanticToken>();
        foreach (var candidate in candidates)
        {
            if (selected.Any(existing =>
                    existing.Line == candidate.Line &&
                    existing.StartCharacter < candidate.StartCharacter + candidate.Length &&
                    candidate.StartCharacter < existing.StartCharacter + existing.Length))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count == MaxSemanticTokenItems)
                break;
        }
        return selected;
    }

    private static readonly HashSet<string> CSharpModifiers = new(StringComparer.Ordinal)
    {
        "abstract", "async", "const", "extern", "file", "internal", "override", "partial",
        "private", "protected", "public", "readonly", "required", "sealed", "static", "unsafe", "virtual", "volatile",
    };

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "params", "record", "ref", "return", "sbyte",
        "short", "sizeof", "stackalloc", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "using", "ushort", "void", "while", "with", "yield",
    };

    private IEnumerable<SemanticToken> BuildCSharpLexicalSemanticTokens(
        IndexedDocumentContext document,
        Dictionary<int, string?> lineCache)
    {
        var inBlockComment = false;
        var stringMode = CSharpStringMode.None;
        var rawQuoteCount = 0;
        var ordinaryQuote = '\0';
        for (var line = 0; line < MaxSemanticTokenItems; line++)
        {
            if (!TryReadPositionLineCached(document.ResolvedPath, line, lineCache, out var sourceLine))
                yield break;

            for (var index = 0; index < sourceLine.Length;)
            {
                if (inBlockComment)
                {
                    var end = sourceLine.IndexOf("*/", index, StringComparison.Ordinal);
                    if (end < 0)
                        break;
                    inBlockComment = false;
                    index = end + 2;
                    continue;
                }

                if (stringMode == CSharpStringMode.Raw)
                {
                    var end = FindRawStringEnd(sourceLine, index, rawQuoteCount);
                    if (end < 0)
                        break;
                    stringMode = CSharpStringMode.None;
                    index = end;
                    continue;
                }

                if (stringMode == CSharpStringMode.Verbatim)
                {
                    var end = sourceLine.IndexOf('"', index);
                    if (end < 0)
                        break;
                    if (end + 1 < sourceLine.Length && sourceLine[end + 1] == '"')
                    {
                        index = end + 2;
                        continue;
                    }
                    stringMode = CSharpStringMode.None;
                    index = end + 1;
                    continue;
                }

                if (stringMode == CSharpStringMode.Ordinary)
                {
                    if (sourceLine[index] == '\\')
                    {
                        index = Math.Min(index + 2, sourceLine.Length);
                        continue;
                    }
                    if (sourceLine[index++] == ordinaryQuote)
                        stringMode = CSharpStringMode.None;
                    continue;
                }

                if (index + 1 < sourceLine.Length && sourceLine[index] == '/' && sourceLine[index + 1] == '/')
                    break;
                if (index + 1 < sourceLine.Length && sourceLine[index] == '/' && sourceLine[index + 1] == '*')
                {
                    inBlockComment = true;
                    index += 2;
                    continue;
                }
                var quoteCount = CountConsecutive(sourceLine, index, '"');
                if (quoteCount >= 3)
                {
                    stringMode = CSharpStringMode.Raw;
                    rawQuoteCount = quoteCount;
                    index += quoteCount;
                    continue;
                }
                if (sourceLine[index] == '@' && index + 1 < sourceLine.Length && sourceLine[index + 1] == '"')
                {
                    stringMode = CSharpStringMode.Verbatim;
                    index += 2;
                    continue;
                }
                if (sourceLine[index] == '@' && index + 2 < sourceLine.Length && sourceLine[index + 1] == '$' && sourceLine[index + 2] == '"')
                {
                    stringMode = CSharpStringMode.Verbatim;
                    index += 3;
                    continue;
                }
                if (sourceLine[index] is '\'' or '"')
                {
                    stringMode = CSharpStringMode.Ordinary;
                    ordinaryQuote = sourceLine[index];
                    index++;
                    continue;
                }
                if (!IsCSharpIdentifierStart(sourceLine[index]))
                {
                    index++;
                    continue;
                }

                var start = index++;
                while (index < sourceLine.Length && IsTokenChar(sourceLine[index]))
                    index++;
                var word = sourceLine[start..index].TrimStart('@');
                if (CSharpModifiers.Contains(word))
                    yield return new SemanticToken(line, start, index - start, 16, 0);
                else if (CSharpKeywords.Contains(word))
                    yield return new SemanticToken(line, start, index - start, 15, 0);
                else if (IsCSharpNamespaceComponent(sourceLine, start))
                    yield return new SemanticToken(line, start, index - start, 0, 0);
            }
        }
    }

    private static bool IsCSharpIdentifierStart(char value) => char.IsLetter(value) || value is '_' or '@';

    private enum CSharpStringMode
    {
        None,
        Ordinary,
        Verbatim,
        Raw,
    }

    private static int CountConsecutive(string text, int start, char value)
    {
        var index = start;
        while (index < text.Length && text[index] == value)
            index++;
        return index - start;
    }

    private static int FindRawStringEnd(string line, int start, int quoteCount)
    {
        for (var index = start; index < line.Length; index++)
        {
            if (line[index] == '"' && CountConsecutive(line, index, '"') >= quoteCount)
                return index + quoteCount;
        }
        return -1;
    }

    private static bool IsCSharpNamespaceComponent(string line, int start)
    {
        var trimmedStart = line.Length - line.AsSpan().TrimStart().Length;
        var trimmedLine = line.AsSpan(trimmedStart);
        var nameStart = trimmedLine.StartsWith("global using ", StringComparison.Ordinal)
            ? trimmedStart + "global using ".Length
            : trimmedLine.StartsWith("using ", StringComparison.Ordinal)
                ? trimmedStart + "using ".Length
                : trimmedLine.StartsWith("namespace ", StringComparison.Ordinal)
                    ? trimmedStart + "namespace ".Length
                    : -1;
        if (nameStart < 0 || start < nameStart)
            return false;

        var semicolon = line.IndexOf(';', nameStart);
        var brace = line.IndexOf('{', nameStart);
        var nameEnd = new[] { semicolon, brace }.Where(value => value >= 0).DefaultIfEmpty(line.Length).Min();
        var alias = line.IndexOf('=', nameStart, Math.Max(0, nameEnd - nameStart));
        var qualifiedNameStart = alias >= 0 ? alias + 1 : nameStart;
        return start >= qualifiedNameStart && start < nameEnd;
    }

    private SemanticToken? BuildSemanticToken(IndexedDocumentContext document, SymbolResult symbol, Dictionary<int, string?> lineCache)
    {
        var line = Math.Max(symbol.Line, symbol.StartLine);
        if (line <= 0)
            return null;

        var startCharacter = FindSymbolStartCharacter(document.ResolvedPath, symbol, lineCache);
        var length = Math.Max(symbol.Name.Length, 1);
        return new SemanticToken(
            line - 1,
            startCharacter,
            length,
            SemanticTokenType(symbol.Kind),
            1 << 0);
    }

    private int FindSymbolStartCharacter(string resolvedPath, SymbolResult symbol, Dictionary<int, string?>? lineCache = null)
    {
        var indexedStart = Math.Max(0, symbol.StartColumn ?? 0);
        var line = Math.Max(symbol.Line, symbol.StartLine);
        if (line <= 0 || string.IsNullOrWhiteSpace(symbol.Name))
            return indexedStart;

        if (!TryReadPositionLineCached(resolvedPath, line - 1, lineCache, out var sourceLine))
            return indexedStart;

        var searchStart = Math.Min(
            ResolveDeclarationIdentifierAnchor(sourceLine, symbol, indexedStart),
            sourceLine.Length);
        var sourceStart = FindIdentifierOccurrence(sourceLine, symbol.Name, searchStart);
        if (sourceStart >= 0)
            return sourceStart;

        sourceStart = FindIdentifierOccurrence(sourceLine, symbol.Name, 0);
        return sourceStart >= 0 ? sourceStart : indexedStart;
    }

    private static int ResolveDeclarationIdentifierAnchor(string sourceLine, SymbolResult symbol, int indexedStart)
    {
        if (string.IsNullOrWhiteSpace(symbol.Signature))
            return indexedStart;

        var firstLineEnd = symbol.Signature.IndexOfAny(['\r', '\n']);
        var signatureLine = firstLineEnd >= 0 ? symbol.Signature[..firstLineEnd] : symbol.Signature;
        var firstName = signatureLine.IndexOf(symbol.Name, StringComparison.Ordinal);
        var declarationName = FindDeclarationIdentifierOffset(signatureLine, symbol.Kind, symbol.Name);
        if (firstName < 0 || declarationName < firstName)
            return indexedStart;

        var adjusted = (long)indexedStart + declarationName - firstName;
        if (adjusted < 0 || adjusted > sourceLine.Length)
            return indexedStart;

        var candidate = (int)adjusted;
        return IsIdentifierOccurrenceAt(sourceLine, symbol.Name, candidate) ? candidate : indexedStart;
    }

    private static int FindDeclarationIdentifierOffset(string signatureLine, string kind, string name)
    {
        var headerEnd = FindDeclarationHeaderEnd(signatureLine, kind);
        var result = -1;
        var searchStart = 0;
        while (searchStart <= headerEnd)
        {
            var candidate = FindIdentifierOccurrence(signatureLine, name, searchStart);
            if (candidate < 0 || candidate + name.Length > headerEnd)
                break;
            result = candidate;
            searchStart = candidate + 1;
        }

        return result;
    }

    private static int FindDeclarationHeaderEnd(string signatureLine, string kind)
    {
        ReadOnlySpan<char> delimiters = kind switch
        {
            "function" or "test.method" => "(",
            "class" or "struct" or "interface" or "enum" or "namespace" => ":{(;",
            _ => "{=;",
        };
        var end = signatureLine.Length;
        foreach (var delimiter in delimiters)
        {
            var candidate = signatureLine.IndexOf(delimiter);
            if (candidate >= 0)
                end = Math.Min(end, candidate);
        }

        return end;
    }

    private static int FindIdentifierOccurrence(string sourceLine, string name, int startIndex)
    {
        var candidate = sourceLine.IndexOf(name, startIndex, StringComparison.Ordinal);
        while (candidate >= 0)
        {
            var hasStartBoundary = candidate == 0 || !IsIdentifierContinuation(sourceLine[candidate - 1]);
            var end = candidate + name.Length;
            var hasEndBoundary = end == sourceLine.Length || !IsIdentifierContinuation(sourceLine[end]);
            if (hasStartBoundary && hasEndBoundary)
                return candidate;

            candidate = sourceLine.IndexOf(name, candidate + 1, StringComparison.Ordinal);
        }

        return -1;
    }

    private static bool IsIdentifierOccurrenceAt(string sourceLine, string name, int start)
    {
        if (start < 0 || start + name.Length > sourceLine.Length ||
            !sourceLine.AsSpan(start, name.Length).SequenceEqual(name.AsSpan()))
        {
            return false;
        }

        var hasStartBoundary = start == 0 || !IsIdentifierContinuation(sourceLine[start - 1]);
        var end = start + name.Length;
        var hasEndBoundary = end == sourceLine.Length || !IsIdentifierContinuation(sourceLine[end]);
        return hasStartBoundary && hasEndBoundary;
    }

    private static bool IsIdentifierContinuation(char value)
    {
        var category = char.GetUnicodeCategory(value);
        return char.IsLetterOrDigit(value) ||
            value is '_' or '$' ||
            category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.ConnectorPunctuation or
                UnicodeCategory.Format;
    }

    private static int SemanticTokenType(string kind) => kind switch
    {
        "namespace" => 0,
        "class" => 2,
        "enum" => 3,
        "interface" => 4,
        "struct" => 5,
        "property" => 9,
        "field" => 23,
        "function" or "test.method" => 13,
        _ => 8,
    };

}
