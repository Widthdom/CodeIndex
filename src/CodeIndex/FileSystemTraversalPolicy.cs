namespace CodeIndex;

internal static class FileSystemTraversalPolicy
{
    internal static EnumerationOptions CreateTopDirectoryOnlyOptions() => new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    internal static IEnumerable<string> EnumerateFiles(string directory, string searchPattern = "*")
        => Directory.EnumerateFiles(directory, searchPattern, CreateTopDirectoryOnlyOptions());

    internal static IEnumerable<string> EnumerateDirectories(string directory, string searchPattern = "*")
        => Directory.EnumerateDirectories(directory, searchPattern, CreateTopDirectoryOnlyOptions());

    internal static IEnumerable<string> EnumerateFileSystemEntries(string directory, string searchPattern = "*")
        => Directory.EnumerateFileSystemEntries(directory, searchPattern, CreateTopDirectoryOnlyOptions());

    internal static bool HasAnyFileSystemEntry(string directory)
    {
        using var enumerator = EnumerateFileSystemEntries(directory).GetEnumerator();
        return enumerator.MoveNext();
    }
}
