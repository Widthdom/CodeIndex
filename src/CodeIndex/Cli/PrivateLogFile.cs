using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class PrivateLogFile
{
    internal const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal const int MaxExistingFilesToHarden = 128;
    private const int MaxDiagnosticTargetChars = 160;

    internal static Stream OpenAppend(
        string path,
        FileShare share = FileShare.ReadWrite,
        RepositoryOutputPathGuard? boundary = null)
    {
        boundary?.PrepareMutation("open_append", path);
        RejectUnsafeTarget(path);

        Stream stream;
        if (boundary is not null && !OperatingSystem.IsWindows())
        {
            stream = boundary.OpenAppendUnix(path);
        }
        else if (OperatingSystem.IsWindows())
        {
            stream = new FileStream(path, FileMode.Append, FileAccess.Write, share);
        }
        else
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = share,
                UnixCreateMode = PrivateFileMode,
            });
        }

        try
        {
            boundary?.CompleteMutation(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static StreamWriter OpenAppendText(string path, RepositoryOutputPathGuard? boundary = null)
        => new(OpenAppend(path, boundary: boundary), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

    internal static void TrySetPrivatePermissions(
        string path,
        Action<PrivateLogFileDiagnostic>? diagnosticSink = null,
        RepositoryOutputPathGuard? boundary = null)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            boundary?.PrepareMutation("set_private_permissions", path);
            RejectUnsafeTarget(path);
            if (boundary is null)
                File.SetUnixFileMode(path, PrivateFileMode);
            else
                boundary.SetPrivateFileModeUnix(path);
            boundary?.CompleteMutation(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ReportDiagnostic(diagnosticSink, "set_private_permissions", path, ex);
        }
    }

    internal static void HardenExisting(
        string directory,
        string pattern,
        Action<PrivateLogFileDiagnostic>? diagnosticSink = null,
        RepositoryOutputPathGuard? boundary = null)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var hardened = 0;
            foreach (var file in SelectFirstByName(
                new DirectoryInfo(directory).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly),
                MaxExistingFilesToHarden + 1))
            {
                if (hardened >= MaxExistingFilesToHarden)
                {
                    ReportDiagnostic(diagnosticSink, "harden_existing_cap", "cap_exceeded", directory);
                    break;
                }

                TrySetPrivatePermissions(file.FullName, diagnosticSink, boundary);
                hardened++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportDiagnostic(diagnosticSink, "harden_existing", directory, ex);
        }
    }

    private static IReadOnlyList<FileInfo> SelectFirstByName(IEnumerable<FileInfo> files, int retainedFileCount)
    {
        if (retainedFileCount <= 0)
            return [];

        var retained = new List<FileInfo>(retainedFileCount);
        foreach (var file in files)
            AddFirstByName(retained, file, retainedFileCount);

        return retained;
    }

    private static void AddFirstByName(List<FileInfo> retained, FileInfo file, int retainedFileCount)
    {
        var insertAt = retained.FindIndex(existing => CompareHardenOrder(file, existing) < 0);
        if (insertAt < 0)
        {
            if (retained.Count < retainedFileCount)
                retained.Add(file);
            return;
        }

        retained.Insert(insertAt, file);
        if (retained.Count > retainedFileCount)
            retained.RemoveAt(retained.Count - 1);
    }

    private static int CompareHardenOrder(FileInfo left, FileInfo right)
    {
        var name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        return name != 0
            ? name
            : string.Compare(left.FullName, right.FullName, StringComparison.Ordinal);
    }

    internal static void PruneOldFiles(
        string directory,
        string pattern,
        int retainedFileCount,
        Action<PrivateLogFileDiagnostic>? diagnosticSink = null,
        Action<string>? deleteOverride = null,
        RepositoryOutputPathGuard? boundary = null)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(directory);
            var retainedFiles = SelectRetainedFiles(
                directoryInfo.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly),
                retainedFileCount);
            var retainedPaths = new HashSet<string>(
                retainedFiles.Select(file => file.FullName),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var file in directoryInfo.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
            {
                if (ShouldPruneFile(file, retainedPaths, retainedFiles, retainedFileCount))
                {
                    boundary?.PrepareMutation("prune_old_file", file.FullName);
                    if (boundary is not null && !OperatingSystem.IsWindows())
                    {
                        boundary.DeleteFileUnix(file.FullName);
                    }
                    else
                    {
                        AtomicFileWriter.TryDeleteFile(
                            file.FullName,
                            ex => ReportDiagnostic(diagnosticSink, "prune_old_file_delete", file.FullName, ex),
                            deleteOverride);
                    }
                    boundary?.CompleteMutation(file.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportDiagnostic(diagnosticSink, "prune_old_files", directory, ex);
        }
    }

    private static IReadOnlyList<FileInfo> SelectRetainedFiles(IEnumerable<FileInfo> files, int retainedFileCount)
    {
        if (retainedFileCount <= 0)
            return [];

        var retained = new List<FileInfo>(retainedFileCount);
        foreach (var file in files)
            AddRetainedFile(retained, file, retainedFileCount);

        return retained;
    }

    private static void AddRetainedFile(List<FileInfo> retained, FileInfo file, int retainedFileCount)
    {
        var insertAt = retained.FindIndex(existing => CompareRetentionOrder(file, existing) > 0);
        if (insertAt < 0)
        {
            if (retained.Count < retainedFileCount)
                retained.Add(file);
            return;
        }

        retained.Insert(insertAt, file);
        if (retained.Count > retainedFileCount)
            retained.RemoveAt(retained.Count - 1);
    }

    private static bool ShouldPruneFile(
        FileInfo file,
        HashSet<string> retainedPaths,
        IReadOnlyList<FileInfo> retainedFiles,
        int retainedFileCount)
    {
        if (retainedPaths.Contains(file.FullName))
            return false;
        if (retainedFileCount <= 0)
            return true;
        if (retainedFiles.Count < retainedFileCount)
            return false;

        return CompareRetentionOrder(file, retainedFiles[^1]) < 0;
    }

    private static int CompareRetentionOrder(FileInfo left, FileInfo right)
    {
        var modified = left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc);
        if (modified != 0)
            return modified;

        return string.Compare(left.Name, right.Name, StringComparison.Ordinal);
    }

    internal static bool TryRotateSlots(
        string path,
        int retainedFileCount,
        Action<string>? afterMove = null,
        Action<Exception>? onFailure = null,
        Action<Exception>? onCleanupFailure = null,
        RepositoryOutputPathGuard? boundary = null)
    {
        try
        {
            var lastSlot = SlotPath(path, retainedFileCount - 1);
            boundary?.PrepareMutation("rotate_delete", lastSlot);
            if (boundary is not null && !OperatingSystem.IsWindows())
                boundary.DeleteFileUnix(lastSlot);
            else
                AtomicFileWriter.TryDeleteFile(lastSlot, onCleanupFailure);
            boundary?.CompleteMutation(lastSlot);

            for (var slot = retainedFileCount - 2; slot >= 1; slot--)
            {
                var current = SlotPath(path, slot);
                var next = SlotPath(path, slot + 1);
                if (!File.Exists(LongPath.EnsureWindowsPrefix(current)))
                    continue;
                boundary?.PrepareMutation("rotate_source", current);
                boundary?.PrepareMutation("rotate_destination", next);
                if (boundary is not null && !OperatingSystem.IsWindows())
                    boundary.MoveReplacingUnix(current, next);
                else
                    AtomicFileWriter.MoveReplacing(current, next);
                boundary?.CompleteMutation(current);
                boundary?.CompleteMutation(next);
                afterMove?.Invoke(next);
            }

            if (File.Exists(LongPath.EnsureWindowsPrefix(path)))
            {
                var first = SlotPath(path, 1);
                boundary?.PrepareMutation("rotate_source", path);
                boundary?.PrepareMutation("rotate_destination", first);
                if (boundary is not null && !OperatingSystem.IsWindows())
                    boundary.MoveReplacingUnix(path, first);
                else
                    AtomicFileWriter.MoveReplacing(path, first);
                boundary?.CompleteMutation(path);
                boundary?.CompleteMutation(first);
                afterMove?.Invoke(first);
            }

            return true;
        }
        catch (Exception ex)
        {
            ReportFailure(onFailure, ex);
            return false;
        }
    }

    private static string SlotPath(string path, int slot)
        => slot <= 0 ? path : path + "." + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void ReportFailure(Action<Exception>? failureSink, Exception exception)
    {
        if (failureSink is null)
            return;

        try
        {
            failureSink(exception);
        }
        catch
        {
            // Failure reporting must not make best-effort rotation fail harder.
        }
    }

    private static void ReportDiagnostic(
        Action<PrivateLogFileDiagnostic>? diagnosticSink,
        string operation,
        string path,
        Exception exception)
        => ReportDiagnostic(diagnosticSink, operation, ClassifyFailure(exception), path);

    private static void ReportDiagnostic(
        Action<PrivateLogFileDiagnostic>? diagnosticSink,
        string operation,
        string reason,
        string path)
    {
        if (diagnosticSink is null)
            return;

        try
        {
            diagnosticSink(new PrivateLogFileDiagnostic(
                operation,
                reason,
                FormatDiagnosticTarget(path)));
        }
        catch
        {
            // Diagnostics must not make best-effort log hardening fail.
        }
    }

    private static void RejectUnsafeTarget(string path)
    {
        try
        {
            var attributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(path));
            if (FileSystemBoundary.IsSymlinkOrReparsePoint(attributes))
                throw new IOException("Refusing to use private log target because it is a symbolic link or reparse point.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    private static string ClassifyFailure(Exception exception)
        => FileSystemBoundary.ClassifyProbeFailure(exception);

    private static string FormatDiagnosticTarget(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(target))
                target = "<target>";
            return target.Length <= MaxDiagnosticTargetChars
                ? target
                : target[..MaxDiagnosticTargetChars] + "...";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return "<invalid>";
        }
    }
}

internal sealed record PrivateLogFileDiagnostic(string Operation, string Reason, string Target);
