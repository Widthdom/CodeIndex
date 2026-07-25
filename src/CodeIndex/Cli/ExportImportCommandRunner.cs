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
    private const string PhaseScopeArchive = "scope_archive";
    private const string PhaseReplaceDb = "replace_db";
    private const string PhaseWriteArchive = "write_archive";
    private const string PhaseWriteCtags = "write_ctags";
    private const string ImportUsage = "cdidx import <archive> [--db <path>] [--prune-paths] [--dry-run|--check] [--limit <n<=10000>] [--offset <n>] [--json]";
    private const string ArchiveExportUsage = "cdidx export <archive> [--db <path>] [--json] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--project <name|path>] [--solution <path>] [--exclude-tests]";
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

    public static int RunImport(string[] args, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken = default)
    {
        var parseResult = ParseImportArguments(args, jsonOptions);
        var importArguments = parseResult.Arguments;
        if (importArguments == null)
            return parseResult.ExitCode;

        var archivePath = importArguments.ArchivePath;
        var wantsJson = importArguments.WantsJson;
        var prunePaths = importArguments.PrunePaths;
        var importMode = importArguments.ImportMode;
        var dryRun = importArguments.DryRun;
        var limit = importArguments.Limit;
        var offset = importArguments.Offset;
        var dbPath = importArguments.DbPath
            ?? DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath;
        var fullDbPath = Path.GetFullPath(DbPathResolver.NormalizeDbPath(dbPath));
        var importTargetProjectRoot = ResolveImportTargetProjectRoot(fullDbPath);
        var dbDirectory = Path.GetDirectoryName(fullDbPath);
        if (string.IsNullOrWhiteSpace(dbDirectory))
            return WriteImportError(wantsJson, jsonOptions, PhaseParseArgs, "import_db_directory_unresolved", $"could not resolve destination DB directory for `{dbPath}`.", "pass an explicit `--db <path>`.", ImportUsage);

        string? tempDirectory = null;
        string? tempPath = null;
        ExportManifest? importedManifest = null;
        var validationPhases = new List<ImportValidationPhaseResult>();
        var phase = PhaseOpenArchive;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dryRun)
            {
                tempDirectory = DataDirectorySecurity.CreateSensitiveTempDirectory("codeindex-import-").FullName;
                tempPath = Path.Combine(tempDirectory, "codeindex.db");
            }
            else
            {
                Directory.CreateDirectory(dbDirectory);
                tempPath = Path.Combine(dbDirectory, $".codeindex-import-{Guid.NewGuid():N}.db");
            }

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddImportValidationPhase(validationPhases, PhaseOpenArchive);
                if (!TryValidateImportArchiveEntries(
                        archive,
                        out var manifestEntry,
                        out var dbEntry,
                        out var entryValidationPhase,
                        out var entryValidationErrorCode,
                        out var entryValidationMessage))
                {
                    return WriteImportError(
                        wantsJson,
                        jsonOptions,
                        entryValidationPhase,
                        entryValidationErrorCode,
                        entryValidationMessage,
                        "use an archive produced by `cdidx export <archive>`.",
                        ImportUsage);
                }

                phase = PhaseManifest;
                if (!TryReadManifest(manifestEntry, jsonOptions, out var manifest, out var manifestError, cancellationToken))
                    return WriteImportError(wantsJson, jsonOptions, PhaseManifest, "import_manifest_invalid", $"archive manifest is invalid: {manifestError}.", "use an archive produced by `cdidx export <archive>`.", ImportUsage);
                if (!ExportImportManifestCodec.TryValidateHeader(manifest, out var manifestHeaderError))
                    return WriteImportError(wantsJson, jsonOptions, PhaseManifest, "import_manifest_incompatible", $"archive manifest is invalid: {manifestHeaderError}.", "re-export from a compatible CodeIndex database.", ImportUsage);
                importedManifest = manifest;
                AddImportValidationPhase(validationPhases, PhaseManifest);

                phase = PhaseDatabaseEntry;
                if (dbEntry == null)
                    return WriteImportError(wantsJson, jsonOptions, PhaseDatabaseEntry, "import_database_entry_missing", $"archive is missing {DatabaseEntryName}.", "use an archive produced by `cdidx export <archive>`.", ImportUsage);
                if (!TryValidateDatabaseEntrySize(dbEntry.Length, dbEntry.CompressedLength, out var sizeValidationMessage))
                    return WriteImportError(wantsJson, jsonOptions, PhaseDatabaseEntry, "import_database_entry_too_large", sizeValidationMessage, "re-export a smaller CodeIndex database or rebuild a smaller index.", ImportUsage);

                ExtractDatabaseEntryToFile(dbEntry, tempPath, cancellationToken);
                AddImportValidationPhase(validationPhases, PhaseDatabaseEntry);

                phase = PhaseSha256;
                if (!TryValidateImportedManifest(manifest, tempPath, out var manifestValidationMessage, out var manifestValidationPhase, cancellationToken))
                    return WriteImportError(wantsJson, jsonOptions, manifestValidationPhase, "import_manifest_mismatch", $"archive manifest mismatch: {manifestValidationMessage}.", "re-export from a compatible CodeIndex database.", ImportUsage);
                AddImportValidationPhase(validationPhases, PhaseSha256);
            }

            phase = PhaseSqliteValidate;
            if (!DbContext.TryValidateExistingCodeIndexDb(
                    tempPath,
                    requireWritable: true,
                    requireSupportedUserVersion: false,
                    out var validationMessage,
                    out _,
                    out _,
                    cancellationToken))
                return WriteImportError(wantsJson, jsonOptions, PhaseSqliteValidate, "import_database_invalid", $"archive database is invalid: {validationMessage}.", "re-export from a compatible CodeIndex database.", ImportUsage);
            AddImportValidationPhase(validationPhases, PhaseSqliteValidate);
            SqliteConnection.ClearAllPools();

            if (prunePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                phase = PhasePrunePaths;
                RewriteImportedProjectRoot(tempPath, importTargetProjectRoot);
                AddImportValidationPhase(validationPhases, PhasePrunePaths);
                SqliteConnection.ClearAllPools();
            }

            if (dryRun)
            {
                phase = PhaseDestinationDelta;
                var destinationDelta = BuildImportDestinationDelta(
                    fullDbPath,
                    tempPath,
                    Path.GetFullPath(archivePath),
                    limit,
                    offset,
                    cancellationToken);
                AddImportValidationPhase(
                    validationPhases,
                    PhaseDestinationDelta,
                    destinationDelta.Comparable ? "success" : "unavailable",
                    destinationDelta.Message);
                AddImportValidationPhase(validationPhases, PhaseReplaceDb, "skipped", $"{importMode} mode does not replace the destination database");
                var manifest = importedManifest ?? throw new InvalidDataException("archive manifest was not loaded");
                return WriteImportDryRunResult(
                    importArguments,
                    jsonOptions,
                    fullDbPath,
                    importTargetProjectRoot,
                    validationPhases,
                    destinationDelta,
                    manifest);
            }

            phase = PhaseReplaceDb;
            ReplaceImportedDatabase(tempPath, fullDbPath, cancellationToken);
            AddImportValidationPhase(validationPhases, PhaseReplaceDb);
            return WriteImportResult(
                importArguments,
                jsonOptions,
                fullDbPath,
                importTargetProjectRoot,
                validationPhases,
                importedManifest ?? throw new InvalidDataException("archive manifest was not loaded"));
        }
        catch (OperationCanceledException)
        {
            return WriteImportError(
                wantsJson,
                jsonOptions,
                phase,
                CommandErrorCodes.Interrupted,
                "import cancelled before it could complete.",
                "retry `cdidx import` after the cancelling operation completes.",
                ImportUsage,
                CommandExitCodes.CancelledBySignal);
        }
        catch (ImportReplacementException ex)
        {
            return WriteImportError(
                wantsJson,
                jsonOptions,
                PhaseReplaceDb,
                "import_replacement_failed",
                $"import failed ({CommandErrorWriter.FormatSanitizedException(ex.InnerException ?? ex)}).",
                "check destination database permissions and inspect diagnostics for residual replacement state.",
                ImportUsage,
                diagnostics: ex.Diagnostics);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException)
        {
            return WriteImportError(
                wantsJson,
                jsonOptions,
                phase,
                "import_failed",
                $"import failed ({CommandErrorWriter.FormatSanitizedException(ex)}).",
                "check the archive path and destination database permissions.",
                ImportUsage,
                rootCause: ClassifyImportFailureRootCause(phase, ex));
        }
        finally
        {
            if (tempPath != null)
            {
                TryDeleteFile(tempPath, "import temporary database");
                DeleteSqliteSidecars(tempPath, "import temporary database sidecar");
            }
            if (tempDirectory != null)
                TryDeleteDirectoryIfEmpty(tempDirectory, "import temporary directory", Path.GetTempPath(), "codeindex-import-");
        }
    }

    private static ImportArgumentParseResult ParseImportArguments(
        string[] args,
        JsonSerializerOptions jsonOptions)
    {
        string? archivePath = null;
        string? dbPath = null;
        var wantsJson = Array.Exists(args, arg => arg == "--json");
        var prunePaths = false;
        var importMode = "import";
        var dryRun = false;
        var limit = DiffCommandRunner.DefaultDiffLimit;
        var offset = 0;
        var pagingOptionSpecified = false;

        ImportArgumentParseResult Fail(string errorCode, string message, string recommendedAction)
        {
            return new ImportArgumentParseResult(
                Arguments: null,
                WriteImportError(
                    wantsJson,
                    jsonOptions,
                    PhaseParseArgs,
                    errorCode,
                    message,
                    recommendedAction,
                    ImportUsage));
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                wantsJson = true;
                continue;
            }
            if (arg == "--prune-paths")
            {
                prunePaths = true;
                continue;
            }
            if (arg is "--dry-run" or "--check")
            {
                importMode = arg == "--check" ? "check" : "dry_run";
                dryRun = true;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--db", arg, out var dbValue, out var dbError))
            {
                if (dbError != null)
                    return Fail("import_db_requires_value", dbError, "use `cdidx import <archive> --db <path>`.");
                dbPath = dbValue;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--limit", arg, out var limitValue, out var limitError))
            {
                pagingOptionSpecified = true;
                if (limitError != null
                    || !int.TryParse(limitValue, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                    || limit < 0
                    || limit > DiffCommandRunner.MaxDiffLimit)
                {
                    return Fail(
                        "import_limit_invalid",
                        $"--limit requires an integer from 0 to {DiffCommandRunner.MaxDiffLimit}.",
                        "use `--limit 20` to bound destination delta samples.");
                }
                continue;
            }

            if (TryReadValueOption(args, ref i, "--offset", arg, out var offsetValue, out var offsetError))
            {
                pagingOptionSpecified = true;
                if (offsetError != null
                    || !int.TryParse(offsetValue, NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                    || offset < 0
                    || offset > int.MaxValue - limit)
                {
                    return Fail(
                        "import_offset_invalid",
                        "--offset requires a non-negative integer that can be combined with --limit.",
                        "use `--offset 0` for the first destination delta page.");
                }
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
                return Fail("import_unknown_option", $"unknown import option `{arg}`.", "use `cdidx import <archive> [--db <path>]`.");

            if (archivePath != null)
                return Fail("import_extra_archive_path", $"import accepts exactly one archive path, got extra `{arg}`.", "remove the extra argument.");
            archivePath = arg;
        }

        if (string.IsNullOrWhiteSpace(archivePath))
            return Fail("import_archive_required", "import requires an archive path.", "pass an archive produced by `cdidx export <archive>`.");
        if (pagingOptionSpecified && !dryRun)
            return Fail("import_paging_requires_dry_run", "--limit and --offset are only valid with --dry-run or --check.", "add `--dry-run` to preview bounded destination deltas.");
        if (offset > int.MaxValue - limit)
            return Fail("import_offset_invalid", "--offset is too large for the requested --limit.", "choose a lower --offset.");

        return new ImportArgumentParseResult(
            new ImportArguments(archivePath, dbPath, wantsJson, prunePaths, importMode, dryRun, limit, offset),
            CommandExitCodes.Success);
    }

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

    private static int RunExportArchive(string[] args, JsonSerializerOptions jsonOptions, string appVersion, CancellationToken cancellationToken)
    {
        string? outputPath = null;
        string? dbPath = null;
        string? lang = null;
        string? solution = null;
        var pathPatterns = new List<string>();
        var excludePathPatterns = new List<string>();
        var projects = new List<string>();
        var excludeTests = false;
        var wantsJson = Array.Exists(args, arg => arg == "--json");

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                wantsJson = true;
                continue;
            }
            if (arg == "--exclude-tests")
            {
                excludeTests = true;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--db", arg, out var dbValue, out var dbError))
            {
                if (dbError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_db_requires_value", dbError, "use `cdidx export <archive> --db <path>`.", ArchiveExportUsage);
                dbPath = dbValue;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--lang", arg, out var langValue, out var langError))
            {
                if (langError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_lang_requires_value", langError, "pass a language name such as `csharp`, `cs`, or `python`.", ArchiveExportUsage);
                lang = DbReader.NormalizeQueryLanguage(langValue);
                continue;
            }

            if (TryReadValueOption(args, ref i, "--path", arg, out var pathValue, out var pathError))
            {
                if (pathError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_path_requires_value", pathError, "pass a path substring or glob such as `src/` or `src/*.cs`.", ArchiveExportUsage);
                pathPatterns.Add(pathValue!);
                continue;
            }

            if (TryReadValueOption(args, ref i, "--exclude-path", arg, out var excludePathValue, out var excludePathError))
            {
                if (excludePathError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_exclude_path_requires_value", excludePathError, "pass a path substring or glob to omit.", ArchiveExportUsage);
                excludePathPatterns.Add(excludePathValue!);
                continue;
            }

            if (TryReadValueOption(args, ref i, "--project", arg, out var projectValue, out var projectError))
            {
                if (projectError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_project_requires_value", projectError, "pass a project name or project path.", ArchiveExportUsage);
                projects.Add(projectValue!);
                continue;
            }

            if (TryReadValueOption(args, ref i, "--solution", arg, out var solutionValue, out var solutionError))
            {
                if (solutionError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_solution_requires_value", solutionError, "pass a solution path used to resolve project names.", ArchiveExportUsage);
                solution = solutionValue;
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
                return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_unknown_option", $"unknown export option `{arg}`.", "use archive scope flags or `cdidx export ctags`.", ArchiveExportUsage);

            if (outputPath != null)
                return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_extra_archive_path", $"export accepts exactly one archive path, got extra `{arg}`.", "remove the extra argument.", ArchiveExportUsage);
            outputPath = arg;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_archive_required", "export requires an output archive path.", "pass a destination such as `codeindex.cdidx.zip`, or use `cdidx export ctags`.", ArchiveExportUsage);
        if (solution != null && projects.Count == 0)
            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_solution_requires_project", "--solution requires at least one --project filter.", "add `--project <name|path>` or remove `--solution`.", ArchiveExportUsage);
        if (!TryValidateArchiveScopeValues(pathPatterns, excludePathPatterns, projects, solution, out var scopeValidationMessage))
            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_scope_invalid", scopeValidationMessage, "reduce or shorten the archive scope values.", ArchiveExportUsage);

        dbPath ??= DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath;
        var normalizedDbPath = DbPathResolver.NormalizeDbPath(dbPath);
        if (!DbContext.TryValidateExistingCodeIndexDb(
                normalizedDbPath,
                requireWritable: false,
                requireSupportedUserVersion: false,
                out var validationMessage,
                out _,
                out _))
            return WriteExportError(wantsJson, jsonOptions, PhaseSqliteValidate, "export_database_invalid", validationMessage, "run `cdidx index <projectPath>` first or pass `--db <path>`.", ArchiveExportUsage);

        var fullSourceDbPath = Path.GetFullPath(normalizedDbPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (IsDatabaseOrSqliteSidecarPath(fullOutputPath, fullSourceDbPath))
        {
            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_archive_overlaps_database", "export archive path must not be the source database or a SQLite sidecar.", "choose a separate archive path, for example `codeindex.cdidx.zip`.", ArchiveExportUsage);
        }

        var scopeOptions = new ArchiveExportOptions(
            lang,
            pathPatterns.ToArray(),
            excludePathPatterns.ToArray(),
            projects.ToArray(),
            solution,
            excludeTests);
        string? snapshotDirectory = null;
        string? snapshotPath = null;
        var phase = PhaseWriteArchive;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshotDirectory = DataDirectorySecurity.CreateSensitiveTempDirectory("codeindex-export-").FullName;
            snapshotPath = Path.Combine(snapshotDirectory, "codeindex.db");
            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            phase = PhaseSqliteValidate;
            CreateDatabaseSnapshot(normalizedDbPath, snapshotPath, cancellationToken);
            ExportManifest manifest;
            if (scopeOptions.IsScoped)
            {
                using var snapshotContext = new DbContext(DbOpenIntent.Migration, snapshotPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                snapshotContext.TryMigrateForRead();
                if (snapshotContext.LastMigrationFailure is { } migrationFailure)
                {
                    throw new InvalidDataException(
                        $"export snapshot schema migration failed at {migrationFailure.Step}: {migrationFailure.SqliteMessage}");
                }
                phase = PhaseScopeArchive;
                var snapshotConnection = snapshotContext.Connection;
                var scope = ApplyArchiveScope(snapshotConnection, scopeOptions, cancellationToken);
                manifest = BuildManifest(snapshotConnection, appVersion, scope, cancellationToken);
            }
            else
            {
                using var snapshotConnection = new SqliteConnection(CreateUnpooledConnectionString(snapshotPath));
                cancellationToken.ThrowIfCancellationRequested();
                snapshotConnection.Open();
                phase = PhaseScopeArchive;
                var scope = ApplyArchiveScope(snapshotConnection, scopeOptions, cancellationToken);
                manifest = BuildManifest(snapshotConnection, appVersion, scope, cancellationToken);
            }
            SqliteConnection.ClearAllPools();
            phase = PhaseSha256;
            manifest = manifest with { DatabaseSha256 = ComputeSha256(snapshotPath, cancellationToken) };
            phase = PhaseWriteArchive;
            WriteExportArchiveFile(fullOutputPath, snapshotPath, manifest, jsonOptions, cancellationToken);

            if (wantsJson)
                Console.WriteLine(JsonSerializer.Serialize(
                    new ExportArchiveResult(
                        "1",
                        fullOutputPath,
                        fullSourceDbPath,
                        manifest.Scope ?? throw new InvalidDataException("export scope metadata was not created")),
                    jsonOptions));
            else
                Console.WriteLine($"Exported CodeIndex archive to {fullOutputPath}");
            return CommandExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return WriteExportError(
                wantsJson,
                jsonOptions,
                phase,
                CommandErrorCodes.Interrupted,
                "export cancelled before it could complete.",
                "retry `cdidx export` after the cancelling operation completes.",
                ArchiveExportUsage,
                CommandExitCodes.CancelledBySignal);
        }
        catch (Exception ex)
        {
            return WriteExportError(wantsJson, jsonOptions, phase, "export_failed", $"export failed ({CommandErrorWriter.FormatSanitizedException(ex)}).", "check the database, scope, project, and output archive paths.", ArchiveExportUsage);
        }
        finally
        {
            if (snapshotPath != null)
            {
                TryDeleteFile(snapshotPath, "export temporary database");
                DeleteSqliteSidecars(snapshotPath, "export temporary database sidecar");
            }
            if (snapshotDirectory != null)
                TryDeleteDirectoryIfEmpty(snapshotDirectory, "export temporary directory", Path.GetTempPath(), "codeindex-export-");
        }
    }

    private static int RunExportCtags(string[] args, JsonSerializerOptions jsonOptions)
    {
        var outputPath = "tags";
        string? dbPath = null;
        string? lang = null;
        var pathPatterns = new List<string>();
        var excludePathPatterns = new List<string>();
        var excludeTests = false;
        var includeGenerated = false;
        var wantsJson = Array.Exists(args, arg => arg == "--json");

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                wantsJson = true;
                continue;
            }
            if (arg == "--exclude-tests")
            {
                excludeTests = true;
                continue;
            }
            if (arg == "--include-generated")
            {
                includeGenerated = true;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--output", arg, out var outputValue, out var outputError))
            {
                if (outputError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "ctags_export_output_requires_value", outputError, "use `cdidx export ctags --output tags`.", CtagsExportUsage);
                outputPath = outputValue!;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--db", arg, out var dbValue, out var dbError))
            {
                if (dbError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "ctags_export_db_requires_value", dbError, "use `cdidx export ctags --db <path>`.", CtagsExportUsage);
                dbPath = dbValue;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--lang", arg, out var langValue, out var langError))
            {
                if (langError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "ctags_export_lang_requires_value", langError, "pass a language name such as `csharp`, `cs`, or `python`.", CtagsExportUsage);
                lang = DbReader.NormalizeQueryLanguage(langValue);
                continue;
            }

            if (TryReadValueOption(args, ref i, "--path", arg, out var pathValue, out var pathError))
            {
                if (pathError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "ctags_export_path_requires_value", pathError, "pass a path substring or glob such as `src/` or `src/*.cs`.", CtagsExportUsage);
                pathPatterns.Add(pathValue!);
                continue;
            }

            if (TryReadValueOption(args, ref i, "--exclude-path", arg, out var excludePathValue, out var excludePathError))
            {
                if (excludePathError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "ctags_export_exclude_path_requires_value", excludePathError, "pass a path substring or glob to omit.", CtagsExportUsage);
                excludePathPatterns.Add(excludePathValue!);
                continue;
            }

            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "ctags_export_unknown_option", $"unknown ctags export option `{arg}`.", "use `--output`, `--db`, `--json`, or filter flags such as `--include-generated`.", CtagsExportUsage);
        }

        dbPath ??= DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath;
        var normalizedDbPath = DbPathResolver.NormalizeDbPath(dbPath);
        var fullSourceDbPath = Path.GetFullPath(normalizedDbPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (IsDatabaseOrSqliteSidecarPath(fullOutputPath, fullSourceDbPath))
        {
            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "ctags_export_output_overlaps_database", "ctags output path must not be the source database or a SQLite sidecar.", "choose a separate tags path, for example `tags`.", CtagsExportUsage);
        }

        if (!DbContext.TryValidateExistingCodeIndexDb(
                normalizedDbPath,
                requireWritable: false,
                requireSupportedUserVersion: false,
                out var validationMessage,
                out _,
                out _))
            return WriteExportError(wantsJson, jsonOptions, PhaseSqliteValidate, "ctags_export_database_invalid", validationMessage, "run `cdidx index <projectPath>` first or pass `--db <path>`.", CtagsExportUsage);

        try
        {
            using var db = new DbContext(DbOpenIntent.QueryOnly, normalizedDbPath);
            var generatedFileFilterAvailable = DbSchemaCache.LoadColumns(db.Connection, "files").Contains("generated");
            var filters = new CtagsExportOptions(
                lang,
                pathPatterns.ToArray(),
                excludePathPatterns.ToArray(),
                excludeTests,
                includeGenerated,
                generatedFileFilterAvailable);
            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            long emittedCount = 0;
            var skipReasonCounts = wantsJson
                ? CountCtagsSkipReasons(db.Connection, filters)
                : null;
            WriteCtagsFile(fullOutputPath, writer =>
            {
                writer.WriteLine("!_TAG_FILE_FORMAT\t2\t/extended format/");
                writer.WriteLine("!_TAG_FILE_SORTED\t1\t/0=unsorted, 1=sorted, 2=foldcase/");

                using var cmd = CreateCtagsSymbolCommand(db.Connection, filters);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = SanitizeCtagsField(reader.GetString(0));
                    var path = SanitizeCtagsField(reader.GetString(1));
                    var line = Math.Max(1, reader.GetInt32(2));
                    var kind = SanitizeCtagsField(reader.GetString(3));
                    var tagLine = new StringBuilder()
                        .Append(name)
                        .Append('\t')
                        .Append(path)
                        .Append('\t')
                        .Append(line.ToString(CultureInfo.InvariantCulture))
                        .Append(";\"\tkind:")
                        .Append(kind)
                        .Append("\tline:")
                        .Append(line.ToString(CultureInfo.InvariantCulture));
                    AppendCtagsExtensionField(tagLine, "language", ExportImportSqliteRow.ReadNullableString(reader, 4));
                    AppendCtagsExtensionField(tagLine, "container_kind", ExportImportSqliteRow.ReadNullableString(reader, 5));
                    AppendCtagsExtensionField(tagLine, "container", ExportImportSqliteRow.ReadNullableString(reader, 6));
                    AppendCtagsExtensionField(tagLine, "visibility", ExportImportSqliteRow.ReadNullableString(reader, 7));
                    writer.WriteLine(tagLine.ToString());
                    emittedCount++;
                }
            });

            if (wantsJson)
            {
                var skippedCount = skipReasonCounts!.Values.Sum();
                var totalTagCount = emittedCount + skippedCount;
                var result = new CtagsExportResult(
                    "1",
                    "success",
                    fullOutputPath,
                    fullSourceDbPath,
                    totalTagCount,
                    emittedCount,
                    skippedCount,
                    skipReasonCounts,
                    new CtagsExportFilterResult(
                        filters.Lang,
                        filters.PathPatterns,
                        filters.ExcludePathPatterns,
                        filters.ExcludeTests,
                        filters.IncludeGenerated,
                        filters.IncludeGenerated
                            ? "include"
                            : filters.GeneratedFileFilterAvailable
                                ? "exclude"
                                : "unavailable",
                        filters.GeneratedFileFilterAvailable),
                    ["kind", "line", "language", "container_kind", "container", "visibility"]);
                Console.WriteLine(JsonSerializer.Serialize(
                    result,
                    CliJsonSerializerContextFactory.Create(jsonOptions).CtagsExportResult));
            }
            else
            {
                Console.WriteLine($"Exported ctags to {fullOutputPath}");
            }
            return CommandExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return WriteExportError(wantsJson, jsonOptions, PhaseWriteCtags, "ctags_export_failed", $"ctags export failed ({CommandErrorWriter.FormatSanitizedException(ex)}).", "check the database and output paths.", CtagsExportUsage);
        }
    }

}
