using CodeIndex.Cli;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal static bool CanIndexFile(string filePath)
        => GetFileIndexability(filePath) == FileProbeStatus.Supported;

    internal static bool HasSkippedAttributes(FileAttributes attributes, bool isWindows)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return true;

        return isWindows && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
    }

    private static bool HasSkippedAttributes(FileAttributes attributes)
        => HasSkippedAttributes(attributes, OperatingSystem.IsWindows());

    // Detect symbolic links / reparse points and Windows Hidden/System paths so the scanner can skip them.
    // Treats probe failures (e.g. dangling symlinks whose target is gone) as skipped attributes
    // so the scanner skips them instead of trying to read the missing target.
    // symlink / reparse point と Windows の Hidden/System 属性を検出し、スキャナでスキップできるようにする。
    // プローブ失敗（例: target が消えた dangling symlink）は missing target を読もうとせずスキップするため、
    // skip 対象属性扱いにする。
    private static bool HasSkippedAttributes(string path)
    {
        return FileSystemBoundary.TryGetAttributes(path, out var attributes) switch
        {
            FileSystemBoundaryProbeStatus.Found => HasSkippedAttributes(attributes),
            FileSystemBoundaryProbeStatus.Missing => true,
            _ => false,
        };
    }

    private static bool IsReparsePoint(string path)
    {
        return FileSystemBoundary.TryGetAttributes(path, out var attributes) == FileSystemBoundaryProbeStatus.Found
            && FileSystemBoundary.IsSymlinkOrReparsePoint(attributes);
    }

    private static FileProbeStatus ToFileProbeStatus(FileSystemBoundaryProbeStatus status)
        => status switch
        {
            FileSystemBoundaryProbeStatus.Missing => FileProbeStatus.Missing,
            FileSystemBoundaryProbeStatus.PermissionDenied or FileSystemBoundaryProbeStatus.IoError => OperatingSystem.IsWindows()
                ? FileProbeStatus.Supported
                : FileProbeStatus.ProbeFailed,
            _ => FileProbeStatus.ProbeFailed,
        };

    internal static FileProbeStatus GetFileIndexability(string filePath)
        => GetFileIndexability(filePath, SymlinkPolicy.None, projectRoot: null);

    internal FileProbeStatus GetFileIndexabilityForIndexing(string filePath)
        => GetFileIndexability(filePath, _symlinkPolicy, _projectRoot);

    internal static FileProbeStatus GetFileIndexability(
        string filePath,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        if (OperatingSystem.IsWindows() && IsWindowsDevicePath(filePath))
            return FileProbeStatus.Unsupported;

        // File.GetAttributes uses lstat-like semantics on .NET (does not follow the symlink target),
        // which lets us apply the active symlink policy before the Unix stat() path follows the target.
        // Windows Hidden/System paths remain rejected to avoid indexing OS-owned caches during broad scans.
        // File.GetAttributes は .NET 上で lstat 相当（symlink target を辿らない）なので、
        // Unix の stat() が target を辿る前に symlink policy を適用できる。Windows では
        // broad scan で OS 管理 cache を索引しないよう Hidden/System も引き続き弾く。
        var probeStatus = FileSystemBoundary.TryGetAttributes(filePath, out var attributes);
        if (probeStatus != FileSystemBoundaryProbeStatus.Found)
            return ToFileProbeStatus(probeStatus);

        return GetFileIndexabilityForFoundAttributes(filePath, attributes, symlinkPolicy, projectRoot);
    }

    private static FileProbeStatus GetFileIndexabilityForFoundAttributes(
        string filePath,
        FileAttributes attributes,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        if (FileSystemBoundary.IsSymlinkOrReparsePoint(attributes))
            return GetFileSymlinkIndexability(filePath, symlinkPolicy, projectRoot);

        if (HasSkippedAttributes(attributes))
            return FileProbeStatus.Unsupported;

        if (OperatingSystem.IsWindows())
            return FileProbeStatus.Supported;

        if (!UnixFileStatus.TryGetFileMode(filePath, out var mode))
            return FileProbeStatus.ProbeFailed;

        return (mode & UnixFileStatus.FileTypeMask) == UnixFileStatus.RegularFile
            ? FileProbeStatus.Supported
            : FileProbeStatus.Unsupported;
    }

    private static FileProbeStatus GetFileSymlinkIndexability(
        string filePath,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        if (symlinkPolicy == SymlinkPolicy.None)
            return FileProbeStatus.Unsupported;

        FileSystemInfo? target;
        try
        {
            FileInfo info = new(LongPath.EnsureWindowsPrefix(filePath));
            target = info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (FileNotFoundException)
        {
            return FileProbeStatus.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileProbeStatus.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return FileProbeStatus.ProbeFailed;
        }
        catch (IOException)
        {
            return FileProbeStatus.ProbeFailed;
        }

        if (target?.FullName is not { Length: > 0 } targetPath)
            return FileProbeStatus.Unsupported;

        if (symlinkPolicy == SymlinkPolicy.Internal)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !IsPathEqualOrParent(projectRoot, targetPath))
                return FileProbeStatus.Unsupported;
        }

        return GetFileIndexability(targetPath, SymlinkPolicy.None, projectRoot: null);
    }
}
