namespace CodeIndex.Database;

public partial class DbReader
{
    private static readonly AsyncLocal<string?> ExactFilePath = new();

    // Apply literal indexed identity before SQL limits, including nested candidate
    // queries. Dispose before following a resolved target into another file.
    internal static IDisposable BeginExactFilePath(string? path)
        => new ExactFilePathLease(path);

    private sealed class ExactFilePathLease : IDisposable
    {
        private readonly string? _previous = ExactFilePath.Value;
        internal ExactFilePathLease(string? path) => ExactFilePath.Value = path;
        public void Dispose() => ExactFilePath.Value = _previous;
    }
}
