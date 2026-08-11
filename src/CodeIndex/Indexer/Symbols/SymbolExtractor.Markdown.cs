using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{

    private static readonly IReadOnlyDictionary<string, string> EmptyMarkdownReferenceDefinitionTargets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static List<SymbolRecord> ExtractMarkdownSymbols(long fileId, string[] lines)
    {
        // Markdown headings are the closest thing to navigable symbols in docs files.
        // Markdown の見出しは、ドキュメント内でナビゲート可能な symbol に最も近い。
        List<SymbolRecord>? symbols = null;
        Stack<(int Level, int SymbolIndex)>? headingStack = null;
        var usedHeadingIdentities = new HashSet<string>(StringComparer.Ordinal);
        var inFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;
        var fenceSymbolIndex = -1;
        var inHtmlComment = false;
        var sourceLineCount = lines.Length > 0 && lines[^1].Length == 0
            ? lines.Length - 1
            : lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            if (TryToggleMarkdownFence(lines[i], inFence, fenceChar, fenceLength, out var nextFenceChar, out var nextFenceLength, out var fenceInfo))
            {
                if (!inFence)
                {
                    var bodyStartLine = i + 2;
                    var hasBodyAtEof = bodyStartLine <= sourceLineCount;
                    var codeSymbol = new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "code",
                        Name = NormalizeMarkdownFenceInfo(fenceInfo),
                        Line = i + 1,
                        StartLine = i + 1,
                        EndLine = sourceLineCount,
                        BodyStartLine = hasBodyAtEof ? bodyStartLine : null,
                        BodyEndLine = hasBodyAtEof ? sourceLineCount : null,
                        Signature = lines[i].Trim(),
                    };

                    if (headingStack is { Count: > 0 })
                    {
                        var parent = symbols![headingStack.Peek().SymbolIndex];
                        codeSymbol.ContainerKind = "heading";
                        codeSymbol.ContainerName = parent.Name;
                    }

                    (symbols ??= []).Add(codeSymbol);
                    fenceSymbolIndex = symbols.Count - 1;
                }
                else if (fenceSymbolIndex >= 0)
                {
                    symbols![fenceSymbolIndex].EndLine = i + 1;
                    symbols[fenceSymbolIndex].BodyEndLine = Math.Max(symbols[fenceSymbolIndex].BodyStartLine ?? i + 1, i);
                    fenceSymbolIndex = -1;
                }

                inFence = nextFenceLength > 0;
                fenceChar = nextFenceChar;
                fenceLength = nextFenceLength;
                continue;
            }

            if (inFence)
                continue;

            AddMarkdownExplicitAnchorSymbols(
                fileId,
                lines[i],
                i + 1,
                headingStack,
                ref inHtmlComment,
                ref symbols);

            if (i + 1 < lines.Length
                && TryParseMarkdownSetextHeading(lines[i], lines[i + 1], out var setextLevel, out var setextHeadingText))
            {
                while (headingStack is { Count: > 0 } && headingStack.Peek().Level >= setextLevel)
                {
                    var closedHeading = headingStack.Pop();
                    CloseMarkdownHeading(symbols!, closedHeading.SymbolIndex, i);
                }

                var setextSymbol = new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "heading",
                    Name = setextHeadingText,
                    IdentityNameFolded = MarkdownAnchorIdentity.CreateUniqueHeadingIdentity(
                        setextHeadingText,
                        usedHeadingIdentities),
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 2,
                    BodyStartLine = i + 3,
                    BodyEndLine = sourceLineCount,
                    Signature = lines[i].TrimEnd(),
                };

                if (headingStack is { Count: > 0 })
                {
                    var parent = symbols![headingStack.Peek().SymbolIndex];
                    setextSymbol.ContainerKind = "heading";
                    setextSymbol.ContainerName = parent.Name;
                }

                (symbols ??= []).Add(setextSymbol);
                (headingStack ??= new Stack<(int Level, int SymbolIndex)>()).Push((setextLevel, symbols.Count - 1));
                i++;
                continue;
            }

            if (!TryParseMarkdownHeading(lines[i], out var level, out var headingText))
                continue;

            while (headingStack is { Count: > 0 } && headingStack.Peek().Level >= level)
            {
                var closedHeading = headingStack.Pop();
                CloseMarkdownHeading(symbols!, closedHeading.SymbolIndex, i);
            }

            var symbol = new SymbolRecord
            {
                FileId = fileId,
                Kind = "heading",
                Name = headingText,
                IdentityNameFolded = MarkdownAnchorIdentity.CreateUniqueHeadingIdentity(
                    headingText,
                    usedHeadingIdentities),
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
                BodyStartLine = i + 2,
                BodyEndLine = sourceLineCount,
                Signature = lines[i].Trim(),
            };

            if (headingStack is { Count: > 0 })
            {
                var parent = symbols![headingStack.Peek().SymbolIndex];
                symbol.ContainerKind = "heading";
                symbol.ContainerName = parent.Name;
            }

            (symbols ??= []).Add(symbol);
            (headingStack ??= new Stack<(int Level, int SymbolIndex)>()).Push((level, symbols.Count - 1));
        }

        while (headingStack is { Count: > 0 })
        {
            var closedHeading = headingStack.Pop();
            CloseMarkdownHeading(symbols!, closedHeading.SymbolIndex, sourceLineCount);
        }

        return symbols ?? [];
    }

    private static void CloseMarkdownHeading(List<SymbolRecord> symbols, int symbolIndex, int endLine)
    {
        var symbol = symbols[symbolIndex];
        symbol.EndLine = endLine;
        if (symbol.BodyStartLine is int bodyStartLine && bodyStartLine <= endLine)
        {
            symbol.BodyEndLine = endLine;
            return;
        }

        symbol.BodyStartLine = null;
        symbol.BodyEndLine = null;
    }

    private static void AddMarkdownExplicitAnchorSymbols(
        long fileId,
        string line,
        int lineNumber,
        Stack<(int Level, int SymbolIndex)>? headingStack,
        ref bool inHtmlComment,
        ref List<SymbolRecord>? symbols)
    {
        for (var index = 0; index < line.Length;)
        {
            if (inHtmlComment)
            {
                var commentEnd = line.IndexOf("-->", index, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return;
                inHtmlComment = false;
                index = commentEnd + 3;
                continue;
            }

            if (line.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                inHtmlComment = true;
                index += 4;
                continue;
            }

            if (line[index] == '`')
            {
                var delimiterLength = 1;
                while (index + delimiterLength < line.Length
                       && line[index + delimiterLength] == '`')
                {
                    delimiterLength++;
                }

                var closing = line.IndexOf(
                    new string('`', delimiterLength),
                    index + delimiterLength,
                    StringComparison.Ordinal);
                index = closing >= 0
                    ? closing + delimiterLength
                    : index + delimiterLength;
                continue;
            }

            if (!IsMarkdownAnchorTagStart(line, index)
                || !TryFindMarkdownTagEnd(line, index, out var tagEnd))
            {
                index++;
                continue;
            }

            AddMarkdownExplicitAnchorTagSymbols(
                fileId,
                line,
                lineNumber,
                index,
                tagEnd,
                headingStack,
                ref symbols);
            index = tagEnd + 1;
        }
    }

    private static bool IsMarkdownAnchorTagStart(string line, int index) =>
        line[index] == '<'
        && index + 2 < line.Length
        && line[index + 1] is 'a' or 'A'
        && (char.IsWhiteSpace(line[index + 2]) || line[index + 2] is '>' or '/');

    private static bool TryFindMarkdownTagEnd(string line, int tagStart, out int tagEnd)
    {
        var quote = '\0';
        for (var index = tagStart + 2; index < line.Length; index++)
        {
            var current = line[index];
            if (quote != '\0')
            {
                if (current == quote)
                    quote = '\0';
                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            if (current == '>')
            {
                tagEnd = index;
                return true;
            }
        }

        tagEnd = -1;
        return false;
    }

    private static void AddMarkdownExplicitAnchorTagSymbols(
        long fileId,
        string line,
        int lineNumber,
        int tagStart,
        int tagEnd,
        Stack<(int Level, int SymbolIndex)>? headingStack,
        ref List<SymbolRecord>? symbols)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var index = tagStart + 2;
        while (index < tagEnd)
        {
            while (index < tagEnd && (char.IsWhiteSpace(line[index]) || line[index] == '/'))
                index++;
            if (index >= tagEnd)
                break;

            var nameStart = index;
            while (index < tagEnd && IsHtmlAttrNameChar(line[index]))
                index++;
            if (index == nameStart)
            {
                index++;
                continue;
            }

            var attributeName = line.AsSpan(nameStart, index - nameStart);
            while (index < tagEnd && char.IsWhiteSpace(line[index]))
                index++;
            if (index >= tagEnd || line[index] != '=')
                continue;

            index++;
            while (index < tagEnd && char.IsWhiteSpace(line[index]))
                index++;
            if (index >= tagEnd)
                break;

            var quote = line[index] is '"' or '\'' ? line[index++] : '\0';
            var valueStart = index;
            if (quote == '\0')
            {
                while (index < tagEnd && !char.IsWhiteSpace(line[index]) && line[index] != '>')
                    index++;
            }
            else
            {
                while (index < tagEnd && line[index] != quote)
                    index++;
            }

            var valueEnd = index;
            if (quote != '\0' && index < tagEnd)
                index++;

            if (!attributeName.Equals("id", StringComparison.OrdinalIgnoreCase)
                && !attributeName.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var identity = MarkdownAnchorIdentity.NormalizeExplicitAnchorDefinition(
                line[valueStart..valueEnd]);
            if (identity.Length == 0 || !identities.Add(identity))
                continue;

            var symbol = new SymbolRecord
            {
                FileId = fileId,
                Kind = "anchor",
                Name = identity,
                IdentityNameFolded = identity,
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = lineNumber,
                Signature = line[tagStart..(tagEnd + 1)].Trim(),
            };
            if (headingStack is { Count: > 0 } && symbols != null)
            {
                var parent = symbols[headingStack.Peek().SymbolIndex];
                symbol.ContainerKind = "heading";
                symbol.ContainerName = parent.Name;
            }

            (symbols ??= []).Add(symbol);
        }
    }

    private static bool TryToggleMarkdownFence(
        string line,
        bool inFence,
        char fenceChar,
        int fenceLength,
        out char nextFenceChar,
        out int nextFenceLength,
        out string fenceInfo)
    {
        nextFenceChar = '\0';
        nextFenceLength = 0;
        fenceInfo = string.Empty;

        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ')
            index++;

        if (index > 3 || index >= line.Length)
            return false;

        var marker = line[index];
        if (marker is not ('`' or '~'))
            return false;

        var length = index;
        while (length < line.Length && line[length] == marker)
            length++;

        if (length - index < 3)
            return false;

        if (inFence && marker == fenceChar && length - index >= fenceLength)
            return true;

        if (!inFence)
        {
            nextFenceChar = marker;
            nextFenceLength = length - index;
            fenceInfo = line[length..].Trim();
            return true;
        }

        return false;
    }

    private static string NormalizeMarkdownFenceInfo(string fenceInfo)
    {
        var normalized = fenceInfo.Trim();
        if (normalized.Length == 0)
            return "code";

        var separatorIndex = normalized.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separatorIndex >= 0)
            normalized = normalized[..separatorIndex];

        return normalized.Length == 0 ? "code" : normalized;
    }

    private static bool TryParseMarkdownSetextHeading(string currentLine, string nextLine, out int level, out string headingText)
    {
        level = 0;
        headingText = string.Empty;

        var trimmedHeading = currentLine.AsSpan().Trim();
        if (trimmedHeading.IsEmpty)
            return false;

        if (TryParseMarkdownHeading(currentLine, out _, out _))
            return false;

        var trimmedUnderline = nextLine.AsSpan().Trim();
        if (trimmedUnderline.Length < 3)
            return false;

        var underlineChar = trimmedUnderline[0];
        if (underlineChar is not ('=' or '-'))
            return false;

        for (var i = 1; i < trimmedUnderline.Length; i++)
        {
            if (trimmedUnderline[i] != underlineChar)
                return false;
        }

        level = underlineChar == '=' ? 1 : 2;
        headingText = trimmedHeading.ToString();
        return true;
    }

    private static bool TryParseMarkdownHeading(string line, out int level, out string headingText)
    {
        level = 0;
        headingText = string.Empty;

        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ')
            index++;

        if (index > 3 || index >= line.Length || line[index] != '#')
            return false;

        var hashStart = index;
        while (index < line.Length && line[index] == '#')
            index++;

        level = index - hashStart;
        if (level is < 1 or > 6)
            return false;

        if (index < line.Length && !char.IsWhiteSpace(line[index]))
            return false;

        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        if (index >= line.Length)
            return false;

        var headingSpan = line.AsSpan(index).Trim();
        if (headingSpan.IsEmpty)
            return false;

        var closingHashesStart = headingSpan.Length;
        while (closingHashesStart > 0 && headingSpan[closingHashesStart - 1] == '#')
            closingHashesStart--;

        if (closingHashesStart < headingSpan.Length && closingHashesStart > 0 && char.IsWhiteSpace(headingSpan[closingHashesStart - 1]))
            headingSpan = headingSpan[..(closingHashesStart - 1)].TrimEnd();

        if (headingSpan.IsEmpty)
            return false;

        headingText = headingSpan.ToString();
        return true;
    }

}
