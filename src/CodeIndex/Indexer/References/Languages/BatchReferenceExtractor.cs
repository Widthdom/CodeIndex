using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class BatchReferenceExtractor
{
    private static readonly Regex JumpTargetRegex = new(
        @"^\s*@?\s*(?:(?:if\s+(?:/i\s+)?(?:not\s+)?(?:(?:errorlevel\s+\d+|defined\s+\S+|exist\s+\S+|cmdextversion\s+\d+)|(?:[^()\r\n]+?\s*(?:==|equ|neq|lss|leq|gtr|geq)\s*[^()\r\n]+?))\s+(?:\(\s*)?)?)?(?<command>goto|call)\s+:(?<name>[\w.\-]+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void EmitJumpTargetReferences(
        string originalLine,
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        if (IsCommentLine(originalLine))
            return;

        for (var segmentStart = 0; segmentStart < preparedLine.Length;)
        {
            var segmentEnd = segmentStart;
            while (segmentEnd < preparedLine.Length && !IsCommandSeparator(preparedLine, segmentEnd))
                segmentEnd++;

            ProcessJumpSegment(
                segmentStart,
                segmentEnd,
                preparedLine,
                references,
                seen,
                context,
                fileId,
                lineNumber,
                resolveContainerForCall);

            var segment = preparedLine.AsSpan(segmentStart, segmentEnd - segmentStart).TrimStart();
            if (!segment.IsEmpty)
            {
                if (segment[0] == '@')
                    segment = segment[1..].TrimStart();
                if (!segment.IsEmpty && (segment.StartsWith("::", StringComparison.Ordinal) || IsRemKeyword(segment)))
                    break;
            }

            segmentStart = segmentEnd;
            while (segmentStart < preparedLine.Length && (char.IsWhiteSpace(preparedLine[segmentStart]) || IsCommandSeparator(preparedLine, segmentStart)))
                segmentStart++;
        }
    }

    private static bool IsCommentLine(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;

        if (i >= line.Length)
            return false;

        if (line[i] == ':' && i + 1 < line.Length && line[i + 1] == ':')
            return true;

        if (line[i] == '@')
        {
            var j = i + 1;
            while (j < line.Length && (line[j] == ' ' || line[j] == '\t'))
                j++;
            return IsRemKeyword(line, j);
        }

        return IsRemKeyword(line, i);
    }

    private static bool IsRemKeyword(string line, int start)
    {
        if (start + 3 > line.Length)
            return false;
        if ((line[start] | 0x20) != 'r')
            return false;
        if ((line[start + 1] | 0x20) != 'e')
            return false;
        if ((line[start + 2] | 0x20) != 'm')
            return false;
        if (start + 3 == line.Length)
            return true;
        var next = line[start + 3];
        return next == ' ' || next == '\t' || next == '\r' || next == '\n';
    }

    private static bool IsRemKeyword(ReadOnlySpan<char> line)
    {
        if (line.Length < 3)
            return false;
        if ((line[0] | 0x20) != 'r')
            return false;
        if ((line[1] | 0x20) != 'e')
            return false;
        if ((line[2] | 0x20) != 'm')
            return false;
        if (line.Length == 3)
            return true;
        var next = line[3];
        return next == ' ' || next == '\t' || next == '\r' || next == '\n';
    }

    private static bool IsCommandSeparator(string line, int index)
    {
        var c = line[index];
        if (c is not '&' and not '|')
            return false;

        var caretCount = 0;
        for (var i = index - 1; i >= 0 && line[i] == '^'; i--)
            caretCount++;

        return (caretCount & 1) == 0;
    }

    private static void ProcessJumpSegment(
        int segmentOffset,
        int segmentEnd,
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        string context,
        long fileId,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var trimmed = preparedLine.AsSpan(segmentOffset, segmentEnd - segmentOffset).TrimStart();
        if (trimmed.IsEmpty)
            return;

        if (trimmed[0] == '@')
        {
            trimmed = trimmed[1..].TrimStart();
            if (trimmed.IsEmpty)
                return;
        }

        if (trimmed.StartsWith("::", StringComparison.Ordinal) || IsRemKeyword(trimmed))
            return;

        if (StartsWithWord(trimmed, "else") || StartsWithWord(trimmed, "do"))
        {
            var keywordEnd = 0;
            while (keywordEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[keywordEnd]))
                keywordEnd++;
            while (keywordEnd < trimmed.Length && char.IsWhiteSpace(trimmed[keywordEnd]))
                keywordEnd++;
            if (keywordEnd >= trimmed.Length)
                return;
            trimmed = trimmed[keywordEnd..].TrimStart();
        }

        var matchText = trimmed.ToString();
        var match = JumpTargetRegex.Match(matchText);
        if (!match.Success)
            return;

        var name = match.Groups["name"].Value;
        if (string.Equals(name, "eof", StringComparison.OrdinalIgnoreCase))
            return;

        var commandIndex = segmentOffset + match.Groups["command"].Index;
        var callContainer = resolveContainerForCall(commandIndex);
        ReferenceExtractor.AddReference(references, seen, fileId, name, commandIndex, "call", context, lineNumber, callContainer);
    }

    private static bool StartsWithWord(ReadOnlySpan<char> text, string word)
    {
        if (!text.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            return false;
        return text.Length == word.Length || char.IsWhiteSpace(text[word.Length]);
    }
}
