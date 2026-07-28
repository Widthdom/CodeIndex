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
    private const string ManifestEntryName = "manifest.json";
    private const string DatabaseEntryName = "codeindex.db";
    private static readonly string[] ExpectedImportArchiveEntryNames = [ManifestEntryName, DatabaseEntryName];
    internal const int MaxImportManifestBytes = 64 * 1024;
    internal const int MaxImportManifestJsonDepth = 16;
    internal const long MaxImportDatabaseBytes = 8L * 1024 * 1024 * 1024;
    internal const long MaxImportDatabaseCompressionRatio = 1000;
    private const int ImportCopyBufferSize = 81920;
    internal const int ManifestUnknownExtensionFileLimit = DbContext.UnknownExtensionFilePathSampleLimit;
    private const int ManifestUnknownExtensionJsonDepth = 4;
    internal const int ManifestUnknownExtensionDecodedItemLimit = ManifestUnknownExtensionFileLimit;
    internal const int ManifestUnknownExtensionPathCharLimit = 4096;
    internal const int ManifestUnknownExtensionFilesTotalCharLimit = 32 * 1024;
    internal const int MaxArchiveScopeValues = 64;
    internal const int MaxArchiveScopeValueChars = 4096;
    internal const int MaxArchiveScopeTotalChars = 32 * 1024;
    private static readonly DateTimeOffset DeterministicZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string ExportCommandName = "export";
    private const string ImportCommandName = "import";
    private const string PhaseParseArgs = "parse_args";
    private const string PhaseOpenArchive = "open_archive";
    private const string PhaseManifest = "manifest";
    private const string PhaseDatabaseEntry = "database_entry";
    private const string PhaseSha256 = "sha256";
    private const string PhaseSqliteValidate = "sqlite_validate";
    private const string PhasePrunePaths = "prune_paths";
    private const string PhaseDestinationDelta = "destination_delta";
    private const string PhasePreReplaceBackup = "pre_replace_backup";
    private const string PhaseScopeArchive = "scope_archive";
    private const string PhaseReplaceDb = "replace_db";
    private const string PhaseWriteArchive = "write_archive";
    private const string PhaseWriteCtags = "write_ctags";
    private const string ImportUsage = "cdidx import <archive> [--db <path>] [--prune-paths] [--no-backup] [--dry-run|--check] [--limit <n<=10000>] [--offset <n>] [--json]";
    private const string ArchiveExportUsage = "cdidx export <archive> [--db <path>] [--json] [--overwrite] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--project <name|path>] [--solution <path>] [--exclude-tests]";
    private const string CtagsExportUsage = "cdidx export ctags [--output <path>] [--db <path>] [--json] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--include-generated]";
    private const string CtagsSkipInvalidName = "invalid_name";
    private const string CtagsSkipUnsupportedKind = "unsupported_kind";
    private const string CtagsSkipGeneratedCode = "generated_code";
    private const string CtagsSkipLanguageFilter = "language_filter";
    private const string CtagsSkipTestFilter = "test_filter";
    private const string CtagsSkipPathFilter = "path_filter";
    private const string CtagsSkipExcludePathFilter = "exclude_path_filter";
    private const string CtagsSkipOther = "other";

    private sealed record ImportArguments(
        string ArchivePath,
        string? DbPath,
        bool WantsJson,
        bool PrunePaths,
        bool NoBackup,
        string ImportMode,
        bool DryRun,
        int Limit,
        int Offset);

    private sealed record ImportArgumentParseResult(ImportArguments? Arguments, int ExitCode);

    public static int RunExport(
        string[] args,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken = default)
    {
        if (args.Length > 0 && args[0] == "ctags")
            return RunExportCtags(args[1..], jsonOptions);

        return RunExportArchive(args, jsonOptions, appVersion, cancellationToken);
    }

}
