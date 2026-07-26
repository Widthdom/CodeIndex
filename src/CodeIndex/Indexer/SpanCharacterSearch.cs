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

    internal static bool EndsWithAfterTrim(
        ReadOnlySpan<char> value,
        char suffix)
    {
        value = value.TrimEnd();
        return !value.IsEmpty && value[^1] == suffix;
    }

    internal static bool EqualsAfterTrim(
        ReadOnlySpan<char> value,
        string expected)
        => value.TrimEnd().Equals(expected, StringComparison.Ordinal);
}
