using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class GitHelper
{
    private const int MaxDoctorGitIndexBytes = 128 * 1024 * 1024;
    private const int MaxDoctorGitIndexEntries = 2_000_000;
    private const int MaxDoctorGitIndexPathBytes = 32 * 1024;
    private const int MaxDoctorPackedRefsBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictGitIndexUtf8 = new(false, true);

    internal static string? TryFindWorktreeRootWithoutProcess(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var current = new DirectoryInfo(fullPath);
            while (current != null)
            {
                var dotGit = Path.Combine(current.FullName, ".git");
                var probe = FileSystemBoundary.TryGetAttributes(LongPath.EnsureWindowsPrefix(dotGit), out var attributes);
                if (probe == FileSystemBoundaryProbeStatus.Found
                    && (TryValidateGitMetadataEntry(
                            dotGit,
                            expectDirectory: (attributes & FileAttributes.Directory) != 0,
                            out _)))
                {
                    return current.FullName;
                }
                if (probe != FileSystemBoundaryProbeStatus.Missing)
                    return null;

                current = current.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        return null;
    }

    internal static HashSet<string>? TryReadSkipWorktreePathsWithoutProcess(string projectRoot)
    {
        try
        {
            var worktreeRoot = TryFindWorktreeRootWithoutProcess(projectRoot);
            if (worktreeRoot == null
                || !TryResolveWorktreeGitDirectoryWithoutProcess(worktreeRoot, out var gitDirectory)
                || !TryResolveGitMetadataChildPath(
                    gitDirectory,
                    "index",
                    expectDirectory: false,
                    allowMissing: false,
                    out var indexPath))
            {
                return null;
            }

            var bytes = DataDirectorySecurity.ReadBytesWithinLimit(
                LongPath.EnsureWindowsPrefix(indexPath),
                MaxDoctorGitIndexBytes,
                FileShare.ReadWrite);
            if (bytes == null || bytes.Length < 32)
                return null;

            var hashLength = ResolveGitIndexHashLength(bytes);
            if (hashLength == 0
                || !TryParseSkipWorktreePaths(bytes, hashLength, out var repositoryPaths))
            {
                return null;
            }

            return RebaseSkipWorktreePaths(repositoryPaths, worktreeRoot, projectRoot);
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException
                                       or DecoderFallbackException
                                       or CryptographicException
                                       or OverflowException)
        {
            return null;
        }
    }

    internal static string? TryReadHeadCommitWithoutProcess(string projectRoot)
    {
        try
        {
            var worktreeRoot = TryFindWorktreeRootWithoutProcess(projectRoot);
            if (worktreeRoot == null
                || !TryResolveWorktreeGitDirectoryWithoutProcess(worktreeRoot, out var gitDirectory)
                || !TryResolveGitMetadataChildPath(
                    gitDirectory,
                    "HEAD",
                    expectDirectory: false,
                    allowMissing: false,
                    out var headPath))
            {
                return null;
            }

            var head = DataDirectorySecurity.ReadTextWithinLimit(
                LongPath.EnsureWindowsPrefix(headPath),
                MaxGitMetadataFileBytes)?.Trim();
            if (IsFullGitObjectId(head))
                return head;
            if (head == null || !head.StartsWith("ref: ", StringComparison.Ordinal))
                return null;

            var referenceName = head["ref: ".Length..].Trim();
            if (!IsSafeGitReferenceName(referenceName))
                return null;

            var commonDirectory = ResolveGitCommonDir(worktreeRoot);
            if (TryReadLooseGitReference(gitDirectory, referenceName, out var objectId)
                || (commonDirectory != null
                    && !PathCasing.PathsEqual(commonDirectory, gitDirectory)
                    && TryReadLooseGitReference(commonDirectory, referenceName, out objectId)))
            {
                return objectId;
            }

            return commonDirectory == null
                ? null
                : TryReadPackedGitReference(commonDirectory, referenceName);
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryResolveWorktreeGitDirectoryWithoutProcess(
        string worktreeRoot,
        out string gitDirectory)
    {
        var dotGit = Path.Combine(worktreeRoot, ".git");
        if (TryValidateGitMetadataEntry(dotGit, expectDirectory: true, out gitDirectory))
            return true;
        if (!TryValidateGitMetadataEntry(dotGit, expectDirectory: false, out var gitFile))
        {
            gitDirectory = string.Empty;
            return false;
        }

        var content = DataDirectorySecurity.ReadTextWithinLimit(
            LongPath.EnsureWindowsPrefix(gitFile),
            MaxGitMetadataFileBytes);
        if (content == null)
        {
            gitDirectory = string.Empty;
            return false;
        }

        content = content.Trim();
        if (!content.StartsWith("gitdir:", StringComparison.Ordinal)
            || !TryResolveGitMetadataPath(
                worktreeRoot,
                content["gitdir:".Length..].Trim(),
                out gitDirectory)
            || !TryValidateGitMetadataEntry(gitDirectory, expectDirectory: true, out gitDirectory))
        {
            gitDirectory = string.Empty;
            return false;
        }

        return true;
    }

    private static int ResolveGitIndexHashLength(byte[] bytes)
    {
        if (bytes.Length > SHA256.HashSizeInBytes
            && SHA256.HashData(bytes.AsSpan(0, bytes.Length - SHA256.HashSizeInBytes))
                .AsSpan()
                .SequenceEqual(bytes.AsSpan(bytes.Length - SHA256.HashSizeInBytes)))
        {
            return SHA256.HashSizeInBytes;
        }

        if (bytes.Length > SHA1.HashSizeInBytes
            && SHA1.HashData(bytes.AsSpan(0, bytes.Length - SHA1.HashSizeInBytes))
                .AsSpan()
                .SequenceEqual(bytes.AsSpan(bytes.Length - SHA1.HashSizeInBytes)))
        {
            return SHA1.HashSizeInBytes;
        }

        return 0;
    }

    private static bool TryParseSkipWorktreePaths(
        byte[] bytes,
        int hashLength,
        out HashSet<string> paths)
    {
        paths = new HashSet<string>(StringComparer.Ordinal);
        var contentLength = bytes.Length - hashLength;
        if (contentLength < 12
            || !bytes.AsSpan(0, 4).SequenceEqual("DIRC"u8))
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
        if (version is < 2 or > 4 || entryCount > MaxDoctorGitIndexEntries)
            return false;

        var objectIdBytes = hashLength;
        var offset = 12;
        byte[] previousPathBytes = [];
        for (var entryIndex = 0U; entryIndex < entryCount; entryIndex++)
        {
            var entryStart = offset;
            var fixedBytes = checked(40 + objectIdBytes + 2);
            if (offset > contentLength - fixedBytes)
                return false;
            var mode = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entryStart + 24, 4));
            offset += 40 + objectIdBytes;
            var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            offset += 2;

            ushort extendedFlags = 0;
            if ((flags & 0x4000) != 0)
            {
                if (offset > contentLength - 2)
                    return false;
                extendedFlags = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
                offset += 2;
            }

            byte[] pathBytes;
            if (version == 4)
            {
                if (!TryReadGitIndexVarInt(bytes, contentLength, ref offset, out var removeCount)
                    || removeCount > previousPathBytes.Length
                    || !TryReadGitIndexPath(bytes, contentLength, ref offset, expectedLength: null, out var suffixBytes)
                    || previousPathBytes.Length - removeCount + suffixBytes.Length > MaxDoctorGitIndexPathBytes)
                {
                    return false;
                }

                var prefixLength = previousPathBytes.Length - removeCount;
                pathBytes = new byte[prefixLength + suffixBytes.Length];
                previousPathBytes.AsSpan(0, prefixLength).CopyTo(pathBytes);
                suffixBytes.CopyTo(pathBytes, prefixLength);
            }
            else
            {
                var encodedLength = flags & 0x0fff;
                if (!TryReadGitIndexPath(
                        bytes,
                        contentLength,
                        ref offset,
                        encodedLength == 0x0fff ? null : encodedLength,
                        out pathBytes))
                {
                    return false;
                }

                var padding = (8 - ((offset - entryStart) & 7)) & 7;
                if (offset > contentLength - padding)
                    return false;
                offset += padding;
            }

            var path = StrictGitIndexUtf8.GetString(pathBytes);
            if (path.Any(char.IsControl))
                return false;
            previousPathBytes = pathBytes;
            if ((extendedFlags & 0x4000) != 0)
            {
                var normalizedPath = FileIndexer.NormalizePathSeparators(path);
                if ((mode & 0xf000) == 0x4000 && !normalizedPath.EndsWith("/", StringComparison.Ordinal))
                    normalizedPath += "/";
                paths.Add(normalizedPath);
            }
        }

        return ValidateGitIndexExtensions(bytes, contentLength, offset);
    }

    private static bool ValidateGitIndexExtensions(byte[] bytes, int contentLength, int offset)
    {
        while (offset < contentLength)
        {
            if (offset > contentLength - 8)
                return false;
            var signature = bytes.AsSpan(offset, 4);
            var extensionLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4, 4));
            offset += 8;
            if (extensionLength > int.MaxValue || offset > contentLength - (int)extensionLength)
                return false;

            // A split index stores its base entries in sharedindex.<hash>. Returning the delta's
            // skip flags as complete would turn sparse paths into false deletions. The caller
            // treats this unsupported layout as unavailable evidence.
            if (signature.SequenceEqual("link"u8))
                return false;
            offset += (int)extensionLength;
        }

        return offset == contentLength;
    }

    private static HashSet<string>? RebaseSkipWorktreePaths(
        HashSet<string> repositoryPaths,
        string worktreeRoot,
        string projectRoot)
    {
        var fullProjectRoot = Path.GetFullPath(projectRoot);
        if (!PathCasing.IsPathEqualOrParent(worktreeRoot, fullProjectRoot))
            return null;

        var relativeProjectRoot = FileIndexer.NormalizePathSeparators(
            Path.GetRelativePath(worktreeRoot, fullProjectRoot));
        var projectPrefix = relativeProjectRoot is "." or ""
            ? string.Empty
            : relativeProjectRoot.TrimEnd('/') + "/";
        var rebased = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repositoryPath in repositoryPaths)
        {
            if (projectPrefix.Length > 0
                && !repositoryPath.StartsWith(projectPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var projectPath = projectPrefix.Length == 0
                ? repositoryPath
                : repositoryPath[projectPrefix.Length..];
            if (projectPath.Length == 0 && repositoryPath.EndsWith("/", StringComparison.Ordinal))
                projectPath = "/";
            if (projectPath.Length > 0)
                rebased.Add(projectPath);
        }

        return rebased;
    }

    private static bool TryReadLooseGitReference(
        string gitDirectory,
        string referenceName,
        out string objectId)
    {
        objectId = string.Empty;
        if (!TryResolveGitMetadataPath(gitDirectory, referenceName, out var referencePath)
            || !PathCasing.IsPathEqualOrParent(gitDirectory, referencePath)
            || PathCasing.PathsEqual(gitDirectory, referencePath)
            || !TryValidateGitMetadataEntry(referencePath, expectDirectory: false, out referencePath))
        {
            return false;
        }

        var value = DataDirectorySecurity.ReadTextWithinLimit(
            LongPath.EnsureWindowsPrefix(referencePath),
            MaxGitMetadataFileBytes)?.Trim();
        if (!IsFullGitObjectId(value))
            return false;
        objectId = value!;
        return true;
    }

    private static string? TryReadPackedGitReference(string commonDirectory, string referenceName)
    {
        if (!TryResolveGitMetadataChildPath(
                commonDirectory,
                "packed-refs",
                expectDirectory: false,
                allowMissing: true,
                out var packedRefsPath)
            || !File.Exists(LongPath.EnsureWindowsPrefix(packedRefsPath)))
        {
            return null;
        }

        var content = DataDirectorySecurity.ReadTextWithinLimit(
            LongPath.EnsureWindowsPrefix(packedRefsPath),
            MaxDoctorPackedRefsBytes);
        if (content == null)
            return null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf(' ');
            if (separator <= 0
                || !string.Equals(line[(separator + 1)..], referenceName, StringComparison.Ordinal))
            {
                continue;
            }

            var objectId = line[..separator];
            return IsFullGitObjectId(objectId) ? objectId : null;
        }

        return null;
    }

    private static bool IsSafeGitReferenceName(string value)
        => value.StartsWith("refs/", StringComparison.Ordinal)
           && !value.EndsWith("/", StringComparison.Ordinal)
           && !value.Contains("..", StringComparison.Ordinal)
           && !value.Contains("//", StringComparison.Ordinal)
           && !value.Contains('\\')
           && !value.Any(char.IsControl);

    private static bool IsFullGitObjectId(string? value)
        => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool TryReadGitIndexVarInt(
        byte[] bytes,
        int contentLength,
        ref int offset,
        out int value)
    {
        value = 0;
        if (offset >= contentLength)
            return false;

        var current = bytes[offset++];
        value = current & 0x7f;
        while ((current & 0x80) != 0)
        {
            if (offset >= contentLength || value > MaxDoctorGitIndexPathBytes)
                return false;
            current = bytes[offset++];
            value = checked(((value + 1) << 7) | (current & 0x7f));
        }

        return value <= MaxDoctorGitIndexPathBytes;
    }

    private static bool TryReadGitIndexPath(
        byte[] bytes,
        int contentLength,
        ref int offset,
        int? expectedLength,
        out byte[] pathBytes)
    {
        pathBytes = [];
        var start = offset;
        var length = expectedLength ?? 0;
        if (expectedLength.HasValue)
        {
            if (length > MaxDoctorGitIndexPathBytes
                || offset > contentLength - length - 1
                || bytes[offset + length] != 0)
            {
                return false;
            }
            offset += length + 1;
        }
        else
        {
            while (offset < contentLength
                   && bytes[offset] != 0
                   && offset - start <= MaxDoctorGitIndexPathBytes)
            {
                offset++;
            }
            if (offset >= contentLength
                || bytes[offset] != 0
                || offset - start > MaxDoctorGitIndexPathBytes)
            {
                return false;
            }
            length = offset - start;
            offset++;
        }

        if (length == 0)
            return false;
        pathBytes = bytes.AsSpan(start, length).ToArray();
        return true;
    }
}
