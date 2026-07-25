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
        ExportManifest manifest)
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
                    ReplacementWouldBeAllowed: true,
                    validationPhases,
                    DestinationDelta: destinationDelta,
                    UnknownExtensionFileCount: manifest.UnknownExtensionFileCount,
                    UnknownExtensionFiles: manifest.UnknownExtensionFiles,
                    UnknownExtensionFilesTruncated: manifest.UnknownExtensionFilesTruncated,
                    UnknownExtensionFilePathLimit: manifest.UnknownExtensionFilePathLimit,
                    UnknownExtensionFileSampleCount: manifest.UnknownExtensionFileSampleCount,
                    UnknownExtensionFileSampleLimit: manifest.UnknownExtensionFileSampleLimit,
                    UnknownExtensionFileSampleTruncated: manifest.UnknownExtensionFileSampleTruncated),
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
        ExportManifest manifest)
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
                    validationPhases,
                    UnknownExtensionFileCount: manifest.UnknownExtensionFileCount,
                    UnknownExtensionFiles: manifest.UnknownExtensionFiles,
                    UnknownExtensionFilesTruncated: manifest.UnknownExtensionFilesTruncated,
                    UnknownExtensionFilePathLimit: manifest.UnknownExtensionFilePathLimit,
                    UnknownExtensionFileSampleCount: manifest.UnknownExtensionFileSampleCount,
                    UnknownExtensionFileSampleLimit: manifest.UnknownExtensionFileSampleLimit,
                    UnknownExtensionFileSampleTruncated: manifest.UnknownExtensionFileSampleTruncated),
                jsonOptions));
        }
        else
        {
            Console.WriteLine(FormatImportSuccessMessage(
                $"Imported CodeIndex database to {fullDbPath}",
                importArguments.PrunePaths,
                importTargetProjectRoot));
        }

        return CommandExitCodes.Success;
    }
}
