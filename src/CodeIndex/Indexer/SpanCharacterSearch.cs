namespace CodeIndex.Indexer;

internal static class SpanCharacterSearch
{
    internal static bool ContainsControl(ReadOnlySpan<char> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]))
                return true;
        }

        return false;
    }

    internal static bool ContainsWhitespace(ReadOnlySpan<char> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
                return true;
        }

        return false;
    }
}
