using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class DbCommandRunner
{
    private static DbRestorePreviewResult PreviewRestoreCheckpoint(
        string fullDbPath,
        string name,
        string checkpointPath)
    {
        ValidateCheckpointName(name);
        var diagnostics = new List<DbDiagnosticJsonResult>();
        var pathsValid = TryValidateCheckpointDirectoryTarget(
            fullDbPath,
            checkpointPath,
            out var validatedCheckpointPath,
            out var checkpointPathFailure);
        if (!pathsValid)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_path_invalid",
                $"Checkpoint directory failed path validation: {checkpointPathFailure}.",
                ConsoleUi.FormatBoundedValue(checkpointPath)));
        }

        var manifestValid = false;
        var payload = new DbCheckpointPayloadValidationResult(
            PathsValid: false,
            Files: [],
            Bytes: 0);
        if (pathsValid)
        {
            manifestValid = TryValidateCheckpointManifest(
                fullDbPath,
                name,
                validatedCheckpointPath,
                diagnostics,
                out _);
            payload = ValidateCheckpointPayload(
                fullDbPath,
                validatedCheckpointPath,
                diagnostics);
            pathsValid = payload.PathsValid;
        }

        var availableSpace = TryGetAvailableFreeSpace(fullDbPath, diagnostics);
        bool? spaceSufficient = availableSpace is long available ? available >= payload.Bytes : null;
        if (spaceSufficient == false)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_space_insufficient",
                "The destination filesystem does not have enough free space to stage the checkpoint payload.",
                ConsoleUi.FormatBoundedValue(Path.GetDirectoryName(fullDbPath) ?? fullDbPath)));
        }

        var ready = manifestValid && pathsValid && spaceSufficient == true;
        return new DbRestorePreviewResult(
            ready,
            manifestValid,
            pathsValid,
            availableSpace.HasValue,
            spaceSufficient,
            payload.Bytes,
            availableSpace,
            payload.Files,
            payload.Bytes,
            diagnostics);
    }

    private static bool TryGetCheckpointRetentionTimestamp(
        string fullDbPath,
        string name,
        string checkpointPath,
        List<DbDiagnosticJsonResult> diagnostics,
        out DateTimeOffset createdAtUtc)
    {
        createdAtUtc = default;
        if (!TryValidateCheckpointDirectoryTarget(
                fullDbPath,
                checkpointPath,
                out var validatedCheckpointPath,
                out var checkpointPathFailure))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_retention_invalid",
                $"Checkpoint cannot occupy a retention slot because its directory is unsafe: {checkpointPathFailure}.",
                ConsoleUi.FormatBoundedValue(checkpointPath)));
            return false;
        }

        var manifestValid = TryValidateCheckpointManifest(
            fullDbPath,
            name,
            validatedCheckpointPath,
            diagnostics,
            out createdAtUtc);
        var payload = ValidateCheckpointPayload(
            fullDbPath,
            validatedCheckpointPath,
            diagnostics);
        if (manifestValid && payload.PathsValid)
            return true;

        diagnostics.Add(new DbDiagnosticJsonResult(
            "checkpoint_retention_invalid",
            "Checkpoint cannot occupy a retention slot because restore validation failed.",
            ConsoleUi.FormatBoundedValue(checkpointPath)));
        return false;
    }

    private static DbCheckpointPayloadValidationResult ValidateCheckpointPayload(
        string fullDbPath,
        string checkpointPath,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        var pathsValid = true;
        var files = new List<string>();
        long bytes = 0;
        var dbFileName = Path.GetFileName(fullDbPath);
        foreach (var fileName in new[] { dbFileName, dbFileName + "-wal", dbFileName + "-shm" })
        {
            var path = Path.Combine(checkpointPath, fileName);
            try
            {
                if (!TryGetRegularExistingFile(path, out var normalizedPath))
                {
                    if (string.Equals(fileName, dbFileName, StringComparison.Ordinal))
                    {
                        pathsValid = false;
                        diagnostics.Add(new DbDiagnosticJsonResult(
                            "checkpoint_payload_missing",
                            "Checkpoint database payload is missing.",
                            ConsoleUi.FormatBoundedValue(path)));
                    }

                    continue;
                }

                files.Add(fileName);
                bytes = checked(bytes + new FileInfo(normalizedPath).Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException or OverflowException)
            {
                pathsValid = false;
                diagnostics.Add(new DbDiagnosticJsonResult(
                    "checkpoint_payload_invalid",
                    $"Checkpoint payload failed regular-file validation ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                    ConsoleUi.FormatBoundedValue(path)));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return new DbCheckpointPayloadValidationResult(pathsValid, files, bytes);
    }

    private static bool TryValidateCheckpointManifest(
        string fullDbPath,
        string name,
        string checkpointPath,
        List<DbDiagnosticJsonResult> diagnostics,
        out DateTimeOffset createdAtUtc)
    {
        createdAtUtc = default;
        var manifestPath = Path.Combine(checkpointPath, "manifest.txt");
        try
        {
            if (!TryGetRegularExistingFile(manifestPath, out var normalizedManifestPath))
            {
                diagnostics.Add(new DbDiagnosticJsonResult(
                    "checkpoint_manifest_missing",
                    "Checkpoint manifest is missing.",
                    ConsoleUi.FormatBoundedValue(manifestPath)));
                return false;
            }

            var length = new FileInfo(normalizedManifestPath).Length;
            if (length > CheckpointManifestByteLimit)
            {
                diagnostics.Add(new DbDiagnosticJsonResult(
                    "checkpoint_manifest_too_large",
                    $"Checkpoint manifest exceeds the {CheckpointManifestByteLimit:N0}-byte validation limit.",
                    ConsoleUi.FormatBoundedValue(manifestPath)));
                return false;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = new StringReader(File.ReadAllText(normalizedManifestPath));
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                    continue;
                var separator = line.IndexOf('=');
                if (separator <= 0 || !values.TryAdd(line[..separator], line[(separator + 1)..]))
                {
                    diagnostics.Add(new DbDiagnosticJsonResult(
                        "checkpoint_manifest_invalid",
                        "Checkpoint manifest contains a malformed or duplicate field.",
                        ConsoleUi.FormatBoundedValue(manifestPath)));
                    return false;
                }
            }

            var expectedDbFile = Path.GetFileName(fullDbPath);
            var valid = values.TryGetValue("name", out var manifestName)
                && string.Equals(manifestName, name, StringComparison.Ordinal)
                && values.TryGetValue("db_file", out var manifestDbFile)
                && string.Equals(manifestDbFile, expectedDbFile, StringComparison.Ordinal)
                && string.Equals(Path.GetFileName(manifestDbFile), manifestDbFile, StringComparison.Ordinal)
                && values.TryGetValue("created_at_utc", out var createdAt)
                && DateTimeOffset.TryParse(
                    createdAt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out createdAtUtc);
            if (valid)
                return true;

            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_manifest_invalid",
                "Checkpoint manifest name, database file, or UTC timestamp does not match the requested restore.",
                ConsoleUi.FormatBoundedValue(manifestPath)));
            return false;
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex) || ex is InvalidOperationException)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_manifest_invalid",
                $"Checkpoint manifest could not be validated ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(manifestPath)));
            return false;
        }
    }

    private static long? TryGetAvailableFreeSpace(
        string fullDbPath,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        try
        {
            var destinationDirectory = Path.GetDirectoryName(fullDbPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                throw new IOException("destination filesystem directory is unavailable");
            var resolvedDestinationDirectory = ResolveDestinationDirectoryForSpaceProbe(destinationDirectory);

            if (AvailableFreeSpaceForTesting is not null)
                return AvailableFreeSpaceForTesting(resolvedDestinationDirectory);

            if (OperatingSystem.IsWindows())
            {
                if (!GetDiskFreeSpaceEx(
                        resolvedDestinationDirectory,
                        out var availableBytes,
                        out _,
                        out _))
                {
                    throw new IOException(
                        "destination filesystem volume is unavailable",
                        new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
                }

                return availableBytes > long.MaxValue
                    ? long.MaxValue
                    : (long)availableBytes;
            }

            DriveInfo? destinationDrive = null;
            var destinationRootLength = -1;
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                        continue;
                    var driveRoot = drive.RootDirectory.FullName;
                    if (driveRoot.Length <= destinationRootLength
                        || !IsPathWithinDriveRoot(driveRoot, resolvedDestinationDirectory))
                    {
                        continue;
                    }

                    destinationDrive = drive;
                    destinationRootLength = driveRoot.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Ignore an unreadable unrelated mount and keep looking for the
                    // longest ready mount that contains the destination directory.
                }
            }

            if (destinationDrive is null)
                throw new IOException("destination filesystem volume is unavailable");
            return destinationDrive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_space_unavailable",
                $"Available destination space could not be determined ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(Path.GetDirectoryName(fullDbPath) ?? fullDbPath)));
            return null;
        }
    }

    private static bool IsPathWithinDriveRoot(string driveRoot, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(driveRoot));
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(normalizedRoot, normalizedPath, comparison))
            return true;

        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSeparator, comparison);
    }

    private static string ResolveDestinationDirectoryForSpaceProbe(string destinationDirectory)
    {
        var fullPath = Path.GetFullPath(destinationDirectory);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            throw new IOException("destination filesystem root is unavailable");

        var current = root;
        var relativePath = fullPath[root.Length..];
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = target.FullName;
        }

        return Path.GetFullPath(current);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    private static string RestoreCheckpoint(string fullDbPath, string name, string checkpointPath)
    {
        ValidateCheckpointName(name);
        if (!TryValidateCheckpointDirectoryTarget(
                fullDbPath,
                checkpointPath,
                out checkpointPath,
                out var checkpointPathFailure))
        {
            throw new InvalidOperationException(
                $"checkpoint path validation failed: {checkpointPathFailure}");
        }

        SqliteConnection.ClearAllPools();
        var checkpointDbPath = Path.Combine(checkpointPath, Path.GetFileName(fullDbPath));
        if (!File.Exists(LongPath.EnsureWindowsPrefix(checkpointDbPath)))
            throw new InvalidOperationException($"checkpoint is incomplete: {FormatCheckpointNameForDiagnostic(name)}");

        var restorePathSuffix = MakeRestorePathSuffix();
        var restoreTempPath = fullDbPath + ".restore-tmp-" + restorePathSuffix;
        var backupPath = fullDbPath + ".restore-backup-" + restorePathSuffix;
        DataDirectorySecurity.CreateSensitiveDirectory(restoreTempPath);
        try
        {
            CopyIfExists(checkpointDbPath, Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath)), privateDestination: true);
            CopyIfExists(Path.Combine(checkpointPath, Path.GetFileName(fullDbPath) + "-wal"), Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath) + "-wal"), privateDestination: true);
            CopyIfExists(Path.Combine(checkpointPath, Path.GetFileName(fullDbPath) + "-shm"), Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath) + "-shm"), privateDestination: true);
            if (!File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath)))))
                throw new InvalidOperationException($"checkpoint staging failed: {FormatCheckpointNameForDiagnostic(name)}");

            DataDirectorySecurity.CreateSensitiveDirectory(backupPath);
            MoveIfExists(fullDbPath, Path.Combine(backupPath, Path.GetFileName(fullDbPath)), privateDestination: true);
            MoveIfExists(fullDbPath + "-wal", Path.Combine(backupPath, Path.GetFileName(fullDbPath) + "-wal"), privateDestination: true);
            MoveIfExists(fullDbPath + "-shm", Path.Combine(backupPath, Path.GetFileName(fullDbPath) + "-shm"), privateDestination: true);

            RestoreFailureAfterBackupForTesting?.Invoke();

            MoveIfExists(Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath)), fullDbPath, privateDestination: true);
            MoveIfExists(Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath) + "-wal"), fullDbPath + "-wal", privateDestination: true);
            MoveIfExists(Path.Combine(restoreTempPath, Path.GetFileName(fullDbPath) + "-shm"), fullDbPath + "-shm", privateDestination: true);
        }
        catch (Exception primaryEx)
        {
            DbDiagnosticJsonResult? rollbackFailure = null;
            try
            {
                RestoreBackedUpFiles(fullDbPath, backupPath);
            }
            catch (Exception rollbackEx) when (IsRecoverableRestoreException(rollbackEx))
            {
                rollbackFailure = new DbDiagnosticJsonResult(
                    "restore_rollback_failed",
                    $"Failed to roll back database restore from backup ({CommandErrorWriter.FormatSanitizedException(rollbackEx)}).",
                    ConsoleUi.FormatBoundedValue(backupPath));
                CommandErrorWriter.WriteStderr($"Warning [{rollbackFailure.Code}]: {rollbackFailure.Message} Backup: {rollbackFailure.Path}");
            }

            throw new DbRestoreOperationException(primaryEx, checkpointPath, backupPath, rollbackFailure);
        }
        finally
        {
            TryDeleteTemporaryDirectory(
                restoreTempPath,
                "restore temporary directory",
                Path.GetDirectoryName(fullDbPath) ?? Path.GetPathRoot(fullDbPath) ?? Path.GetFullPath("."),
                Path.GetFileName(fullDbPath) + ".restore-tmp-");
        }

        return backupPath;
    }
}
