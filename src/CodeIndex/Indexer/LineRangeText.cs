namespace CodeIndex.Indexer;

internal static class LineRangeText
{
    internal static string Join(IReadOnlyList<string> lines, int startIndex, int endIndex, char separator = '\n')
    {
        if (lines.Count == 0 || startIndex > endIndex || startIndex >= lines.Count)
            return string.Empty;

        if (startIndex < 0)
            startIndex = 0;
        if (endIndex >= lines.Count)
            endIndex = lines.Count - 1;

        if (startIndex == endIndex)
            return lines[startIndex] ?? string.Empty;

        var totalLength = endIndex - startIndex;
        for (var lineIndex = startIndex; lineIndex <= endIndex; lineIndex++)
            totalLength += lines[lineIndex]?.Length ?? 0;

        return string.Create(
            totalLength,
            (Lines: lines, StartIndex: startIndex, EndIndex: endIndex, Separator: separator),
            static (destination, state) =>
            {
                var offset = 0;
                for (var lineIndex = state.StartIndex; lineIndex <= state.EndIndex; lineIndex++)
                {
                    if (lineIndex > state.StartIndex)
                        destination[offset++] = state.Separator;

                    var line = state.Lines[lineIndex];
                    if (!string.IsNullOrEmpty(line))
                    {
                        line.AsSpan().CopyTo(destination[offset..]);
                        offset += line.Length;
                    }
                }
            });
    }
}
