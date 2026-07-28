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
using CodeIndex.Semantics;

namespace CodeIndex.Lsp;

internal sealed partial class LspServer : IDisposable
{
    private readonly record struct SemanticToken(int Line, int StartCharacter, int Length, int TokenType, int TokenModifiers);

    private IEnumerable<SemanticToken> BuildCSharpSemanticTokens(IndexedDocumentContext document)
    {
        if (!TryReadAllPositionLines(document.ResolvedPath, out var sourceLines))
        {
            foreach (var indexedToken in BuildIndexedSemanticTokens(document))
                yield return indexedToken;
            yield break;
        }

        foreach (var token in CSharpSemanticTokenClassifier.Classify(sourceLines, MaxSemanticTokenItems))
        {
            yield return new SemanticToken(
                token.Line,
                token.StartCharacter,
                token.Length,
                CSharpSemanticTokenClassifier.ToLspTokenType(token.Kind),
                token.IsDeclaration ? 1 << 0 : 0);
        }
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
