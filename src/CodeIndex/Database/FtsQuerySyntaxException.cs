namespace CodeIndex.Database;

internal enum FtsQuerySyntaxErrorKind
{
    General,
    ColumnQualifier,
}

internal sealed class FtsQuerySyntaxException : Exception
{
    public FtsQuerySyntaxException(string message, FtsQuerySyntaxErrorKind kind = FtsQuerySyntaxErrorKind.General)
        : base(message)
    {
        Kind = kind;
    }

    public FtsQuerySyntaxException(
        string message,
        Exception innerException,
        FtsQuerySyntaxErrorKind kind = FtsQuerySyntaxErrorKind.General)
        : base(message, innerException)
    {
        Kind = kind;
    }

    internal FtsQuerySyntaxErrorKind Kind { get; }
}
