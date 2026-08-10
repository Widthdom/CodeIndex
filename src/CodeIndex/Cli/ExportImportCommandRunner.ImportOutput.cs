using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Archives;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ExportImportCommandRunner
{
    private static int WriteImportDryRunResult(
        ImportArguments importArguments,
        JsonSerializerOptions jsonOptions,
        string fullDbPath,
        string importTargetProjectRoot,
        IReadOnlyList<ImportValidationPhaseResult> validationPhases,
        ImportDestinationDeltaResult destinationDelta,
        ExportManifest manifest,
        ManagedRestoreBackupCreationPreview backupPreview)
    {
        if (importArguments.WantsJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new ImportDryRunResult(
                    "1",
                    "success",
                    Path.GetFullPath(importArguments.ArchivePath),
                    fullDbPath,
                    importArguments.ImportMode,
                    importArguments.DryRun,
                    importArguments.PrunePaths,
                    importArguments.PrunePaths ? importTargetProjectRoot : null,
                    BackupPolicy: importArguments.NoBackup ? "disabled" : "automatic",
                    BackupWouldBeCreated: backupPreview.WouldCreate,
                    BackupRequiredSpaceBytes: backupPreview.RequiredSpaceBytes,
                    ReplacementWouldBeAllowed: true,
                    ValidationPhases: validationPhases,
                    DestinationDelta: destinationDelta,
                    UnknownExtensionFileCount: manifest.UnknownExtensionFileCount,
                    UnknownExtensionFiles: manifest.UnknownExtensionFiles,
                    UnknownExtensionFilesTruncated: manifest.UnknownExtensionFilesTruncated,
                    UnknownExtensionFilePathLimit: manifest.UnknownExtensionFilePathLimit,
                    UnknownExtensionFileSampleCount: manifest.UnknownExtensionFileSampleCount,
                    UnknownExtensionFileSampleLimit: manifest.UnknownExtensionFileSampleLimit,
                    UnknownExtensionFileSampleTruncated: manifest.UnknownExtensionFileSampleTruncated,
                    IndexComplete: manifest.IndexComplete,
                    IndexIncompleteReasons: manifest.IndexIncompleteReasons,
                    Scope: manifest.Scope),
                CliJsonSerializerContextFactory.Create(jsonOptions).ImportDryRunResult));
        }
        else
        {
            Console.WriteLine(FormatImportSuccessMessage(
                $"Validated CodeIndex archive {Path.GetFullPath(importArguments.ArchivePath)}; replacement would be allowed for {fullDbPath}{FormatDestinationDeltaSummary(destinationDelta)}",
                importArguments.PrunePaths,
                importTargetProjectRoot));
        }

        return CommandExitCodes.Success;
    }

    private static int WriteImportResult(
        ImportArguments importArguments,
        JsonSerializerOptions jsonOptions,
        string fullDbPath,
        string importTargetProjectRoot,
        IReadOnlyList<ImportValidationPhaseResult> validationPhases,
        ExportManifest manifest,
        ManagedRestoreBackupInfo? managedBackup)
    {
        if (importArguments.WantsJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new ImportResult(
                    "1",
                    "success",
                    Path.GetFullPath(importArguments.ArchivePath),
                    fullDbPath,
                    importArguments.ImportMode,
                    DryRun: false,
                    importArguments.PrunePaths,
                    importArguments.PrunePaths ? importTargetProjectRoot : null,
                    BackupPolicy: importArguments.NoBackup ? "disabled" : "automatic",
                    BackupId: managedBackup?.Id,
                    ValidationPhases: validationPhases,
                    UnknownExtensionFileCount: manifest.UnknownExtensionFileCount,
                    UnknownExtensionFiles: manifest.UnknownExtensionFiles,
                    UnknownExtensionFilesTruncated: manifest.UnknownExtensionFilesTruncated,
                    UnknownExtensionFilePathLimit: manifest.UnknownExtensionFilePathLimit,
                    UnknownExtensionFileSampleCount: manifest.UnknownExtensionFileSampleCount,
                    UnknownExtensionFileSampleLimit: manifest.UnknownExtensionFileSampleLimit,
                    UnknownExtensionFileSampleTruncated: manifest.UnknownExtensionFileSampleTruncated,
                    IndexComplete: manifest.IndexComplete,
                    IndexIncompleteReasons: manifest.IndexIncompleteReasons,
                    Scope: manifest.Scope),
                jsonOptions));
        }
        else
        {
            Console.WriteLine(FormatImportSuccessMessage(
                managedBackup is null
                    ? $"Imported CodeIndex database to {fullDbPath}"
                    : $"Imported CodeIndex database to {fullDbPath}; rollback backup ID: {managedBackup.Id}",
                importArguments.PrunePaths,
                importTargetProjectRoot));
        }

        return CommandExitCodes.Success;
    }
}
