using System.Text;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private const string ConflictStartMarker = "<<<<<<<";
    private const string ConflictEndMarker = ">>>>>>>";
    internal const int ConflictMarkerScanLimitBytes = 50 * 1024;

    public static bool HasConflictMarkers(string content) => GetConflictMarkerLine(content) > 0;

    internal static int GetConflictMarkerLine(string content)
        => TryGetConflictMarkerLine(content, out var line) ? line : 0;

    internal static bool IsConflictMarkerLineStart(ReadOnlySpan<char> content)
        => content.StartsWith(ConflictStartMarker, StringComparison.Ordinal)
            || content.StartsWith(ConflictEndMarker, StringComparison.Ordinal);

    private static bool TryGetConflictMarkerLine(string content, out int line)
    {
        line = 0;
        if (string.IsNullOrEmpty(content))
            return false;
        if (!ContainsConflictMarkerCandidate(content))
            return false;

        var byteCount = 0;
        var lineNumber = 1;
        var atLineStart = true;
        for (var index = 0; index < content.Length;)
        {
            var current = content[index];
            int utf8ByteLength;
            var charCount = 1;
            if (char.IsHighSurrogate(current)
                && index + 1 < content.Length
                && char.IsLowSurrogate(content[index + 1]))
            {
                utf8ByteLength = new Rune(current, content[index + 1]).Utf8SequenceLength;
                charCount = 2;
            }
            else if (char.IsSurrogate(current))
            {
                utf8ByteLength = 3;
            }
            else
            {
                utf8ByteLength = current <= '\u007F'
                    ? 1
                    : new Rune(current).Utf8SequenceLength;
            }

            byteCount += utf8ByteLength;
            if (byteCount > ConflictMarkerScanLimitBytes)
                return false;

            if (atLineStart
                && current is '<' or '>'
                && IsConflictMarkerLineStart(content.AsSpan(index)))
            {
                line = lineNumber;
                return true;
            }

            if (current == '\n')
            {
                lineNumber++;
                atLineStart = true;
            }
            else
            {
                atLineStart = false;
            }

            index += charCount;
        }

        return false;
    }

    private static bool ContainsConflictMarkerCandidate(string content)
    {
        var scanLength = Math.Min(
            content.Length,
            ConflictMarkerScanLimitBytes + Math.Max(ConflictStartMarker.Length, ConflictEndMarker.Length));
        var searchStart = 0;
        while (searchStart < scanLength)
        {
            var relativeIndex = content.AsSpan(searchStart, scanLength - searchStart).IndexOfAny('<', '>');
            if (relativeIndex < 0)
                return false;

            var candidateIndex = searchStart + relativeIndex;
            var marker = content[candidateIndex];
            var candidate = content.AsSpan(candidateIndex);
            if (marker == '<' && candidate.StartsWith(ConflictStartMarker, StringComparison.Ordinal))
                return true;
            if (marker == '>' && candidate.StartsWith(ConflictEndMarker, StringComparison.Ordinal))
                return true;

            searchStart = candidateIndex + 1;
        }

        return false;
    }
}
