using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static int CountIndent(string line)
    {
        int indent = 0;
        foreach (var c in line)
        {
            if (c == ' ')
                indent++;
            else if (c == '\t')
                indent += 4;
            else
                break;
        }

        return indent;
    }

    private static bool StartsWithKeyword(string line, int startIndex, string keyword)
    {
        if (startIndex < 0 || startIndex + keyword.Length > line.Length)
            return false;

        if (string.CompareOrdinal(line, startIndex, keyword, 0, keyword.Length) != 0)
            return false;

        var nextIndex = startIndex + keyword.Length;
        return nextIndex >= line.Length || char.IsWhiteSpace(line[nextIndex]);
    }

    private static string? TryGetGroup(Match match, string? groupName)
    {
        if (groupName == null || !match.Groups[groupName].Success)
            return null;

        return NormalizeMetadata(match.Groups[groupName].Value);
    }

    private static string? NormalizeMetadata(string? value)
    {
        if (value is null)
            return null;

        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return null;

        return trimmed.Length == value.Length ? value : trimmed.ToString();
    }

    private static string NormalizeExtractedSymbolName(string? lang, string name, Match match, string matchLine)
    {
        return lang switch
        {
            "csharp" => CSharpSymbolNameNormalizer.Normalize(name, match, matchLine),
            "cobol" => CobolSymbolNameNormalizer.Normalize(name),
            "fsharp" => FSharpSymbolNameNormalizer.Normalize(name),
            "java" => JavaSymbolNameNormalizer.Normalize(name),
            "kotlin" => KotlinSymbolNameNormalizer.Normalize(name, matchLine),
            "ruby" => NormalizeRubySymbolName(name, matchLine),
            "rust" => RustSymbolNameNormalizer.Normalize(name),
            "smalltalk" => NormalizeSmalltalkSelectorName(name),
            "swift" => SwiftSymbolNameNormalizer.Normalize(name),
            "vb" => NormalizeVisualBasicSymbolName(name),
            "sql" => SqlSymbolNameNormalizer.Normalize(name),
            _ => name,
        };
    }

    private static string NormalizeVisualBasicSymbolName(string name)
    {
        var trimmed = name.Trim();
        if (TryValidateVisualBasicIdentifierSegments(trimmed, out var hasEscapedSegment))
            return hasEscapedSegment
                ? StripVisualBasicIdentifierEscapes(trimmed)
                : trimmed;

        return trimmed;
    }

    private static bool TryValidateVisualBasicIdentifierSegments(string name, out bool hasEscapedSegment)
    {
        hasEscapedSegment = false;
        var segmentStart = 0;
        for (var index = 0; index <= name.Length; index++)
        {
            if (index < name.Length && name[index] != '.')
                continue;

            var segment = name.AsSpan(segmentStart, index - segmentStart);
            if (!IsVisualBasicIdentifierSegment(segment))
                return false;

            hasEscapedSegment |= IsVisualBasicEscapedIdentifier(segment);
            segmentStart = index + 1;
        }

        return true;
    }

    private static bool IsVisualBasicIdentifierSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length == 0)
            return false;
        if (IsVisualBasicEscapedIdentifier(segment))
            return true;

        foreach (var ch in segment)
        {
            if (ch != '_' && !char.IsLetterOrDigit(ch))
                return false;
        }

        return true;
    }

    private static bool IsCppTemplateSpecializationSymbol(
        string kind,
        string name,
        string signature,
        IReadOnlyList<string> lines,
        int lineIndex)
    {
        if (kind is not ("class" or "struct" or "union" or "function"))
            return false;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(signature))
            return false;
        if (!signature.Contains(name + "<", StringComparison.Ordinal))
            return false;

        var trimmedSignature = signature.AsSpan().TrimStart();
        if (trimmedSignature.StartsWith("template", StringComparison.Ordinal)
            || trimmedSignature.StartsWith("export template", StringComparison.Ordinal))
        {
            return true;
        }

        for (var previousLineIndex = lineIndex - 1; previousLineIndex >= 0; previousLineIndex--)
        {
            var previous = lines[previousLineIndex].AsSpan().Trim();
            if (previous.IsEmpty)
                continue;
            return previous.StartsWith("template", StringComparison.Ordinal)
                || previous.StartsWith("export template", StringComparison.Ordinal);
        }

        return false;
    }

    private static readonly Regex RustAssociatedTypeDefaultRegex = new(
        @"^\s*(?:(?<visibility>pub(?:\([^)]*\))?)\s+)?type\s+(?<name>(?:r#)?\w+)(?:\s*<[^=;]+>)?(?:\s*:[^=;]+)?\s*=\s*(?<returnType>[^;]+)\s*;",
        RegexOptions.Compiled);

    private static void ExtractRustAssociatedTypeDefaultSymbols(long fileId, string[] lines, string[] structuralLines, List<SymbolRecord> symbols)
    {
        if (!LinesContain(lines, "type", StringComparison.Ordinal))
            return;

        var traits = BuildRustAssociatedTypeContainerSnapshot(symbols);
        if (traits.Count == 0)
            return;

        foreach (var trait in traits)
        {
            if (!TryFindRustBraceBodyBounds(structuralLines, trait.StartLine - 1, out var startLineIndex, out var endLineIndex))
                continue;

            var depth = 1;
            for (var lineIndex = startLineIndex + 1; lineIndex < endLineIndex; lineIndex++)
            {
                if (depth == 1)
                {
                    var match = RustAssociatedTypeDefaultRegex.Match(lines[lineIndex]);
                    if (match.Success)
                    {
                        var nameGroup = match.Groups["name"];
                        var name = RustSymbolNameNormalizer.Normalize(nameGroup.Value);
                        var lineNumber = lineIndex + 1;
                        symbols.Add(new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "property",
                            Name = name,
                            Line = lineNumber,
                            StartLine = lineNumber,
                            StartColumn = nameGroup.Index,
                            EndLine = lineNumber,
                            Signature = lines[lineIndex].Trim(),
                            ContainerKind = trait.Kind,
                            ContainerName = trait.Name,
                            ContainerQualifiedName = trait.ContainerQualifiedName,
                            Visibility = match.Groups["visibility"].Success ? match.Groups["visibility"].Value : null,
                            ReturnType = match.Groups["returnType"].ValueSpan.Trim().ToString(),
                        });
                    }
                }

                depth = Math.Max(1, depth + CountBraceDelta(structuralLines[lineIndex]));
            }
        }
    }

    private static IReadOnlyList<SymbolRecord> BuildRustAssociatedTypeContainerSnapshot(IReadOnlyList<SymbolRecord> symbols)
    {
        List<(SymbolRecord Symbol, int OriginalIndex)>? candidates = null;
        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            if (symbol.Kind is ("interface" or "protocol")
                && symbol.BodyStartLine is > 0
                && symbol.BodyEndLine is > 0)
            {
                (candidates ??= []).Add((symbol, index));
            }
        }

        if (candidates is null)
            return Array.Empty<SymbolRecord>();

        if (candidates.Count == 1)
            return [candidates[0].Symbol];

        candidates.Sort(static (left, right) =>
        {
            var comparison = left.Symbol.StartLine.CompareTo(right.Symbol.StartLine);
            return comparison != 0
                ? comparison
                : left.OriginalIndex.CompareTo(right.OriginalIndex);
        });

        var snapshot = new SymbolRecord[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            snapshot[i] = candidates[i].Symbol;
        return snapshot;
    }

    private static bool TryFindRustBraceBodyBounds(string[] structuralLines, int startLineIndex, out int bodyStartLineIndex, out int bodyEndLineIndex)
    {
        bodyStartLineIndex = 0;
        bodyEndLineIndex = 0;
        if (startLineIndex < 0 || startLineIndex >= structuralLines.Length)
            return false;

        var depth = 0;
        var opened = false;
        for (var lineIndex = startLineIndex; lineIndex < structuralLines.Length; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            if (!opened)
            {
                var openColumn = line.IndexOf('{');
                if (openColumn < 0)
                    continue;

                opened = true;
                bodyStartLineIndex = lineIndex;
                depth = 1 + CountBraceDelta(line[(openColumn + 1)..]);
            }
            else
            {
                depth += CountBraceDelta(line);
            }

            if (opened && depth == 0)
            {
                bodyEndLineIndex = lineIndex;
                return true;
            }
        }

        return false;
    }

    private static int CountBraceDelta(string line)
    {
        var delta = 0;
        var inDoubleQuote = false;
        var escapeNext = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inDoubleQuote && line[index] == '\\')
            {
                escapeNext = true;
                continue;
            }

            if (line[index] == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (inDoubleQuote)
                continue;

            if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '/')
                break;

            if (line[index] == '{')
                delta++;
            else if (line[index] == '}')
                delta--;
        }

        return delta;
    }

    private static bool IsVisualBasicEscapedIdentifier(ReadOnlySpan<char> segment)
        => segment.Length >= 2 && segment[0] == '[' && segment[^1] == ']';

    private static string StripVisualBasicIdentifierEscapes(string name)
    {
        var builder = new StringBuilder(name.Length);
        var segmentStart = 0;
        for (var index = 0; index <= name.Length; index++)
        {
            if (index < name.Length && name[index] != '.')
                continue;

            if (segmentStart > 0)
                builder.Append('.');

            var segment = name.AsSpan(segmentStart, index - segmentStart);
            builder.Append(IsVisualBasicEscapedIdentifier(segment)
                ? segment[1..^1]
                : segment);
            segmentStart = index + 1;
        }

        return builder.ToString();
    }

    private static string NormalizeRubySymbolName(string name, string matchLine)
    {
        if (!matchLine.AsSpan().TrimStart().StartsWith("require", StringComparison.Ordinal))
            return name;

        var trimmed = name.AsSpan().Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '\'' && trimmed[^1] == '\'')
                || (trimmed[0] == '"' && trimmed[^1] == '"')))
        {
            return trimmed[1..^1].ToString();
        }

        return trimmed.Length == name.Length ? name : trimmed.ToString();
    }

    private static string NormalizeSmalltalkSelectorName(string name)
    {
        var trimmed = name.Trim();
        if (!trimmed.Contains(':'))
            return trimmed;

        var builder = new StringBuilder(trimmed.Length);
        var tokenStart = -1;
        for (var index = 0; index <= trimmed.Length; index++)
        {
            var atEnd = index == trimmed.Length;
            if (!atEnd && !char.IsWhiteSpace(trimmed[index]))
            {
                if (tokenStart < 0)
                    tokenStart = index;
                continue;
            }

            if (tokenStart < 0)
                continue;

            if (trimmed[index - 1] == ':')
                builder.Append(trimmed, tokenStart, index - tokenStart);

            tokenStart = -1;
        }

        return builder.Length == 0 ? trimmed : builder.ToString();
    }
}
