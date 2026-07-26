namespace CodeIndex.Indexer;

internal static class Utf8LineStarts
{
    internal static int[] Build(ReadOnlySpan<byte> utf8)
    {
        var lineCount = 1;
        foreach (var value in utf8)
        {
            if (value == (byte)'\n')
                lineCount++;
        }

        var starts = new int[lineCount];
        var writeIndex = 1;
        for (var index = 0; index < utf8.Length; index++)
        {
            if (utf8[index] == (byte)'\n')
                starts[writeIndex++] = index + 1;
        }

        return starts;
    }
}
