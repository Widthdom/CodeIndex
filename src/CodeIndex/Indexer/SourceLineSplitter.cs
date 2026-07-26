namespace CodeIndex.Indexer;

internal static class SourceLineSplitter
{
    internal static string[] Split(string content)
    {
        var firstLineBreak = content.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineBreak < 0)
            return [content];

        var lineCount = 2;
        for (var index = firstLineBreak + 1; index < content.Length; index++)
        {
            if (content[index] == '\n')
                lineCount++;
        }

        var lines = new string[lineCount];
        var lineIndex = 0;
        var lineStart = 0;
        for (var index = firstLineBreak; index < content.Length; index++)
        {
            if (content[index] != '\n')
                continue;

            lines[lineIndex++] = content[lineStart..index];
            lineStart = index + 1;
        }

        lines[lineIndex] = content[lineStart..];
        return lines;
    }
}
