using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractCppSameLineClassBodyMembers(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        var classSymbols = BuildCppSameLineClassSymbolSnapshot(symbols);

        foreach (var classSymbol in classSymbols)
        {
            var lineIndex = classSymbol.StartLine - 1;
            if (lineIndex < 0 || lineIndex >= lines.Length)
                continue;

            var line = lines[lineIndex];
            var openBraceIndex = line.IndexOf('{');
            var closeBraceIndex = line.LastIndexOf('}');
            if (openBraceIndex < 0 || closeBraceIndex <= openBraceIndex)
                continue;

            var body = line[(openBraceIndex + 1)..closeBraceIndex];
            if (body.Length == 0)
                continue;

            var existingMemberNames = BuildCppSameLineClassMemberNameSet(symbols, classSymbol);
            foreach (var segment in EnumerateTrimmedCppSegments(body))
                TryAddCppSameLineClassMemberSymbol(fileId, classSymbol, segment, lineIndex + 1, symbols, existingMemberNames);
        }
    }

    private static List<SymbolRecord> BuildCppSameLineClassSymbolSnapshot(IReadOnlyList<SymbolRecord> symbols)
    {
        var classSymbols = new List<SymbolRecord>();
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is ("class" or "struct")
                && symbol.BodyStartLine.HasValue
                && symbol.BodyEndLine.HasValue
                && symbol.StartLine == symbol.BodyStartLine.Value
                && symbol.EndLine == symbol.BodyEndLine.Value)
            {
                classSymbols.Add(symbol);
            }
        }

        return classSymbols;
    }

    private static HashSet<string> BuildCppSameLineClassMemberNameSet(IReadOnlyList<SymbolRecord> symbols, SymbolRecord classSymbol)
    {
        var existingMemberNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind == "function"
                && symbol.Line == classSymbol.StartLine
                && symbol.ContainerKind == classSymbol.Kind
                && symbol.ContainerName == classSymbol.Name)
            {
                existingMemberNames.Add(symbol.Name);
            }
        }

        return existingMemberNames;
    }

    private static IEnumerable<string> EnumerateTrimmedCppSegments(string body)
    {
        var segmentStart = 0;
        while (segmentStart <= body.Length)
        {
            var delimiterIndex = body.IndexOf(';', segmentStart);
            var segmentEnd = delimiterIndex >= 0 ? delimiterIndex : body.Length;
            var trimStart = segmentStart;
            while (trimStart < segmentEnd && char.IsWhiteSpace(body[trimStart]))
                trimStart++;

            var trimEnd = segmentEnd;
            while (trimEnd > trimStart && char.IsWhiteSpace(body[trimEnd - 1]))
                trimEnd--;

            if (trimEnd > trimStart)
                yield return body[trimStart..trimEnd];

            if (delimiterIndex < 0)
                yield break;

            segmentStart = delimiterIndex + 1;
        }
    }

    private static bool TryAddCppSameLineClassMemberSymbol(
        long fileId,
        SymbolRecord classSymbol,
        string segment,
        int lineNumber,
        List<SymbolRecord> symbols,
        HashSet<string> existingMemberNames)
    {
        foreach (var pattern in PatternCache["cpp"])
        {
            if (pattern.Kind != "function")
                continue;

            var match = pattern.Regex.Match(segment);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Success
                ? match.Groups["name"].Value.Trim()
                : match.Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!existingMemberNames.Add(name))
                return true;

            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = name,
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = lineNumber,
                Signature = segment.Trim(),
                ContainerKind = classSymbol.Kind,
                ContainerName = classSymbol.Name,
                ReturnType = TryGetGroup(match, "returnType"),
            });

            return true;
        }

        return false;
    }

    private static bool TryAddCppIndentedAlias(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("namespace ", StringComparison.Ordinal))
        {
            var equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex > 0)
            {
                var aliasName = ExtractTrailingCppIdentifier(trimmed.AsSpan(0, equalsIndex));
                if (aliasName != null)
                {
                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols: null,
                        lineNumber,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "import",
                            Name = aliasName,
                            Line = lineNumber,
                            StartLine = lineNumber,
                            EndLine = lineNumber,
                            Signature = trimmed
                        });
                    return true;
                }
            }
        }

        if (trimmed.StartsWith("using namespace ", StringComparison.Ordinal))
        {
            var target = NormalizeCppUsingNamespaceTarget(trimmed["using namespace ".Length..]);

            if (target.Length > 0)
            {
                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    lineNumber,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "import",
                        Name = target,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        EndLine = lineNumber,
                        Signature = trimmed
                    });
                return true;
            }
        }

        if (trimmed.StartsWith("using typename ", StringComparison.Ordinal))
        {
            var target = NormalizeCppUsingNamespaceTarget(trimmed["using typename ".Length..]);

            if (target.Length > 0)
            {
                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    lineNumber,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "import",
                        Name = target,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        EndLine = lineNumber,
                        Signature = trimmed
                    });
                return true;
            }
        }

        if (line.Length == line.TrimStart().Length)
            return false;

        if (trimmed.StartsWith("using ", StringComparison.Ordinal)
            && !trimmed.StartsWith("using enum ", StringComparison.Ordinal)
            && !trimmed.Contains('='))
        {
            var target = NormalizeCppUsingNamespaceTarget(trimmed["using ".Length..]);
            if (target.Length > 0 && target.Contains("::", StringComparison.Ordinal))
            {
                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    lineNumber,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "import",
                        Name = target,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        EndLine = lineNumber,
                        Signature = trimmed
                    });
                return true;
            }
        }

        if (trimmed.StartsWith("using ", StringComparison.Ordinal))
        {
            var equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex <= 0)
                return false;

            var aliasName = ExtractTrailingCppIdentifier(trimmed.AsSpan(0, equalsIndex));
            if (aliasName == null)
                return false;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "import",
                    Name = aliasName,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    EndLine = lineNumber,
                    Signature = trimmed
                });
            return true;
        }

        if (!trimmed.StartsWith("typedef ", StringComparison.Ordinal) || trimmed.Contains('('))
            return false;

        var semicolonIndex = trimmed.IndexOf(';');
        if (semicolonIndex <= 0)
            return false;

        var typedefName = ExtractTrailingCppIdentifier(trimmed.AsSpan(0, semicolonIndex));
        if (typedefName == null)
            return false;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "import",
                Name = typedefName,
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = lineNumber,
                Signature = trimmed
            });
        return true;
    }

    private static string? ExtractTrailingCppIdentifier(ReadOnlySpan<char> text)
    {
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            end--;

        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            start--;

        if (start == end)
            return null;

        return text[start..end].ToString();
    }

    private static string NormalizeCppUsingNamespaceTarget(string text)
    {
        var target = text.AsSpan().Trim();
        var commentIndex = target.IndexOf("//", StringComparison.Ordinal);
        if (commentIndex < 0)
            commentIndex = target.IndexOf("/*", StringComparison.Ordinal);

        if (commentIndex >= 0)
            target = target[..commentIndex].TrimEnd();

        if (target.Length > 0 && target[^1] == ';')
            target = target[..^1].TrimEnd();

        return target.ToString();
    }

}
