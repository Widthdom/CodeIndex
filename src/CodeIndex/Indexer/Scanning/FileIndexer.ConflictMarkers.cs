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
        var lineStart = 0;
        var lineNumber = 1;
        for (int i = 0; i <= content.Length; i++)
        {
            if (i < content.Length)
            {
                byteCount += content[i] <= '\u007f' ? 1 : Encoding.UTF8.GetByteCount(content.AsSpan(i, 1));
                if (byteCount > ConflictMarkerScanLimitBytes)
                    return false;
            }

            if (i < content.Length && content[i] != '\n')
                continue;

            var lineLength = i - lineStart;
            if (lineLength > 0 && content[lineStart + lineLength - 1] == '\r')
                lineLength--;
            var currentLine = content.AsSpan(lineStart, lineLength);
            if (currentLine.StartsWith(ConflictStartMarker, StringComparison.Ordinal)
                || currentLine.StartsWith(ConflictEndMarker, StringComparison.Ordinal))
            {
                line = lineNumber;
                return true;
            }

            lineStart = i + 1;
            lineNumber++;
        }

        return false;
    }

    private static bool ContainsConflictMarkerCandidate(string content)
    {
        var searchStart = 0;
        while (searchStart < content.Length)
        {
            var relativeIndex = content.AsSpan(searchStart).IndexOfAny('<', '>');
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
