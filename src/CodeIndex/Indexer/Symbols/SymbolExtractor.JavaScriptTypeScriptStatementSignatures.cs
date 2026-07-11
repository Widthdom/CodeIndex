using System.Text;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static string BuildJavaScriptTypeScriptStatementSignature(
        string[] rawLines,
        int startLineIndex,
        int startColumn,
        int endLineIndex,
        int endColumn)
        => BuildJavaScriptTypeScriptTrimmedLineSliceSignature(rawLines, startLineIndex, startColumn, endLineIndex, endColumn);

    private static string BuildJavaScriptTypeScriptTrimmedLineSliceSignature(
        string[] rawLines,
        int startLineIndex,
        int startColumn,
        int endLineIndex,
        int endColumn)
    {
        if (startLineIndex == endLineIndex)
        {
            var line = rawLines[startLineIndex];
            var start = Math.Min(Math.Max(0, startColumn), line.Length);
            var endExclusive = Math.Min(Math.Max(start, endColumn + 1), line.Length);
            return line.AsSpan(start, endExclusive - start).Trim().ToString();
        }

        var firstLineIndex = -1;
        var firstColumn = -1;
        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var line = rawLines[lineIndex];
            var sliceStart = lineIndex == startLineIndex ? Math.Min(Math.Max(0, startColumn), line.Length) : 0;
            var sliceEnd = lineIndex == endLineIndex ? Math.Min(Math.Max(sliceStart, endColumn + 1), line.Length) : line.Length;
            for (var column = sliceStart; column < sliceEnd; column++)
            {
                if (!char.IsWhiteSpace(line[column]))
                {
                    firstLineIndex = lineIndex;
                    firstColumn = column;
                    break;
                }
            }

            if (firstLineIndex >= 0)
                break;
        }

        if (firstLineIndex < 0)
            return string.Empty;

        var lastLineIndex = firstLineIndex;
        var lastColumnExclusive = firstColumn + 1;
        var foundLast = false;
        for (var lineIndex = endLineIndex; lineIndex >= firstLineIndex && !foundLast; lineIndex--)
        {
            var line = rawLines[lineIndex];
            var sliceStart = lineIndex == startLineIndex ? Math.Min(Math.Max(0, startColumn), line.Length) : 0;
            var sliceEnd = lineIndex == endLineIndex ? Math.Min(Math.Max(sliceStart, endColumn + 1), line.Length) : line.Length;
            if (lineIndex == firstLineIndex)
                sliceStart = firstColumn;

            for (var column = sliceEnd - 1; column >= sliceStart; column--)
            {
                if (!char.IsWhiteSpace(line[column]))
                {
                    lastLineIndex = lineIndex;
                    lastColumnExclusive = column + 1;
                    foundLast = true;
                    break;
                }
            }
        }

        var builder = new StringBuilder(EstimateJavaScriptTypeScriptLineSliceLength(rawLines, startLineIndex, endLineIndex));
        for (var lineIndex = firstLineIndex; lineIndex <= lastLineIndex; lineIndex++)
        {
            if (lineIndex > firstLineIndex)
                builder.Append('\n');

            var line = rawLines[lineIndex];
            var sliceStart = lineIndex == startLineIndex ? Math.Min(Math.Max(0, startColumn), line.Length) : 0;
            var sliceEnd = lineIndex == endLineIndex ? Math.Min(Math.Max(sliceStart, endColumn + 1), line.Length) : line.Length;
            if (lineIndex == firstLineIndex)
                sliceStart = firstColumn;
            if (lineIndex == lastLineIndex)
                sliceEnd = lastColumnExclusive;
            builder.Append(line.AsSpan(sliceStart, sliceEnd - sliceStart));
        }

        return builder.ToString();
    }

    private static string BuildJavaScriptTypeScriptDynamicImportSignature(
        string[] rawLines,
        int startLineIndex,
        int endLineIndex,
        int endColumn)
        => BuildJavaScriptTypeScriptTrimmedLineSliceSignature(rawLines, startLineIndex, 0, endLineIndex, endColumn);

    private static int EstimateJavaScriptTypeScriptLineSliceLength(string[] lines, int startLineIndex, int endLineIndex)
    {
        if (lines.Length == 0)
            return 0;

        startLineIndex = Math.Clamp(startLineIndex, 0, lines.Length - 1);
        endLineIndex = Math.Clamp(endLineIndex, startLineIndex, lines.Length - 1);

        var capacity = 0;
        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
            capacity += lines[lineIndex].Length + (lineIndex > startLineIndex ? 1 : 0);

        return capacity;
    }

    private static int EstimateJavaScriptTypeScriptStatementCapacity(string[] lines, int startLineIndex)
    {
        if (lines.Length == 0)
            return 0;

        startLineIndex = Math.Clamp(startLineIndex, 0, lines.Length - 1);
        var endExclusive = Math.Min(lines.Length, startLineIndex + 16);
        var capacity = 0;
        for (var lineIndex = startLineIndex; lineIndex < endExclusive; lineIndex++)
            capacity += lines[lineIndex].Length + (lineIndex > startLineIndex ? 1 : 0);

        return capacity;
    }
}
