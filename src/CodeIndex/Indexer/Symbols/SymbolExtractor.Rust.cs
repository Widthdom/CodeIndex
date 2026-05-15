using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractRustUseSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryReadRustUseStatement(lines, i, out var statement, out var lineStarts, out var endLineIndex))
                continue;

            var useIndex = statement.IndexOf("use ", StringComparison.Ordinal);
            if (useIndex < 0)
                continue;

            var body = statement[(useIndex + 4)..].Trim();
            if (body.Length == 0)
                continue;

            var semicolonIndex = body.LastIndexOf(';');
            if (semicolonIndex < 0)
                continue;
            body = body[..semicolonIndex].Trim();
            if (body.Length == 0)
                continue;

            var occurrences = new List<RustUseSymbolOccurrence>();
            var bodyOffset = useIndex + 4;
            var trimmedBodyOffset = statement[(useIndex + 4)..].IndexOf(body, StringComparison.Ordinal);
            if (trimmedBodyOffset >= 0)
                bodyOffset += trimmedBodyOffset;
            CollectRustUseSymbolOccurrences(body, bodyOffset, lineStarts, i + 1, occurrences);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var occurrence in occurrences)
            {
                if (!seen.Add($"{occurrence.Name}@{occurrence.Line}:{occurrence.Column}"))
                    continue;

                var name = occurrence.Name;
                if (HasRustSymbol(symbols, fileId, occurrence.Line, "import", name))
                    continue;

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    occurrence.Line,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "import",
                        Name = name,
                        Line = occurrence.Line,
                        StartLine = occurrence.Line,
                        StartColumn = occurrence.Column,
                        EndLine = occurrence.Line,
                        Signature = statement.Trim(),
                    },
                    lines[occurrence.Line - 1]);
            }

            i = endLineIndex;
        }
    }

    private static void ExtractRustMultilineImplSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryReadRustImplStatement(lines, i, out var statement, out var lineStarts, out var endLineIndex))
                continue;

            if (endLineIndex <= i)
                continue;

            var match = RustMultilineImplForRegex.Match(statement);
            if (!match.Success)
                match = RustMultilineImplTypeRegex.Match(statement);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value.Trim();
            if (name.Length == 0)
                continue;

            var position = GetRustStatementPosition(match.Groups["name"].Index, lineStarts, i + 1);
            if (HasRustSymbol(symbols, fileId, position.Line, "class", name))
                continue;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                position.Line,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = name,
                    Line = position.Line,
                    StartLine = position.Line,
                    StartColumn = position.Column,
                    EndLine = position.Line,
                    Signature = statement.Trim(),
                },
                lines[position.Line - 1]);
        }
    }

    private static bool TryReadRustUseStatement(string[] lines, int startIndex, out string statement, out List<int> lineStarts, out int endIndex)
    {
        statement = string.Empty;
        lineStarts = [];
        endIndex = startIndex;

        var firstLine = lines[startIndex];
        if (!RustUseStartRegex.IsMatch(firstLine))
            return false;

        var builder = new StringBuilder(firstLine.Length + 32);
        var braceDepth = 0;
        lineStarts.Add(0);

        for (var i = startIndex; i < lines.Length; i++)
        {
            var current = lines[i];
            if (i > startIndex)
            {
                builder.Append('\n');
                lineStarts.Add(builder.Length);
            }
            builder.Append(current);

            foreach (var ch in current)
            {
                switch (ch)
                {
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        break;
                    case ';':
                        if (braceDepth == 0)
                        {
                            statement = builder.ToString();
                            endIndex = i;
                            return true;
                        }
                        break;
                }
            }
        }

        return false;
    }

    private static bool TryReadRustImplStatement(string[] lines, int startIndex, out string statement, out List<int> lineStarts, out int endIndex)
    {
        statement = string.Empty;
        lineStarts = [];
        endIndex = startIndex;

        var firstLine = lines[startIndex];
        if (!firstLine.TrimStart().StartsWith("impl", StringComparison.Ordinal) && !firstLine.TrimStart().StartsWith("unsafe impl", StringComparison.Ordinal))
            return false;

        var builder = new StringBuilder(firstLine.Length + 32);
        lineStarts.Add(0);

        for (var i = startIndex; i < lines.Length; i++)
        {
            var current = lines[i];
            if (i > startIndex)
            {
                builder.Append('\n');
                lineStarts.Add(builder.Length);
            }
            builder.Append(current);

            if (current.Contains('{'))
            {
                statement = builder.ToString();
                endIndex = i;
                return endIndex > startIndex;
            }
        }

        return false;
    }

    private readonly record struct RustStatementPosition(int Line, int Column);

    private static RustStatementPosition GetRustStatementPosition(int statementIndex, IReadOnlyList<int> lineStarts, int startLineNumber)
    {
        var lineIndex = 0;
        for (var i = lineStarts.Count - 1; i >= 0; i--)
        {
            if (lineStarts[i] <= statementIndex)
            {
                lineIndex = i;
                break;
            }
        }

        var column = statementIndex - lineStarts[lineIndex] + 1;
        return new RustStatementPosition(startLineNumber + lineIndex, column);
    }

    private static void CollectRustUseSymbolOccurrences(
        string body,
        int bodyOffset,
        IReadOnlyList<int> lineStarts,
        int startLineNumber,
        List<RustUseSymbolOccurrence> occurrences,
        string? prefix = null)
    {
        var text = body.Trim();
        if (text.Length == 0)
            return;

        bodyOffset += body.IndexOf(text, StringComparison.Ordinal);

        var openBraceIndex = text.IndexOf('{');
        if (openBraceIndex >= 0)
        {
            var closeBraceIndex = text.LastIndexOf('}');
            if (closeBraceIndex > openBraceIndex)
            {
                var groupedPrefix = CombineRustUsePrefix(prefix, text[..openBraceIndex].Trim());
                var inner = text[(openBraceIndex + 1)..closeBraceIndex];
                foreach (var item in SplitRustUseItems(inner))
                {
                    var itemOffset = bodyOffset + openBraceIndex + 1 + inner.IndexOf(item, StringComparison.Ordinal);
                    CollectRustUseSymbolOccurrences(item, itemOffset, lineStarts, startLineNumber, occurrences, groupedPrefix);
                }
                return;
            }
        }

        var asKeyword = FindTopLevelAsKeyword(text);
        var aliasIndex = asKeyword.Start;
        var hasPathPrefix = !string.IsNullOrWhiteSpace(prefix);

        if (hasPathPrefix && text is not "self" and not "super" and not "crate" and not "*")
            AddRustUseOccurrence(occurrences, CombineRustUsePrefix(prefix, text), bodyOffset, lineStarts, startLineNumber);

        if (aliasIndex >= 0)
        {
            var afterKeyword = aliasIndex + asKeyword.Length;
            var aliasTail = text[afterKeyword..];
            var alias = aliasTail.Trim();
            if (alias.Length > 0)
            {
                var aliasStart = bodyOffset + afterKeyword + (aliasTail.Length - aliasTail.TrimStart().Length);
                AddRustUseOccurrence(occurrences, alias, aliasStart, lineStarts, startLineNumber);
            }
        }

        var leaf = text;
        if (aliasIndex >= 0)
            leaf = text[..aliasIndex].Trim();

        if (hasPathPrefix && text == "self")
        {
            var selfIndex = text.IndexOf("self", StringComparison.Ordinal);
            if (selfIndex >= 0)
                AddRustUseOccurrence(occurrences, CombineRustUsePrefix(prefix, string.Empty), bodyOffset + selfIndex, lineStarts, startLineNumber);
        }

        if (leaf.Length == 0)
            return;

        var leafIndex = leaf.LastIndexOf("::", StringComparison.Ordinal);
        if (leafIndex >= 0)
            leaf = leaf[(leafIndex + 2)..].Trim();

        if (leaf.Length > 0 && leaf is not "self" and not "super" and not "crate" and not "*")
        {
            var leafStartInText = text.IndexOf(leaf, StringComparison.Ordinal);
            if (leafStartInText < 0)
                leafStartInText = text.IndexOf(leaf, StringComparison.Ordinal);
            var leafOffset = bodyOffset + Math.Max(0, leafStartInText);
            if (hasPathPrefix)
                AddRustUseOccurrence(occurrences, CombineRustUsePrefix(prefix, leaf), leafOffset, lineStarts, startLineNumber);
            AddRustUseOccurrence(occurrences, leaf, leafOffset, lineStarts, startLineNumber);
        }
    }

    private static void AddRustUseOccurrence(
        List<RustUseSymbolOccurrence> occurrences,
        string name,
        int absoluteOffset,
        IReadOnlyList<int> lineStarts,
        int startLineNumber)
    {
        if (name.Length == 0)
            return;

        var lineIndex = 0;
        for (var i = lineStarts.Count - 1; i >= 0; i--)
        {
            if (lineStarts[i] <= absoluteOffset)
            {
                lineIndex = i;
                break;
            }
        }

        var column = absoluteOffset - lineStarts[lineIndex] + 1;
        occurrences.Add(new RustUseSymbolOccurrence(name, startLineNumber + lineIndex, column));
    }

    private static string CombineRustUsePrefix(string? prefix, string name)
    {
        var cleanedPrefix = prefix?.Trim().TrimEnd(':');
        var cleanedName = name.Trim();

        if (string.IsNullOrWhiteSpace(cleanedPrefix))
            return cleanedName;
        if (string.IsNullOrWhiteSpace(cleanedName))
            return cleanedPrefix;
        return $"{cleanedPrefix}::{cleanedName}";
    }

    private static IEnumerable<string> SplitRustUseItems(string text)
    {
        var start = 0;
        var braceDepth = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case ',':
                    if (braceDepth == 0)
                    {
                        var item = text[start..i].Trim();
                        if (item.Length > 0)
                            yield return item;
                        start = i + 1;
                    }
                    break;
            }
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
            yield return tail;
    }

    private static int FindTopLevelChar(string text, char target)
    {
        var braceDepth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                default:
                    if (braceDepth == 0 && text[i] == target)
                        return i;
                    break;
            }
        }

        return -1;
    }

    private static int FindMatchingRustBrace(string text, int openIndex)
    {
        var braceDepth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '{')
                braceDepth++;
            else if (text[i] == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                    return i;
            }
        }

        return -1;
    }

    private readonly record struct RustAsKeywordSpan(int Start, int Length);

    private static RustAsKeywordSpan FindTopLevelAsKeyword(string text)
    {
        var braceDepth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
            }

            if (braceDepth != 0 || !char.IsWhiteSpace(ch))
                continue;

            var asStart = i + 1;
            while (asStart < text.Length && char.IsWhiteSpace(text[asStart]))
                asStart++;

            if (asStart + 2 > text.Length)
                continue;
            if (text[asStart] != 'a' || text[asStart + 1] != 's')
                continue;
            if (asStart + 2 >= text.Length || !char.IsWhiteSpace(text[asStart + 2]))
                continue;

            var endOfTrailingWs = asStart + 2;
            while (endOfTrailingWs < text.Length && char.IsWhiteSpace(text[endOfTrailingWs]))
                endOfTrailingWs++;

            return new RustAsKeywordSpan(i, endOfTrailingWs - i);
        }

        return new RustAsKeywordSpan(-1, 0);
    }

    private static bool HasRustSymbol(List<SymbolRecord> symbols, long fileId, int lineNumber, string kind, string name)
    {
        return symbols.Any(symbol =>
            symbol.FileId == fileId
            && symbol.Line == lineNumber
            && symbol.Kind == kind
            && symbol.Name == name);
    }

}
