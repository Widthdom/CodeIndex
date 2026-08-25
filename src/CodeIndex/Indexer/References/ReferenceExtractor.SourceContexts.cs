using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void NormalizeRawSourceLineContexts(
        IReadOnlyList<string> sourceLines,
        List<ReferenceRecord> references,
        int startIndex)
    {
        if ((uint)startIndex >= (uint)references.Count)
            return;

        var cachedLineIndex = -1;
        string? cachedRawLine = null;
        string? cachedTrimmedLine = null;

        for (var referenceIndex = startIndex;
             referenceIndex < references.Count;
             referenceIndex++)
        {
            var reference = references[referenceIndex];
            var lineIndex = reference.Line - 1;
            if ((uint)lineIndex >= (uint)sourceLines.Count)
                continue;

            var rawLine = sourceLines[lineIndex];
            if (!ReferenceEquals(reference.Context, rawLine))
                continue;

            if (lineIndex != cachedLineIndex
                || !ReferenceEquals(rawLine, cachedRawLine))
            {
                cachedLineIndex = lineIndex;
                cachedRawLine = rawLine;
                cachedTrimmedLine = rawLine.Length > 0
                    && !char.IsWhiteSpace(rawLine[0])
                    && !char.IsWhiteSpace(rawLine[^1])
                        ? rawLine
                        : rawLine.Trim();
            }

            reference.Context = cachedTrimmedLine!;
        }
    }
}
