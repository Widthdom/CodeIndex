using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static string FormatCSharpWorkspaceSnapshotPath(
        string projectRoot,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "<csharp_workspace>")
            return "<csharp_workspace>";
        if (!Path.IsPathRooted(path))
            return FileIndexer.NormalizePathSeparators(path);

        try
        {
            return FileIndexer.NormalizePathSeparators(
                FileIndexer.GetRelativePathFromDirectory(projectRoot, path));
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return "<csharp_workspace>";
        }
    }
}
