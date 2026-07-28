using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static class ManagedRestoreBackupStore
{
    internal const string ManifestFileName = "manifest.json";
    internal const string Format = "cdidx-managed-restore-backup";
    internal const int FormatVersion = 1;
    internal const int ManifestByteLimit = 64 * 1024;
    internal const string PreImportProvenance = "pre_import";
    internal const string PreCheckpointRestoreProvenance = "pre_checkpoint_restore";
    internal const string PreRestoreBackupProvenance = "pre_restore_backup";

    private const long ManifestSpaceAllowanceBytes = 64 * 1024;

    internal static ManagedRestoreBackupCreationPreview PreviewCreation(
        string fullDbPath,
        bool enabled,
        List<DbDiagnosticJsonResult> diagnostics,
        CancellationToken cancellationToken = default)
    {
        if (!enabled)
            return new ManagedRestoreBackupCreationPreview(false, false, true, 0, null, true);
        if (!File.Exists(LongPath.EnsureWindowsPrefix(fullDbPath)))
            return new ManagedRestoreBackupCreationPreview(false, false, true, 0, null, true);

        cancellationToken.ThrowIfCancellationRequested();
        if (!DbContext.TryValidateExistingCodeIndexDb(
                fullDbPath,
                requireWritable: false,
                requireSupportedUserVersion: true,
                out var validationMessage,
                out _,
                out _,
                cancellationToken))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_source_invalid",
                $"The current database cannot be captured as verified rollback material ({DiagnosticSanitizer.ForMessage(validationMessage)}).",
                ConsoleUi.FormatBoundedValue(fullDbPath)));
            return new ManagedRestoreBackupCreationPreview(true, true, false, 0, null, false);
        }

        var requiredBytes = EstimateSnapshotBytes(fullDbPath, diagnostics);
        var availableBytes = DbCommandRunner.TryGetAvailableFreeSpace(fullDbPath, diagnostics);
        var spaceSufficient = availableBytes is long available && available >= requiredBytes;
        if (!spaceSufficient)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                availableBytes.HasValue ? "restore_backup_space_insufficient" : "restore_backup_space_unavailable",
                availableBytes.HasValue
                    ? "The destination filesystem does not have enough free space for a verified rollback snapshot."
                    : "Available destination space could not be confirmed for a verified rollback snapshot.",
                ConsoleUi.FormatBoundedValue(Path.GetDirectoryName(fullDbPath) ?? fullDbPath)));
        }

        return new ManagedRestoreBackupCreationPreview(
            Enabled: true,
            WouldCreate: true,
            Ready: spaceSufficient,
            RequiredSpaceBytes: requiredBytes,
            AvailableSpaceBytes: availableBytes,
            SpaceSufficient: spaceSufficient);
    }

    internal static ManagedRestoreBackupInfo? Create(
        string fullDbPath,
        string provenance,
        string? sourceId,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(LongPath.EnsureWindowsPrefix(fullDbPath)))
            return null;

        ValidateProvenance(provenance);
        ValidateSourceId(sourceId);

        var diagnostics = new List<DbDiagnosticJsonResult>();
        var preview = PreviewCreation(fullDbPath, enabled: true, diagnostics, cancellationToken);
        if (!preview.Ready)
            throw new ManagedRestoreBackupException("verified rollback backup preflight failed", diagnostics);

        var id = MakeId();
        var backupPath = GetBackupPath(fullDbPath, id);
        var parent = Path.GetDirectoryName(fullDbPath)
            ?? Path.GetPathRoot(fullDbPath)
            ?? Path.GetFullPath(".");
        var tempPath = fullDbPath + ".restore-tmp-" + id;
        var published = false;
        DataDirectorySecurity.CreateSensitiveDirectory(tempPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dbFileName = Path.GetFileName(fullDbPath);
            var snapshotPath = Path.Combine(tempPath, dbFileName);
            ExportImportCommandRunner.CreateDatabaseSnapshot(fullDbPath, snapshotPath, cancellationToken);

            var manifest = CreateManifest(
                snapshotPath,
                id,
                dbFileName,
                provenance,
                sourceId,
                cancellationToken);
            WriteManifest(tempPath, manifest);

            var stagedValidation = ValidateDirectory(
                fullDbPath,
                tempPath,
                id,
                requireManagedBoundary: false,
                checkFreeSpace: false,
                cancellationToken);
            if (!stagedValidation.Ready)
                throw new ManagedRestoreBackupException("staged rollback backup validation failed", stagedValidation.Diagnostics);

            AtomicFileWriter.PublishDirectory(tempPath, backupPath);
            published = true;

            var publishedValidation = Validate(
                fullDbPath,
                id,
                checkFreeSpace: false,
                cancellationToken);
            if (!publishedValidation.Ready)
                throw new ManagedRestoreBackupException("published rollback backup validation failed", publishedValidation.Diagnostics);

            return new ManagedRestoreBackupInfo(
                id,
                backupPath,
                manifest.CreatedAtUtc,
                provenance,
                sourceId,
                manifest.DatabaseBytes,
                manifest.DatabaseSha256,
                manifest.UserVersion);
        }
        catch (Exception ex)
        {
            var cleanupPath = published ? backupPath : tempPath;
            var cleanupPrefix = published
                ? Path.GetFileName(fullDbPath) + ".restore-backup-"
                : Path.GetFileName(fullDbPath) + ".restore-tmp-";
            DbCommandRunner.TryDeleteTemporaryDirectory(
                cleanupPath,
                published ? "invalid managed restore backup" : "managed restore backup staging directory",
                parent,
                cleanupPrefix);
            if (ex is ManagedRestoreBackupException or OperationCanceledException)
                throw;
            if (IsRecoverableValidationException(ex))
            {
                throw new ManagedRestoreBackupException(
                    "verified rollback backup creation failed",
                    [
                        new DbDiagnosticJsonResult(
                            "restore_backup_creation_failed",
                            $"Verified rollback backup creation failed ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                            ConsoleUi.FormatBoundedValue(fullDbPath)),
                    ]);
            }

            throw;
        }
    }

    internal static ManagedRestoreBackupValidation Validate(
        string fullDbPath,
        string id,
        bool checkFreeSpace,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return ValidateDirectory(
            fullDbPath,
            GetBackupPath(fullDbPath, id),
            id,
            requireManagedBoundary: true,
            checkFreeSpace,
            cancellationToken);
    }

    internal static ManagedRestoreBackupValidation ValidateStagedDirectory(
        string fullDbPath,
        string stagedDirectory,
        string id,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return ValidateDirectory(
            fullDbPath,
            stagedDirectory,
            id,
            requireManagedBoundary: false,
            checkFreeSpace: false,
            cancellationToken);
    }

    internal static bool TryReadSummary(
        string fullDbPath,
        string backupPath,
        out ManagedRestoreBackupSummary summary)
    {
        summary = default;
        try
        {
            var nameComparison = ResolveDatabaseFileNameComparison(fullDbPath);
            var prefix = GetBackupDirectoryPrefix(fullDbPath);
            var directoryName = Path.GetFileName(backupPath);
            if (!directoryName.StartsWith(prefix, nameComparison))
                return false;

            var id = directoryName[prefix.Length..];
            ValidateId(id);
            if (!TryReadManifest(backupPath, out var manifest, out _)
                || !ValidateManifestHeader(
                    manifest,
                    id,
                    Path.GetFileName(fullDbPath),
                    nameComparison,
                    out _))
            {
                return false;
            }

            summary = new ManagedRestoreBackupSummary(
                id,
                manifest.CreatedAtUtc,
                manifest.Provenance,
                manifest.SourceId,
                manifest.DatabaseBytes,
                manifest.UserVersion);
            return true;
        }
        catch (Exception ex) when (IsRecoverableValidationException(ex))
        {
            return false;
        }
    }

    internal static string GetBackupPath(string fullDbPath, string id)
    {
        ValidateId(id);
        var parent = Path.GetDirectoryName(fullDbPath)
            ?? Path.GetPathRoot(fullDbPath)
            ?? Path.GetFullPath(".");
        return Path.Combine(parent, GetBackupDirectoryPrefix(fullDbPath) + id);
    }

    internal static string GetBackupDirectoryPrefix(string fullDbPath)
        => Path.GetFileName(fullDbPath) + ".restore-backup-";

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Length > 128
            || id is "." or ".."
            || id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
        {
            throw new ArgumentException("restore backup ID must contain only ASCII letters, digits, and hyphens");
        }
    }

    private static ManagedRestoreBackupValidation ValidateDirectory(
        string fullDbPath,
        string backupPath,
        string id,
        bool requireManagedBoundary,
        bool checkFreeSpace,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<DbDiagnosticJsonResult>();
        var validatedPath = backupPath;
        if (requireManagedBoundary
            && !TryValidateBackupDirectory(fullDbPath, backupPath, out validatedPath, out var pathFailure))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_path_invalid",
                $"Restore backup directory failed path validation: {pathFailure}.",
                ConsoleUi.FormatBoundedValue(backupPath)));
            return InvalidValidation(id, backupPath, diagnostics);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadManifest(validatedPath, out var manifest, out var manifestFailure))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_manifest_invalid",
                manifestFailure,
                ConsoleUi.FormatBoundedValue(Path.Combine(validatedPath, ManifestFileName))));
            return InvalidValidation(id, validatedPath, diagnostics);
        }

        StringComparison nameComparison;
        try
        {
            nameComparison = ResolveDatabaseFileNameComparison(fullDbPath);
        }
        catch (Exception ex) when (IsRecoverableValidationException(ex))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_path_case_unavailable",
                $"Restore backup database filename comparison could not be determined ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(fullDbPath)));
            return InvalidValidation(id, validatedPath, diagnostics, manifest);
        }

        if (!ValidateManifestHeader(
                manifest,
                id,
                Path.GetFileName(fullDbPath),
                nameComparison,
                out manifestFailure))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_manifest_invalid",
                manifestFailure,
                ConsoleUi.FormatBoundedValue(Path.Combine(validatedPath, ManifestFileName))));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest);
        }

        var snapshotPath = Path.Combine(validatedPath, manifest.DatabaseFile);
        if (!TryGetRegularFile(snapshotPath, out var normalizedSnapshotPath, out var fileFailure))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_payload_invalid",
                fileFailure,
                ConsoleUi.FormatBoundedValue(snapshotPath)));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest,
                manifestValid: true);
        }

        var unexpectedEntries = 0;
        try
        {
            foreach (var entry in CodeIndex.FileSystemTraversalPolicy.EnumerateFileSystemEntries(validatedPath))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, manifest.DatabaseFile, StringComparison.Ordinal)
                    || string.Equals(name, ManifestFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                unexpectedEntries++;
                if (unexpectedEntries >= 1)
                    break;
            }
        }
        catch (Exception ex) when (IsRecoverableValidationException(ex))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_payload_enumeration_failed",
                $"Restore backup contents could not be enumerated ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(validatedPath)));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest,
                manifestValid: true);
        }

        if (unexpectedEntries != 0)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_payload_unexpected_entry",
                "Restore backup contains an unexpected filesystem entry.",
                ConsoleUi.FormatBoundedValue(validatedPath)));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest,
                manifestValid: true);
        }

        long actualBytes;
        try
        {
            actualBytes = new FileInfo(normalizedSnapshotPath).Length;
        }
        catch (Exception ex) when (IsRecoverableValidationException(ex))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_payload_stat_failed",
                $"Restore backup database size could not be read ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(snapshotPath)));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest,
                manifestValid: true);
        }

        if (actualBytes != manifest.DatabaseBytes)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_size_mismatch",
                "Restore backup database size does not match its manifest.",
                ConsoleUi.FormatBoundedValue(snapshotPath)));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest,
                manifestValid: true);
        }

        string actualSha256;
        try
        {
            actualSha256 = ComputeSha256(normalizedSnapshotPath, cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableValidationException(ex))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_hash_unavailable",
                $"Restore backup database SHA-256 could not be computed ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(snapshotPath)));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest,
                manifestValid: true);
        }

        if (!string.Equals(actualSha256, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_hash_mismatch",
                "Restore backup database SHA-256 does not match its manifest.",
                ConsoleUi.FormatBoundedValue(snapshotPath)));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest,
                manifestValid: true);
        }

        if (!DbContext.TryValidateExistingCodeIndexDb(
                normalizedSnapshotPath,
                requireWritable: false,
                requireSupportedUserVersion: true,
                out var schemaMessage,
                out _,
                out _,
                cancellationToken))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_schema_invalid",
                $"Restore backup database schema is not supported ({DiagnosticSanitizer.ForMessage(schemaMessage)}).",
                ConsoleUi.FormatBoundedValue(snapshotPath)));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest,
                manifestValid: true,
                hashValid: true);
        }

        int actualUserVersion;
        try
        {
            actualUserVersion = ReadUserVersion(normalizedSnapshotPath);
        }
        catch (Exception ex) when (IsRecoverableValidationException(ex))
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_schema_stamp_unavailable",
                $"Restore backup database schema stamp could not be read ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(snapshotPath)));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest,
                manifestValid: true,
                hashValid: true);
        }
        if (actualUserVersion != manifest.UserVersion)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_schema_mismatch",
                "Restore backup database schema stamp does not match its manifest.",
                ConsoleUi.FormatBoundedValue(snapshotPath)));
            return InvalidValidation(
                id,
                validatedPath,
                diagnostics,
                manifest,
                manifestValid: true,
                hashValid: true);
        }

        long? availableBytes = null;
        bool? spaceSufficient = null;
        if (checkFreeSpace)
        {
            availableBytes = DbCommandRunner.TryGetAvailableFreeSpace(fullDbPath, diagnostics);
            spaceSufficient = availableBytes is long available && available >= manifest.DatabaseBytes;
            if (spaceSufficient != true)
            {
                diagnostics.Add(new DbDiagnosticJsonResult(
                    availableBytes.HasValue ? "restore_backup_space_insufficient" : "restore_backup_space_unavailable",
                    availableBytes.HasValue
                        ? "The destination filesystem does not have enough free space to stage this restore backup."
                        : "Available destination space could not be confirmed for this restore backup.",
                    ConsoleUi.FormatBoundedValue(Path.GetDirectoryName(fullDbPath) ?? fullDbPath)));
            }
        }

        return new ManagedRestoreBackupValidation(
            Ready: !checkFreeSpace || spaceSufficient == true,
            id,
            validatedPath,
            manifest,
            ManifestValid: true,
            HashValid: true,
            SchemaValid: true,
            RequiredSpaceBytes: manifest.DatabaseBytes,
            AvailableSpaceBytes: availableBytes,
            SpaceSufficient: spaceSufficient,
            diagnostics);
    }

    private static ManagedRestoreBackupValidation InvalidValidation(
        string id,
        string backupPath,
        List<DbDiagnosticJsonResult> diagnostics,
        ManagedRestoreBackupManifest? manifest = null,
        bool manifestValid = false,
        bool hashValid = false,
        bool schemaValid = false)
        => new(
            Ready: false,
            id,
            backupPath,
            manifest,
            ManifestValid: manifestValid,
            HashValid: hashValid,
            SchemaValid: schemaValid,
            RequiredSpaceBytes: manifest?.DatabaseBytes ?? 0,
            AvailableSpaceBytes: null,
            SpaceSufficient: null,
            diagnostics);

    private static ManagedRestoreBackupManifest CreateManifest(
        string snapshotPath,
        string id,
        string dbFileName,
        string provenance,
        string? sourceId,
        CancellationToken cancellationToken)
    {
        if (!DbContext.TryValidateExistingCodeIndexDb(
                snapshotPath,
                requireWritable: false,
                requireSupportedUserVersion: true,
                out var validationMessage,
                out _,
                out _,
                cancellationToken))
        {
            throw new InvalidDataException(
                $"rollback snapshot is not a supported CodeIndex database ({DiagnosticSanitizer.ForMessage(validationMessage)})");
        }

        var bytes = new FileInfo(LongPath.EnsureWindowsPrefix(snapshotPath)).Length;
        return new ManagedRestoreBackupManifest(
            Format,
            FormatVersion,
            id,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            provenance,
            sourceId,
            dbFileName,
            bytes,
            ComputeSha256(snapshotPath, cancellationToken),
            ReadUserVersion(snapshotPath));
    }

    private static void WriteManifest(string directory, ManagedRestoreBackupManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, ProgramRunner.CreateDefaultJsonOptions());
        if (System.Text.Encoding.UTF8.GetByteCount(json) > ManifestByteLimit)
            throw new InvalidDataException("managed restore backup manifest exceeds its size limit");
        DataDirectorySecurity.WritePrivateText(Path.Combine(directory, ManifestFileName), json + "\n");
    }

    private static bool TryReadManifest(
        string directory,
        out ManagedRestoreBackupManifest manifest,
        out string failure)
    {
        manifest = null!;
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!TryGetRegularFile(manifestPath, out var normalizedManifestPath, out failure))
            return false;

        try
        {
            var json = DataDirectorySecurity.ReadTextWithinLimit(
                normalizedManifestPath,
                ManifestByteLimit,
                FileShare.Read);
            if (json is null)
            {
                failure = "Restore backup manifest is missing or exceeds its size limit.";
                return false;
            }

            manifest = JsonSerializer.Deserialize<ManagedRestoreBackupManifest>(
                json,
                ProgramRunner.CreateDefaultJsonOptions())!;
            if (manifest is null)
            {
                failure = "Restore backup manifest is empty.";
                return false;
            }

            failure = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException || IsRecoverableValidationException(ex))
        {
            failure = $"Restore backup manifest could not be parsed ({CommandErrorWriter.FormatSanitizedException(ex)}).";
            return false;
        }
    }

    private static bool ValidateManifestHeader(
        ManagedRestoreBackupManifest manifest,
        string id,
        string dbFileName,
        StringComparison dbFileNameComparison,
        out string failure)
    {
        if (!string.Equals(manifest.Format, Format, StringComparison.Ordinal)
            || manifest.FormatVersion != FormatVersion
            || !string.Equals(manifest.Id, id, StringComparison.Ordinal)
            || !DateTimeOffset.TryParse(
                manifest.CreatedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _)
            || !string.Equals(manifest.DatabaseFile, dbFileName, dbFileNameComparison)
            || !string.Equals(Path.GetFileName(manifest.DatabaseFile), manifest.DatabaseFile, StringComparison.Ordinal)
            || manifest.DatabaseBytes < 0
            || string.IsNullOrWhiteSpace(manifest.DatabaseSha256)
            || manifest.DatabaseSha256.Length != 64
            || manifest.DatabaseSha256.Any(ch => !Uri.IsHexDigit(ch))
            || manifest.UserVersion < 0)
        {
            failure = "Restore backup manifest header, database metadata, or identifier is invalid.";
            return false;
        }

        try
        {
            ValidateProvenance(manifest.Provenance);
            ValidateSourceId(manifest.SourceId);
        }
        catch (ArgumentException)
        {
            failure = "Restore backup manifest provenance is invalid.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static void ValidateProvenance(string provenance)
    {
        if (provenance is not (PreImportProvenance
            or PreCheckpointRestoreProvenance
            or PreRestoreBackupProvenance))
        {
            throw new ArgumentException("unsupported managed restore backup provenance", nameof(provenance));
        }
    }

    private static void ValidateSourceId(string? sourceId)
    {
        if (sourceId is null)
            return;
        if (sourceId.Length is 0 or > 128
            || sourceId.Any(ch => char.IsControl(ch)
                || ch is '/' or '\\'
                || ch == Path.DirectorySeparatorChar
                || ch == Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("managed restore backup source ID is invalid", nameof(sourceId));
        }
    }

    private static bool TryValidateBackupDirectory(
        string fullDbPath,
        string backupPath,
        out string validatedPath,
        out string failure)
    {
        var parent = Path.GetDirectoryName(fullDbPath)
            ?? Path.GetPathRoot(fullDbPath)
            ?? Path.GetFullPath(".");
        try
        {
            var nameComparison = PathCasing.ComparisonFor(parent);
            var options = new DirectoryCleanupBoundaryOptions(
                GetBackupDirectoryPrefix(fullDbPath),
                "target is outside the database directory",
                "target name does not match the managed restore-backup prefix",
                "target is not a regular managed restore-backup directory",
                NameComparison: nameComparison);
            return FileSystemBoundary.TryValidateDirectoryCleanupTarget(
                backupPath,
                parent,
                options,
                out validatedPath,
                out failure,
                pathComparisonOverride: nameComparison);
        }
        catch (Exception ex) when (IsRecoverableValidationException(ex))
        {
            validatedPath = string.Empty;
            failure = $"database filename comparison could not be determined ({CommandErrorWriter.FormatSanitizedException(ex)})";
            return false;
        }
    }

    private static StringComparison ResolveDatabaseFileNameComparison(string fullDbPath)
    {
        var parent = Path.GetDirectoryName(fullDbPath)
            ?? Path.GetPathRoot(fullDbPath)
            ?? Path.GetFullPath(".");
        return PathCasing.ComparisonFor(parent);
    }

    private static bool TryGetRegularFile(string path, out string normalizedPath, out string failure)
    {
        normalizedPath = LongPath.EnsureWindowsPrefix(path);
        try
        {
            var attributes = File.GetAttributes(normalizedPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                failure = "Restore backup payload is not a regular file.";
                return false;
            }

            failure = string.Empty;
            return true;
        }
        catch (FileNotFoundException)
        {
            failure = "Restore backup payload is missing.";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            failure = "Restore backup directory is missing.";
            return false;
        }
        catch (Exception ex) when (IsRecoverableValidationException(ex))
        {
            failure = $"Restore backup payload could not be inspected ({CommandErrorWriter.FormatSanitizedException(ex)}).";
            return false;
        }
    }

    private static long EstimateSnapshotBytes(
        string fullDbPath,
        List<DbDiagnosticJsonResult> diagnostics)
    {
        long bytes = ManifestSpaceAllowanceBytes;
        try
        {
            foreach (var path in new[] { fullDbPath, fullDbPath + "-wal", fullDbPath + "-shm" })
            {
                try
                {
                    var attributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(path));
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        throw new InvalidOperationException("database snapshot source is not a regular file");
                    bytes = checked(bytes + new FileInfo(LongPath.EnsureWindowsPrefix(path)).Length);
                }
                catch (FileNotFoundException)
                {
                    // Optional SQLite sidecar is absent.
                }
                catch (DirectoryNotFoundException)
                {
                    // Optional SQLite sidecar is absent.
                }
            }
        }
        catch (Exception ex) when (IsRecoverableValidationException(ex) || ex is OverflowException)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "restore_backup_size_unavailable",
                $"Rollback snapshot size could not be determined ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                ConsoleUi.FormatBoundedValue(fullDbPath)));
            return long.MaxValue;
        }

        return bytes;
    }

    private static int ReadUserVersion(string dbPath)
    {
        using var connection = DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
            dbPath,
            pooling: false,
            out _,
            out _);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SqliteCommandPolicy.PragmaSql("user_version");
        return SqliteCommandPolicy.ReadInt32Scalar(command, "pragma user_version");
    }

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            LongPath.EnsureWindowsPrefix(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string MakeId()
        => DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N");

    private static bool IsRecoverableValidationException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or PathTooLongException
            or CodeIndexException
            or SqliteException;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ManagedRestoreBackupManifest(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("format_version")] int FormatVersion,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created_at_utc")] string CreatedAtUtc,
    [property: JsonPropertyName("provenance")] string Provenance,
    [property: JsonPropertyName("source_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceId,
    [property: JsonPropertyName("database_file")] string DatabaseFile,
    [property: JsonPropertyName("database_bytes")] long DatabaseBytes,
    [property: JsonPropertyName("database_sha256")] string DatabaseSha256,
    [property: JsonPropertyName("user_version")] int UserVersion);

internal sealed record ManagedRestoreBackupInfo(
    string Id,
    string BackupPath,
    string CreatedAtUtc,
    string Provenance,
    string? SourceId,
    long DatabaseBytes,
    string DatabaseSha256,
    int UserVersion);

internal readonly record struct ManagedRestoreBackupSummary(
    string Id,
    string CreatedAtUtc,
    string Provenance,
    string? SourceId,
    long DatabaseBytes,
    int UserVersion);

internal sealed record ManagedRestoreBackupCreationPreview(
    bool Enabled,
    bool WouldCreate,
    bool Ready,
    long RequiredSpaceBytes,
    long? AvailableSpaceBytes,
    bool? SpaceSufficient);

internal sealed record ManagedRestoreBackupValidation(
    bool Ready,
    string Id,
    string BackupPath,
    ManagedRestoreBackupManifest? Manifest,
    bool ManifestValid,
    bool HashValid,
    bool SchemaValid,
    long RequiredSpaceBytes,
    long? AvailableSpaceBytes,
    bool? SpaceSufficient,
    List<DbDiagnosticJsonResult> Diagnostics);

internal sealed class ManagedRestoreBackupException : IOException
{
    internal ManagedRestoreBackupException(
        string message,
        IReadOnlyList<DbDiagnosticJsonResult> diagnostics)
        : base(message)
    {
        Diagnostics = diagnostics;
    }

    internal IReadOnlyList<DbDiagnosticJsonResult> Diagnostics { get; }
}
