using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private static bool PathsEqual(string left, string right)
        => CodeIndex.Cli.PathCasing.PathsEqual(
            NormalizePathForComparison(left),
            NormalizePathForComparison(right));

    private static bool IsPathEqualOrParent(string candidateParent, string candidateChild)
    {
        var normalizedParent = NormalizePathForComparison(candidateParent);
        var normalizedChild = NormalizePathForComparison(candidateChild);
        return CodeIndex.Cli.PathCasing.IsPathEqualOrParent(normalizedParent, normalizedChild);
    }

    private static bool IsLexicalPathEqualOrParent(string candidateParent, string candidateChild)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateParent));
        var normalizedChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateChild));
        return CodeIndex.Cli.PathCasing.IsPathEqualOrParent(normalizedParent, normalizedChild);
    }

    internal static string ResolveFileReadPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return TryResolveNativeFinalFilePath(fullPath, out var resolvedPath)
            ? Path.TrimEndingDirectorySeparator(resolvedPath)
            : NormalizePathForComparison(fullPath);
    }

    internal bool ResolvesSymlinkTargets
        => _symlinkPolicy != SymlinkPolicy.None;

    internal static bool FileReadPathsEqual(string left, string right)
        => CodeIndex.Cli.PathCasing.PathsEqual(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));

    private static bool TryResolveNativeFinalFilePath(
        string fullPath,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (OperatingSystem.IsWindows())
            return TryResolveWindowsFinalFilePath(fullPath, out resolvedPath);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        IntPtr pointer = IntPtr.Zero;
        try
        {
            pointer = UnixRealPath(fullPath, IntPtr.Zero);
            if (pointer == IntPtr.Zero)
                return false;

            var value = Marshal.PtrToStringUTF8(pointer);
            if (string.IsNullOrEmpty(value))
                return false;

            resolvedPath = Path.GetFullPath(value);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                   or EntryPointNotFoundException
                                   or ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
                UnixFree(pointer);
        }
    }

    private static bool TryResolveWindowsFinalFilePath(
        string fullPath,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            using var handle = File.OpenHandle(
                LongPath.EnsureWindowsPrefix(fullPath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var capacity = 512;
            while (capacity <= short.MaxValue)
            {
                var buffer = new StringBuilder(capacity);
                var length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    flags: 0);
                if (length == 0)
                    return false;
                if (length < buffer.Capacity)
                {
                    resolvedPath = Path.GetFullPath(
                        LongPath.RemoveWindowsPrefix(buffer.ToString()));
                    return true;
                }

                capacity = checked((int)length + 1);
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or OverflowException
                                   or DllNotFoundException
                                   or EntryPointNotFoundException)
        {
        }

        return false;
    }

    private void ValidateResolvedFileReadPath(string resolvedReadPath)
    {
        if (!IsPathEqualOrParent(_projectRoot, resolvedReadPath))
        {
            throw new IOException(
                "File symlink target resolved outside the project root before opening; rerun indexing.");
        }
    }

    private static string NormalizePathForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return Path.TrimEndingDirectorySeparator(fullPath);

        if (fullPath.Length <= root.Length)
            return Path.TrimEndingDirectorySeparator(fullPath);

        var current = root;
        var remaining = fullPath.AsSpan(root.Length);
        while (!remaining.IsEmpty)
        {
            var separatorIndex = remaining.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var segment = separatorIndex >= 0 ? remaining[..separatorIndex] : remaining;
            if (segment.Length != 0 && !IsCurrentDirectorySegment(segment))
            {
                current = Path.Join(current.AsSpan(), segment);
                current = ResolvePathComparisonSegment(current);
            }

            if (separatorIndex < 0)
                break;
            remaining = remaining[(separatorIndex + 1)..];
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static bool IsCurrentDirectorySegment(ReadOnlySpan<char> segment)
        => segment.Length == 1 && segment[0] == '.';

    private static string ResolvePathComparisonSegment(string fullPath)
    {
        try
        {
            var attributes = File.GetAttributes(fullPath);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath);
            var target = info?.ResolveLinkTarget(returnFinalTarget: true);
            if (target?.FullName is { Length: > 0 } resolvedPath)
                return resolvedPath;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return fullPath;
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr UnixRealPath(string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void UnixFree(IntPtr pointer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        StringBuilder path,
        uint pathLength,
        uint flags);

    private string ToRelativePath(string absolutePath)
    {
        if (TryGetRelativePathFromProjectRootPrefix(absolutePath, out var fastRelativePath))
            return fastRelativePath;

        var relativePath = NormalizePathSeparators(Path.GetRelativePath(_projectRoot, absolutePath));
        return relativePath == "." ? string.Empty : relativePath;
    }

    private static string CreateProjectRootRelativePrefix(string projectRoot)
        => Path.EndsInDirectorySeparator(projectRoot)
            ? projectRoot
            : projectRoot + Path.DirectorySeparatorChar;

    private bool TryGetRelativePathFromProjectRootPrefix(string absolutePath, out string relativePath)
    {
        var comparison = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(absolutePath, _projectRoot, comparison))
        {
            relativePath = string.Empty;
            return true;
        }

        if (absolutePath.StartsWith(_projectRootRelativePrefix, comparison))
        {
            relativePath = NormalizePathSeparators(absolutePath[_projectRootRelativePrefix.Length..]);
            return true;
        }

        relativePath = string.Empty;
        return false;
    }
}
