using System.Text.Json;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

public static partial class DbCommandRunner
{
    private static int RunRestoreBackupById(
        DbCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string fullDbPath,
        CancellationToken cancellationToken)
    {
        var id = options.Name ?? string.Empty;
        try
        {
            ManagedRestoreBackupStore.ValidateId(id);
        }
        catch (ArgumentException ex)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                CommandErrorWriter.FormatSanitizedExceptionMessage(ex),
                CommandExitCodes.UsageError,
                "Copy an ID from `cdidx db restore-backups --list`.",
                CommandErrorCodes.UsageError);
        }

        var preview = PreviewRestoreBackupById(
            fullDbPath,
            id,
            backupEnabled: !options.NoBackup,
            cancellationToken);
        if (options.RestoreBackupsDryRun)
            return WriteRestoreBackupByIdResult(options, jsonOptions, fullDbPath, preview, restored: false, preRestoreBackup: null);
        if (!preview.Ready)
            return WriteRestoreBackupByIdResult(options, jsonOptions, fullDbPath, preview, restored: false, preRestoreBackup: null);

        var parent = Path.GetDirectoryName(fullDbPath)
            ?? Path.GetPathRoot(fullDbPath)
            ?? Path.GetFullPath(".");
        var restoreSuffix = MakeRestorePathSuffix();
        var restoreTempPath = fullDbPath + ".restore-tmp-" + restoreSuffix;
        ManagedRestoreBackupInfo? preRestoreBackup = null;
        try
        {
            DataDirectorySecurity.CreateSensitiveDirectory(restoreTempPath);
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = preview.Source.Manifest
                ?? throw new InvalidDataException("managed restore backup manifest is unavailable");
            CopyIfExists(
                Path.Combine(preview.Source.BackupPath, manifest.DatabaseFile),
                Path.Combine(restoreTempPath, manifest.DatabaseFile),
                privateDestination: true);
            CopyIfExists(
                Path.Combine(preview.Source.BackupPath, ManagedRestoreBackupStore.ManifestFileName),
                Path.Combine(restoreTempPath, ManagedRestoreBackupStore.ManifestFileName),
                privateDestination: true);

            var stagedValidation = ManagedRestoreBackupStore.ValidateStagedDirectory(
                fullDbPath,
                restoreTempPath,
                id,
                cancellationToken);
            if (!stagedValidation.Ready)
                throw new ManagedRestoreBackupException("staged restore backup validation failed", stagedValidation.Diagnostics);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (!options.NoBackup)
            {
                preRestoreBackup = ManagedRestoreBackupStore.Create(
                    fullDbPath,
                    ManagedRestoreBackupStore.PreRestoreBackupProvenance,
                    id,
                    cancellationToken);
            }

            ExportImportCommandRunner.ReplaceImportedDatabase(
                Path.Combine(restoreTempPath, manifest.DatabaseFile),
                fullDbPath,
                cancellationToken);

            return WriteRestoreBackupByIdResult(
                options,
                jsonOptions,
                fullDbPath,
                preview,
                restored: true,
                preRestoreBackup);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ManagedRestoreBackupException ex)
        {
            var diagnostics = preview.Diagnostics.ToList();
            diagnostics.AddRange(ex.Diagnostics);
            var failed = preview with
            {
                Ready = false,
                Diagnostics = diagnostics,
                FailureMessage = "managed restore backup validation failed before replacement",
            };
            return WriteRestoreBackupByIdResult(
                options,
                jsonOptions,
                fullDbPath,
                failed,
                restored: false,
                preRestoreBackup);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or PathTooLongException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            var diagnostics = preview.Diagnostics.ToList();
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_replacement_failed",
                $"Managed restore backup replacement failed ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(fullDbPath)));
            var failed = preview with
            {
                Ready = false,
                Diagnostics = diagnostics,
                FailureMessage = "managed restore backup replacement failed; the previous destination was rolled back when possible",
            };
            return WriteRestoreBackupByIdResult(
                options,
                jsonOptions,
                fullDbPath,
                failed,
                restored: false,
                preRestoreBackup);
        }
        finally
        {
            TryDeleteTemporaryDirectory(
                restoreTempPath,
                "managed restore staging directory",
                parent,
                Path.GetFileName(fullDbPath) + ".restore-tmp-");
        }
    }

    private static DbManagedRestorePreview PreviewRestoreBackupById(
        string fullDbPath,
        string id,
        bool backupEnabled,
        CancellationToken cancellationToken)
    {
        var source = ManagedRestoreBackupStore.Validate(
            fullDbPath,
            id,
            checkFreeSpace: false,
            cancellationToken);
        var diagnostics = source.Diagnostics.ToList();
        var currentBackup = ManagedRestoreBackupStore.PreviewCreation(
            fullDbPath,
            backupEnabled,
            diagnostics,
            cancellationToken);

        long requiredSpace;
        try
        {
            requiredSpace = checked(source.RequiredSpaceBytes + currentBackup.RequiredSpaceBytes);
        }
        catch (OverflowException)
        {
            requiredSpace = long.MaxValue;
        }

        var availableSpace = TryGetAvailableFreeSpace(fullDbPath, diagnostics);
        var spaceSufficient = availableSpace is long available && available >= requiredSpace;
        if (!spaceSufficient)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                availableSpace.HasValue ? "restore_backup_space_insufficient" : "restore_backup_space_unavailable",
                availableSpace.HasValue
                    ? "The destination filesystem does not have enough free space for restore staging and rollback material."
                    : "Available destination space could not be confirmed for restore staging and rollback material.",
                ConsoleUi.FormatBoundedValue(Path.GetDirectoryName(fullDbPath) ?? fullDbPath)));
        }

        return new DbManagedRestorePreview(
            Ready: source.Ready && currentBackup.Ready && spaceSufficient,
            source,
            currentBackup,
            requiredSpace,
            availableSpace,
            spaceSufficient,
            diagnostics,
            FailureMessage: null);
    }

    private static int WriteRestoreBackupByIdResult(
        DbCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string fullDbPath,
        DbManagedRestorePreview preview,
        bool restored,
        ManagedRestoreBackupInfo? preRestoreBackup)
    {
        var dryRun = options.RestoreBackupsDryRun;
        var status = restored ? "success" : dryRun && preview.Ready ? "dry_run" : "invalid";
        var message = preview.FailureMessage
            ?? (!preview.Ready ? "managed restore backup validation found blocking failures" : null);
        const string hint = "Fix the reported manifest, hash, schema, or free-space diagnostics and retry; use --no-backup only when discarding rollback material is intentional.";

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new DbRestoreBackupRestoreJsonResult(
                    status,
                    fullDbPath,
                    preview.Source.Id,
                    preview.Source.BackupPath,
                    dryRun,
                    restored,
                    preview.Ready,
                    preview.Source.ManifestValid,
                    preview.Source.HashValid,
                    preview.Source.SchemaValid,
                    options.NoBackup ? "disabled" : "automatic",
                    preRestoreBackup?.Id,
                    preview.CurrentBackup.WouldCreate,
                    preview.RequiredSpaceBytes,
                    preview.AvailableSpaceBytes,
                    preview.SpaceSufficient,
                    preview.Source.Manifest?.Provenance,
                    preview.Source.Manifest?.SourceId,
                    preview.Diagnostics,
                    message,
                    preview.Ready || restored ? null : CommandErrorCodes.DbError,
                    preview.Ready || restored ? null : hint),
                CliJsonSerializerContextFactory.Create(jsonOptions).DbRestoreBackupRestoreJsonResult));
        }
        else
        {
            Console.WriteLine(dryRun
                ? "Managed restore backup dry run."
                : restored
                    ? "Restored managed database backup."
                    : "Managed restore backup validation failed.");
            Console.WriteLine($"  database       : {fullDbPath}");
            Console.WriteLine($"  backup ID      : {preview.Source.Id}");
            Console.WriteLine($"  backup policy  : {(options.NoBackup ? "disabled" : "automatic")}");
            Console.WriteLine($"  manifest       : {(preview.Source.ManifestValid ? "valid" : "invalid")}");
            Console.WriteLine($"  SHA-256        : {(preview.Source.HashValid ? "valid" : "invalid")}");
            Console.WriteLine($"  schema         : {(preview.Source.SchemaValid ? "valid" : "invalid")}");
            Console.WriteLine($"  required bytes : {preview.RequiredSpaceBytes:N0}");
            Console.WriteLine($"  available bytes: {(preview.AvailableSpaceBytes is long available ? available.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "unknown")}");
            Console.WriteLine($"  side effect    : {(dryRun ? "none" : restored ? "database atomically replaced" : "none")}");
            if (preRestoreBackup is not null)
                Console.WriteLine($"  rollback ID    : {preRestoreBackup.Id}");
            foreach (var diagnostic in preview.Diagnostics)
                CommandErrorWriter.WriteStderr($"Warning [{diagnostic.Code}]: {diagnostic.Message}");
            if (!preview.Ready && !restored)
                CommandErrorWriter.WriteStderr($"Error: {message}. Hint: {hint}");
        }

        return restored || (dryRun && preview.Ready)
            ? CommandExitCodes.Success
            : CommandExitCodes.DatabaseError;
    }
}

internal sealed record DbManagedRestorePreview(
    bool Ready,
    ManagedRestoreBackupValidation Source,
    ManagedRestoreBackupCreationPreview CurrentBackup,
    long RequiredSpaceBytes,
    long? AvailableSpaceBytes,
    bool SpaceSufficient,
    List<DbDiagnosticJsonResult> Diagnostics,
    string? FailureMessage);
