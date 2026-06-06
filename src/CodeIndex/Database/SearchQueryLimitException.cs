namespace CodeIndex.Database;

internal sealed class SearchQueryLimitException : Exception
{
    public SearchQueryLimitException(string message)
        : base(message)
    {
    }
}
