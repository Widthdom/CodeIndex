namespace CodeIndex.Indexer;

internal sealed class CaseSensitivityProbeException : IOException
{
    internal CaseSensitivityProbeException(
        string message,
        string projectRoot,
        Exception? innerException = null,
        string? probePath = null,
        string? cleanupPath = null)
        : base(message, innerException)
    {
        ProjectRoot = projectRoot;
        ProbePath = probePath;
        CleanupPath = cleanupPath;
    }

    internal string ProjectRoot { get; }
    internal string? ProbePath { get; }
    internal string? CleanupPath { get; }
}
