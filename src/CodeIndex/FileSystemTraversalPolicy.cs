namespace CodeIndex;

internal enum FileSystemTraversalFailureKind
{
    Permissions,
    IoError,
    InvalidPath,
    UnsupportedPath,
    PathTooLong,
    BudgetExceeded,
    Unexpected,
}

internal readonly record struct FileSystemTraversalOptions
{
    internal FileSystemTraversalOptions(int? maxEntries = null, CancellationToken cancellationToken = default)
    {
        if (maxEntries is < 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Traversal entry budget must be zero or greater.");

        MaxEntries = maxEntries;
        CancellationToken = cancellationToken;
    }

    internal static FileSystemTraversalOptions Default => default;

    internal int? MaxEntries { get; }
    internal CancellationToken CancellationToken { get; }
}

internal sealed class FileSystemTraversalBudgetExceededException : Exception
{
    internal FileSystemTraversalBudgetExceededException(int maxEntries)
        : base($"Filesystem traversal exceeded the configured entry budget of {maxEntries}.")
        => MaxEntries = maxEntries;

    internal int MaxEntries { get; }
}

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
        => EnumerateFiles(directory, searchPattern, FileSystemTraversalOptions.Default);

    internal static IEnumerable<string> EnumerateFiles(string directory, FileSystemTraversalOptions options)
        => EnumerateFiles(directory, "*", options);

    internal static IEnumerable<string> EnumerateFiles(
        string directory,
        string searchPattern,
        FileSystemTraversalOptions options)
        => EnumerateWithPolicy(
            () => Directory.EnumerateFiles(directory, searchPattern, CreateTopDirectoryOnlyOptions()),
            options);

    internal static IEnumerable<string> EnumerateDirectories(string directory, string searchPattern = "*")
        => EnumerateDirectories(directory, searchPattern, FileSystemTraversalOptions.Default);

    internal static IEnumerable<string> EnumerateDirectories(string directory, FileSystemTraversalOptions options)
        => EnumerateDirectories(directory, "*", options);

    internal static IEnumerable<string> EnumerateDirectories(
        string directory,
        string searchPattern,
        FileSystemTraversalOptions options)
        => EnumerateWithPolicy(
            () => Directory.EnumerateDirectories(directory, searchPattern, CreateTopDirectoryOnlyOptions()),
            options);

    internal static IEnumerable<string> EnumerateFileSystemEntries(string directory, string searchPattern = "*")
        => EnumerateFileSystemEntries(directory, searchPattern, FileSystemTraversalOptions.Default);

    internal static IEnumerable<string> EnumerateFileSystemEntries(string directory, FileSystemTraversalOptions options)
        => EnumerateFileSystemEntries(directory, "*", options);

    internal static IEnumerable<string> EnumerateFileSystemEntries(
        string directory,
        string searchPattern,
        FileSystemTraversalOptions options)
        => EnumerateWithPolicy(
            () => Directory.EnumerateFileSystemEntries(directory, searchPattern, CreateTopDirectoryOnlyOptions()),
            options);

    internal static bool HasAnyFileSystemEntry(string directory)
        => HasAnyFileSystemEntry(directory, FileSystemTraversalOptions.Default);

    internal static bool HasAnyFileSystemEntry(string directory, FileSystemTraversalOptions options)
    {
        using var enumerator = EnumerateFileSystemEntries(directory, options).GetEnumerator();
        return enumerator.MoveNext();
    }

    internal static bool IsExpectedTraversalException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or FileSystemTraversalBudgetExceededException;

    internal static FileSystemTraversalFailureKind ClassifyFailure(Exception ex)
        => ex switch
        {
            UnauthorizedAccessException => FileSystemTraversalFailureKind.Permissions,
            PathTooLongException => FileSystemTraversalFailureKind.PathTooLong,
            ArgumentException => FileSystemTraversalFailureKind.InvalidPath,
            NotSupportedException => FileSystemTraversalFailureKind.UnsupportedPath,
            FileSystemTraversalBudgetExceededException => FileSystemTraversalFailureKind.BudgetExceeded,
            IOException => FileSystemTraversalFailureKind.IoError,
            _ => FileSystemTraversalFailureKind.Unexpected,
        };

    internal static string DescribeFailureReason(Exception ex)
        => ClassifyFailure(ex) switch
        {
            FileSystemTraversalFailureKind.Permissions => "permissions",
            FileSystemTraversalFailureKind.PathTooLong => "a path that is too long",
            FileSystemTraversalFailureKind.InvalidPath => "an invalid path",
            FileSystemTraversalFailureKind.UnsupportedPath => "an unsupported path",
            FileSystemTraversalFailureKind.BudgetExceeded => "entry budget exceeded",
            FileSystemTraversalFailureKind.IoError => "an I/O error",
            _ => "an unexpected filesystem traversal error",
        };

    internal static string ExceptionTypeName(Exception ex)
        => ex.GetType().Name;

    private static IEnumerable<string> EnumerateWithPolicy(
        Func<IEnumerable<string>> enumerate,
        FileSystemTraversalOptions options)
    {
        options.CancellationToken.ThrowIfCancellationRequested();

        using var enumerator = enumerate().GetEnumerator();
        var entriesReturned = 0;
        while (true)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
                yield break;

            entriesReturned++;
            if (options.MaxEntries is { } maxEntries && entriesReturned > maxEntries)
                throw new FileSystemTraversalBudgetExceededException(maxEntries);

            yield return enumerator.Current;
        }
    }
}
