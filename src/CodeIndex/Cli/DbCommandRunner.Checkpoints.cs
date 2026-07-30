using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class DbCommandRunner
{
    private static DbCheckpointPlan PlanCheckpoint(string fullDbPath, string name)
    {
        ValidateCheckpointName(name);
        var root = GetCheckpointRoot(fullDbPath);
        var checkpointPath = GetCheckpointPath(fullDbPath, name);
        var diagnostics = new List<DbDiagnosticJsonResult>();
        var destinationStatus = FileSystemBoundary.TryGetAttributes(checkpointPath, out _);
        var destinationExists = destinationStatus == FileSystemBoundaryProbeStatus.Found;
        var destinationReady = destinationStatus == FileSystemBoundaryProbeStatus.Missing;
        if (destinationExists)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_already_exists",
                "A checkpoint with this name already exists; execution would fail.",
                ConsoleUi.FormatBoundedValue(checkpointPath)));
        }
        else if (!destinationReady)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_destination_probe_failed",
                "The checkpoint destination could not be inspected; execution would fail.",
                ConsoleUi.FormatBoundedValue(checkpointPath)));
        }

        var sourceCandidatePaths = new[] { fullDbPath, fullDbPath + "-wal", fullDbPath + "-shm" };
        var sourceFiles = ReadCheckpointSourceFiles(sourceCandidatePaths, diagnostics);
        var outputNameComparer = StringComparer.FromComparison(PathCasing.ComparisonFor(checkpointPath));
        var outputNameConflict = sourceFiles.Files.Any(
            source => outputNameComparer.Equals(source.OutputName, CheckpointManifestFileName));
        if (outputNameConflict)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_output_name_conflict",
                $"A checkpoint source file conflicts with the generated {CheckpointManifestFileName}; execution would overwrite a planned output and is refused.",
                CheckpointManifestFileName));
        }

        var ready = destinationReady && !sourceFiles.Truncated && !outputNameConflict;
        var manifestContents = $"format_version=1{Environment.NewLine}name={name}{Environment.NewLine}created_at_utc={GetUtcNow():O}{Environment.NewLine}db_file={Path.GetFileName(fullDbPath)}{Environment.NewLine}";
        var manifestBytes = Encoding.UTF8.GetBytes(manifestContents);
        var plannedOutputFiles = sourceFiles.Files
            .Select(source => source.OutputName)
            .Append(CheckpointManifestFileName)
            .Distinct(outputNameComparer)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new DbCheckpointPlan(
            name,
            root,
            checkpointPath,
            Array.AsReadOnly(sourceCandidatePaths),
            sourceFiles.Files.AsReadOnly(),
            plannedOutputFiles.AsReadOnly(),
            sourceFiles.Bytes,
            checked(sourceFiles.Bytes + manifestBytes.LongLength),
            manifestContents,
            ComputeCheckpointSha256(manifestBytes),
            CheckpointDestinationPolicy,
            CheckpointConflictPolicy,
            CheckpointPlanUncertainty,
            CheckpointManifestSchema,
            CheckpointSidecarPolicy,
            CheckpointCompressionPolicy,
            OperatingSystem.IsWindows()
                ? CheckpointWindowsMetadataPolicy
                : CheckpointPosixMetadataPolicy,
            ready,
            destinationExists,
            sourceFiles.Truncated,
            diagnostics.AsReadOnly());
    }

    private static DbCheckpointOperationResult CreateCheckpoint(DbCheckpointPlan plan)
    {
        if (!plan.Ready)
            throw new InvalidOperationException($"checkpoint plan is not ready: {FormatCheckpointNameForDiagnostic(plan.Name)}");

        CheckpointPlanReadyForExecutionForTesting?.Invoke();
        ValidateCheckpointPlanSources(plan);

        DataDirectorySecurity.CreateSensitiveDirectory(plan.RootPath);
        var tempPath = Path.Combine(plan.RootPath, ".tmp-" + plan.Name + "-" + Guid.NewGuid().ToString("N"));
        DataDirectorySecurity.CreateSensitiveDirectory(tempPath);
        try
        {
            foreach (var source in plan.SourceFiles)
            {
                var destination = Path.Combine(tempPath, source.OutputName);
                CopyIfExists(source.SourcePath, destination, privateDestination: true);
                ValidateCheckpointOutput(destination, source);
            }

            var manifestPath = Path.Combine(tempPath, CheckpointManifestFileName);
            DataDirectorySecurity.WritePrivateText(manifestPath, plan.ManifestContents);
            ValidateCheckpointOutput(
                manifestPath,
                new DbCheckpointSourcePlan(
                    manifestPath,
                    CheckpointManifestFileName,
                    Encoding.UTF8.GetByteCount(plan.ManifestContents),
                    LastWriteTimeUtcTicks: null,
                    plan.ManifestSha256));
            ValidateCheckpointPlanSources(plan);
            AtomicFileWriter.PublishDirectory(tempPath, plan.CheckpointPath);
        }
        catch
        {
            TryDeleteTemporaryDirectory(
                tempPath,
                "checkpoint temporary directory",
                plan.RootPath,
                ".tmp-");
            throw;
        }

        var diagnostics = new List<DbDiagnosticJsonResult>(plan.Diagnostics);
        var files = EnumerateCheckpointFileNames(plan.CheckpointPath, diagnostics);
        var bytes = files.Truncated
            ? (Bytes: 0L, Truncated: true)
            : SumCheckpointBytes(plan.CheckpointPath, diagnostics);
        return new DbCheckpointOperationResult(plan.Name, plan.CheckpointPath, files.Items, files.Truncated || bytes.Truncated, diagnostics, bytes.Bytes);
    }

    private static (List<DbCheckpointSourcePlan> Files, long Bytes, bool Truncated) ReadCheckpointSourceFiles(
        IReadOnlyList<string> sourceCandidatePaths,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        var files = new List<DbCheckpointSourcePlan>();
        long bytes = 0;
        foreach (var source in sourceCandidatePaths)
        {
            try
            {
                if (!TryGetRegularExistingFile(source, out var normalizedSource))
                    continue;

                var sourcePlan = CaptureCheckpointSource(normalizedSource, Path.GetFileName(source) ?? source);
                files.Add(sourcePlan);
                bytes = checked(bytes + sourcePlan.Bytes);
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                diagnostics.Add(CreateCheckpointDiagnostic(
                    "checkpoint_source_file_stat_failed",
                    $"Unable to inspect checkpoint source file ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                    source));
                return (files, bytes, Truncated: true);
            }
        }

        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.OutputName, right.OutputName));
        return (files, bytes, Truncated: false);
    }

    private static DbCheckpointSourcePlan CaptureCheckpointSource(string sourcePath, string outputName)
    {
        var fileInfo = new FileInfo(sourcePath);
        fileInfo.Refresh();
        var length = fileInfo.Length;
        var lastWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
        var sha256 = ComputeCheckpointSha256(sourcePath);
        fileInfo.Refresh();
        if (length != fileInfo.Length || lastWriteTimeUtcTicks != fileInfo.LastWriteTimeUtc.Ticks)
            throw new DbCheckpointPlanDriftException();

        return new DbCheckpointSourcePlan(sourcePath, outputName, length, lastWriteTimeUtcTicks, sha256);
    }

    private static void ValidateCheckpointPlanSources(DbCheckpointPlan plan)
    {
        foreach (var sourceCandidatePath in plan.SourceCandidatePaths)
        {
            try
            {
                var outputName = Path.GetFileName(sourceCandidatePath) ?? sourceCandidatePath;
                var expected = plan.SourceFiles.SingleOrDefault(
                    source => string.Equals(source.OutputName, outputName, StringComparison.Ordinal));
                var exists = TryGetRegularExistingFile(sourceCandidatePath, out var normalizedSource);
                if (exists != (expected is not null))
                    throw new DbCheckpointPlanDriftException();
                if (expected is null)
                    continue;

                var current = CaptureCheckpointSource(normalizedSource, expected.OutputName);
                if (current.Bytes != expected.Bytes
                    || current.LastWriteTimeUtcTicks != expected.LastWriteTimeUtcTicks
                    || !string.Equals(current.Sha256, expected.Sha256, StringComparison.Ordinal))
                {
                    throw new DbCheckpointPlanDriftException();
                }
            }
            catch (DbCheckpointPlanDriftException)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                throw new DbCheckpointPlanDriftException(ex);
            }
        }
    }

    private static void ValidateCheckpointOutput(string destinationPath, DbCheckpointSourcePlan expected)
    {
        try
        {
            if (!TryGetRegularExistingFile(destinationPath, out var normalizedDestination))
                throw new DbCheckpointPlanDriftException();
            var fileInfo = new FileInfo(normalizedDestination);
            if (fileInfo.Length != expected.Bytes
                || !string.Equals(ComputeCheckpointSha256(normalizedDestination), expected.Sha256, StringComparison.Ordinal))
            {
                throw new DbCheckpointPlanDriftException();
            }
        }
        catch (DbCheckpointPlanDriftException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            throw new DbCheckpointPlanDriftException(ex);
        }
    }

    private static string ComputeCheckpointSha256(string path)
    {
        using var stream = new FileStream(
            LongPath.EnsureWindowsPrefix(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeCheckpointSha256(byte[] contents)
        => Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();

    private static DbCheckpointListReadResult ListCheckpoints(string fullDbPath, int limit)
    {
        var root = GetCheckpointRoot(fullDbPath);
        var diagnostics = new List<DbDiagnosticJsonResult>();
        if (!Directory.Exists(root))
            return new DbCheckpointListReadResult([], DirectoryEnumerationTruncated: false, FileInspectionTruncated: false, diagnostics);

        var dbFileName = Path.GetFileName(fullDbPath);
        var entries = new List<DbCheckpointListEntryJsonResult>();
        var checkpointsTruncated = false;
        var directoriesInspected = 0;
        var directories = EnumerateCheckpointDirectories(root, diagnostics, limit + 1);
        checkpointsTruncated |= directories.Truncated;
        foreach (var path in directories.Items)
        {
            if (directoriesInspected >= limit)
            {
                checkpointsTruncated = true;
                break;
            }

            directoriesInspected++;
            if (Path.GetFileName(path).StartsWith(".tmp-", StringComparison.Ordinal))
                continue;
            if (!File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(path, dbFileName))))
                continue;

            DirectoryInfo info;
            DateTime createdAtUtc;
            try
            {
                info = new DirectoryInfo(path);
                createdAtUtc = info.CreationTimeUtc;
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                diagnostics.Add(CreateCheckpointDiagnostic("checkpoint_directory_stat_failed", "Unable to inspect checkpoint directory metadata.", path));
                checkpointsTruncated = true;
                continue;
            }

            var bytes = SumCheckpointBytes(path, diagnostics);
            entries.Add(new DbCheckpointListEntryJsonResult(
                info.Name,
                path,
                createdAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                bytes.Bytes,
                bytes.Truncated));
        }

        entries.Sort((left, right) =>
        {
            var createdCompare = string.Compare(right.CreatedAtUtc, left.CreatedAtUtc, StringComparison.Ordinal);
            return createdCompare != 0
                ? createdCompare
                : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });
        return new DbCheckpointListReadResult(
            entries,
            checkpointsTruncated,
            entries.Any(entry => entry.FilesTruncated),
            diagnostics);
    }

    private static DbRestoreBackupReadResult ListRestoreBackups(string fullDbPath, int limit)
    {
        var parent = Path.GetDirectoryName(fullDbPath) ?? Path.GetPathRoot(fullDbPath) ?? Path.GetFullPath(".");
        var diagnostics = new List<DbDiagnosticJsonResult>();
        if (!Directory.Exists(parent))
            return new DbRestoreBackupReadResult([], DirectoryEnumerationTruncated: false, FileInspectionTruncated: false, diagnostics);

        var dbFileName = Path.GetFileName(fullDbPath);
        var prefix = GetRestoreBackupDirectoryPrefix(fullDbPath);
        var entries = new List<DbRestoreBackupEntryJsonResult>();
        var backupsTruncated = false;
        var directoriesInspected = 0;
        var directories = EnumerateRestoreBackupDirectories(parent, prefix, diagnostics, limit + 1);
        backupsTruncated |= directories.Truncated;
        foreach (var path in directories.Items)
        {
            if (directoriesInspected >= limit)
            {
                backupsTruncated = true;
                break;
            }

            directoriesInspected++;
            if (!File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(path, dbFileName))))
                continue;

            DirectoryInfo info;
            DateTime createdAtUtc;
            try
            {
                info = new DirectoryInfo(path);
                createdAtUtc = info.CreationTimeUtc;
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                diagnostics.Add(CreateCheckpointDiagnostic("restore_backup_directory_stat_failed", "Unable to inspect restore backup directory metadata.", path));
                backupsTruncated = true;
                continue;
            }

            var bytes = SumCheckpointBytes(path, diagnostics);
            var managed = ManagedRestoreBackupStore.TryReadSummary(
                fullDbPath,
                path,
                out var summary);
            entries.Add(new DbRestoreBackupEntryJsonResult(
                info.Name,
                path,
                managed
                    ? summary.CreatedAtUtc
                    : createdAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                bytes.Bytes,
                bytes.Truncated,
                managed ? summary.Id : null,
                managed,
                managed ? summary.Provenance : null,
                managed ? summary.SourceId : null,
                managed ? summary.UserVersion : null));
        }

        entries.Sort((left, right) =>
        {
            var createdCompare = string.Compare(right.CreatedAtUtc, left.CreatedAtUtc, StringComparison.Ordinal);
            return createdCompare != 0
                ? createdCompare
                : string.Compare(right.Name, left.Name, StringComparison.Ordinal);
        });
        return new DbRestoreBackupReadResult(entries, backupsTruncated, entries.Any(entry => entry.FilesTruncated), diagnostics);
    }

    private static DbRestoreBackupPruneResult PruneRestoreBackups(string fullDbPath, int keep, bool dryRun)
    {
        var result = ListRestoreBackups(fullDbPath, RestoreBackupPruneScanLimit);
        var diagnostics = result.Diagnostics;
        if (result.DirectoryEnumerationTruncated)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_prune_truncated",
                "Restore backup pruning was skipped because backup enumeration reached the scan limit.",
                ConsoleUi.FormatBoundedValue(fullDbPath)));
            return new DbRestoreBackupPruneResult(
                Deleted: 0,
                Retained: result.Entries.Count,
                DeletedPaths: [],
                RetainedPaths: result.Entries.Select(entry => entry.BackupPath).ToList(),
                Truncated: true,
                diagnostics);
        }

        var retainedPaths = result.Entries
            .Take(keep)
            .Select(entry => entry.BackupPath)
            .ToList();
        var deletedPaths = new List<string>();
        var parent = Path.GetDirectoryName(fullDbPath) ?? Path.GetPathRoot(fullDbPath) ?? Path.GetFullPath(".");
        var prefix = GetRestoreBackupDirectoryPrefix(fullDbPath);
        foreach (var entry in result.Entries.Skip(keep))
        {
            if (dryRun)
            {
                if (TryValidateTemporaryDirectoryCleanupTarget(
                        entry.BackupPath,
                        parent,
                        prefix,
                        out _,
                        out var validationFailure,
                        filesystemAwarePrefix: true))
                    deletedPaths.Add(entry.BackupPath);
                else
                {
                    diagnostics.Add(new DbDiagnosticJsonResult(
                        "restore_backup_delete_skipped",
                        $"Restore backup deletion would be skipped: {validationFailure}.",
                        ConsoleUi.FormatBoundedValue(entry.BackupPath)));
                    retainedPaths.Add(entry.BackupPath);
                }
            }
            else if (TryDeleteRestoreBackupDirectory(fullDbPath, entry.BackupPath, diagnostics))
            {
                deletedPaths.Add(entry.BackupPath);
            }
            else
            {
                retainedPaths.Add(entry.BackupPath);
            }
        }

        return new DbRestoreBackupPruneResult(
            deletedPaths.Count,
            retainedPaths.Count,
            deletedPaths,
            retainedPaths,
            result.Truncated,
            diagnostics);
    }

    private static (List<string> Items, bool Truncated) EnumerateRestoreBackupDirectories(
        string parent,
        string prefix,
        List<DbDiagnosticJsonResult> diagnostics,
        int limit)
    {
        var directories = new List<string>();
        try
        {
            var nameComparison = PathCasing.ComparisonFor(parent);
            foreach (var directory in CodeIndex.FileSystemTraversalPolicy.EnumerateDirectories(parent, prefix + "*"))
            {
                if (directories.Count >= limit)
                    return (directories, Truncated: true);
                if (Path.GetFileName(directory).StartsWith(prefix, nameComparison))
                    directories.Add(directory);
            }

            return (directories, Truncated: false);
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex) || ex is CodeIndexException)
        {
            diagnostics.Add(CreateCheckpointDiagnostic("restore_backup_directory_enumeration_failed", "Unable to enumerate every restore backup directory.", parent));
            return (directories, Truncated: true);
        }
    }

    private static bool TryDeleteRestoreBackupDirectory(
        string fullDbPath,
        string backupPath,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        var parent = Path.GetDirectoryName(fullDbPath) ?? Path.GetPathRoot(fullDbPath) ?? Path.GetFullPath(".");
        var prefix = GetRestoreBackupDirectoryPrefix(fullDbPath);
        if (!TryValidateTemporaryDirectoryCleanupTarget(
                backupPath,
                parent,
                prefix,
                out var fullPath,
                out var validationFailure,
                filesystemAwarePrefix: true))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_delete_skipped",
                $"Skipped deleting restore backup directory: {validationFailure}.",
                ConsoleUi.FormatBoundedValue(backupPath)));
            return false;
        }

        try
        {
            if (!Directory.Exists(LongPath.EnsureWindowsPrefix(fullPath)))
                return false;

            Directory.Delete(LongPath.EnsureWindowsPrefix(fullPath), recursive: true);
            return true;
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_delete_failed",
                $"Unable to delete restore backup directory ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(fullPath)));
            return false;
        }
    }

    private static bool TryDeleteCheckpointDirectory(
        string fullDbPath,
        string checkpointPath,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        if (!TryValidateCheckpointDirectoryTarget(
                fullDbPath,
                checkpointPath,
                out var fullPath,
                out var validationFailure))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_delete_skipped",
                $"Skipped deleting checkpoint directory: {validationFailure}.",
                ConsoleUi.FormatBoundedValue(checkpointPath)));
            return false;
        }

        try
        {
            if (!Directory.Exists(LongPath.EnsureWindowsPrefix(fullPath)))
                return false;
            if (!TryValidateCheckpointDirectoryTarget(
                    fullDbPath,
                    fullPath,
                    out fullPath,
                    out validationFailure))
            {
                diagnostics.Add(new DbDiagnosticJsonResult(
                    "checkpoint_delete_skipped",
                    $"Skipped deleting checkpoint directory after revalidation: {validationFailure}.",
                    ConsoleUi.FormatBoundedValue(checkpointPath)));
                return false;
            }

            Directory.Delete(LongPath.EnsureWindowsPrefix(fullPath), recursive: true);
            return true;
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_delete_failed",
                $"Unable to delete checkpoint directory ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(fullPath)));
            return false;
        }
    }

    private static bool TryValidateCheckpointDirectoryTarget(
        string fullDbPath,
        string checkpointPath,
        out string fullPath,
        out string failureReason)
    {
        var checkpointRoot = GetCheckpointRoot(fullDbPath);
        var rootStatus = FileSystemBoundary.TryGetAttributes(checkpointRoot, out var rootAttributes);
        if (rootStatus != FileSystemBoundaryProbeStatus.Found)
        {
            fullPath = string.Empty;
            failureReason = "checkpoint root is unavailable";
            return false;
        }
        if ((rootAttributes & FileAttributes.Directory) == 0
            || FileSystemBoundary.IsSymlinkOrReparsePoint(rootAttributes)
            || FileSystemBoundary.IsDevice(rootAttributes))
        {
            fullPath = string.Empty;
            failureReason = "checkpoint root is not a regular directory";
            return false;
        }

        var options = new DirectoryCleanupBoundaryOptions(
            ExpectedNamePrefix: string.Empty,
            OutsideRootReason: "target is outside the checkpoint root",
            PrefixMismatchReason: "target name is not a checkpoint name",
            UnsafeDirectoryReason: "target is not a regular checkpoint directory");
        return FileSystemBoundary.TryValidateDirectoryCleanupTarget(
            checkpointPath,
            checkpointRoot,
            options,
            out fullPath,
            out failureReason);
    }

    private static (List<string> Items, bool Truncated) EnumerateCheckpointDirectories(
        string root,
        List<DbDiagnosticJsonResult> diagnostics,
        int limit)
    {
        var directories = new List<string>();
        try
        {
            foreach (var directory in CodeIndex.FileSystemTraversalPolicy.EnumerateDirectories(root))
            {
                if (directories.Count >= limit)
                    return (directories, Truncated: true);
                directories.Add(directory);
            }

            return (directories, Truncated: false);
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(CreateCheckpointDiagnostic("checkpoint_directory_enumeration_failed", "Unable to enumerate every checkpoint directory.", root));
            return (directories, Truncated: true);
        }
    }

    private static (List<string> Items, bool Truncated) EnumerateCheckpointFileNames(
        string checkpointPath,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        var files = new List<string>();
        var truncated = false;
        try
        {
            if (EnumerateCheckpointFileNamesForTesting != null)
            {
                foreach (var name in EnumerateCheckpointFileNamesForTesting(checkpointPath))
                {
                    if (files.Count >= CheckpointFileInspectLimit)
                    {
                        truncated = true;
                        break;
                    }

                    if (name is not null)
                        files.Add(name);
                }
            }
            else
            {
                var listedFiles = EnumerateCheckpointFiles(checkpointPath, diagnostics, CheckpointFileInspectLimit + 1);
                foreach (var file in listedFiles.Items)
                {
                    if (files.Count >= CheckpointFileInspectLimit)
                    {
                        truncated = true;
                        break;
                    }

                    var name = Path.GetFileName(file);
                    if (name is not null)
                        files.Add(name);
                }

                truncated = listedFiles.Truncated || listedFiles.Items.Count > CheckpointFileInspectLimit;
            }
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(CreateCheckpointDiagnostic("checkpoint_file_enumeration_failed", "Unable to enumerate every checkpoint file.", checkpointPath));
            truncated = true;
        }

        files.Sort(StringComparer.Ordinal);
        return (files, truncated);
    }

    private static (long Bytes, bool Truncated) SumCheckpointBytes(string checkpointPath, List<DbDiagnosticJsonResult> diagnostics)
    {
        long bytes = 0;
        var filesSeen = 0;
        var files = EnumerateCheckpointFiles(checkpointPath, diagnostics, CheckpointFileInspectLimit + 1);
        foreach (var file in files.Items)
        {
            if (filesSeen >= CheckpointFileInspectLimit)
                return (bytes, Truncated: true);

            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (Exception ex) when (IsRecoverableFilesystemException(ex))
            {
                diagnostics.Add(CreateCheckpointDiagnostic("checkpoint_file_stat_failed", "Unable to inspect every checkpoint file.", file));
                return (bytes, Truncated: true);
            }

            filesSeen++;
        }

        return (bytes, files.Truncated);
    }

    private static (List<string> Items, bool Truncated) EnumerateCheckpointFiles(
        string checkpointPath,
        List<DbDiagnosticJsonResult> diagnostics,
        int limit)
    {
        var files = new List<string>();
        try
        {
            foreach (var file in EnumerateCheckpointFilesForTesting?.Invoke(checkpointPath) ?? CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(checkpointPath))
            {
                if (files.Count >= limit)
                    return (files, Truncated: true);
                files.Add(file);
            }

            return (files, Truncated: false);
        }
        catch (Exception ex) when (IsRecoverableFilesystemException(ex))
        {
            diagnostics.Add(CreateCheckpointDiagnostic("checkpoint_file_enumeration_failed", "Unable to enumerate every checkpoint file.", checkpointPath));
            return (files, Truncated: true);
        }
    }

}
