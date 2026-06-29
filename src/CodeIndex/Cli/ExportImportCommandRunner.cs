using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static class ExportImportCommandRunner
{
    private const string ManifestEntryName = "manifest.json";
    private const string DatabaseEntryName = "codeindex.db";
    private static readonly string[] ExpectedImportArchiveEntryNames = [ManifestEntryName, DatabaseEntryName];
    internal const int MaxImportManifestBytes = 64 * 1024;
    internal const int MaxImportManifestJsonDepth = 16;
    internal const long MaxImportDatabaseBytes = 8L * 1024 * 1024 * 1024;
    internal const long MaxImportDatabaseCompressionRatio = 1000;
    private const int ImportCopyBufferSize = 81920;
    private const int ManifestUnknownExtensionFileLimit = DbContext.UnknownExtensionFilePathSampleLimit;
    private const int ManifestUnknownExtensionJsonDepth = 4;
    internal const int ManifestUnknownExtensionDecodedItemLimit = ManifestUnknownExtensionFileLimit;
    internal const int ManifestUnknownExtensionPathCharLimit = 4096;
    private const int ManifestUnknownExtensionFilesTotalCharLimit = 32 * 1024;
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
    private const string PhaseReplaceDb = "replace_db";
    private const string PhaseWriteArchive = "write_archive";
    private const string PhaseWriteCtags = "write_ctags";
    private const string ImportUsage = "cdidx import <archive> [--db <path>] [--prune-paths] [--dry-run|--check] [--json]";
    private const string CtagsExportUsage = "cdidx export ctags [--output <path>] [--db <path>] [--json] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests]";

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
        string? archivePath = null;
        string? dbPath = null;
        var wantsJson = Array.Exists(args, arg => arg == "--json");
        var prunePaths = false;
        var dryRun = false;

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
                dryRun = true;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--db", arg, out var dbValue, out var dbError))
            {
                if (dbError != null)
                    return WriteImportError(wantsJson, jsonOptions, PhaseParseArgs, "import_db_requires_value", dbError, "use `cdidx import <archive> --db <path>`.", ImportUsage);
                dbPath = dbValue;
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
                return WriteImportError(wantsJson, jsonOptions, PhaseParseArgs, "import_unknown_option", $"unknown import option `{arg}`.", "use `cdidx import <archive> [--db <path>]`.", ImportUsage);

            if (archivePath != null)
                return WriteImportError(wantsJson, jsonOptions, PhaseParseArgs, "import_extra_archive_path", $"import accepts exactly one archive path, got extra `{arg}`.", "remove the extra argument.", ImportUsage);
            archivePath = arg;
        }

        if (string.IsNullOrWhiteSpace(archivePath))
            return WriteImportError(wantsJson, jsonOptions, PhaseParseArgs, "import_archive_required", "import requires an archive path.", "pass an archive produced by `cdidx export <archive>`.", ImportUsage);

        dbPath ??= DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath;
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
                if (!TryValidateManifestHeader(manifest, out var manifestHeaderError))
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
            if (!DbContext.TryValidateExistingCodeIndexDb(tempPath, out var validationMessage, out _, cancellationToken: cancellationToken))
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
                AddImportValidationPhase(validationPhases, PhaseReplaceDb, "skipped", "dry-run does not replace the destination database");
                var manifest = importedManifest ?? throw new InvalidDataException("archive manifest was not loaded");
                if (wantsJson)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new ImportDryRunResult(
                            "1",
                            "success",
                            Path.GetFullPath(archivePath),
                            fullDbPath,
                            dryRun,
                            prunePaths,
                            prunePaths ? importTargetProjectRoot : null,
                            ReplacementWouldBeAllowed: true,
                            validationPhases,
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
                        $"Validated CodeIndex archive {Path.GetFullPath(archivePath)}; replacement would be allowed for {fullDbPath}",
                        prunePaths,
                        importTargetProjectRoot));
                }

                return CommandExitCodes.Success;
            }

            phase = PhaseReplaceDb;
            ReplaceImportedDatabase(tempPath, fullDbPath, cancellationToken);
            if (wantsJson)
            {
                var manifest = importedManifest ?? throw new InvalidDataException("archive manifest was not loaded");
                Console.WriteLine(JsonSerializer.Serialize(
                    new ImportResult(
                        "1",
                        fullDbPath,
                        prunePaths,
                        prunePaths ? importTargetProjectRoot : null,
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
                    prunePaths,
                    importTargetProjectRoot));
            }
            return CommandExitCodes.Success;
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
            return WriteImportError(wantsJson, jsonOptions, phase, "import_failed", $"import failed ({CommandErrorWriter.FormatSanitizedException(ex)}).", "check the archive path and destination database permissions.", ImportUsage);
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

    private static int RunExportArchive(string[] args, JsonSerializerOptions jsonOptions, string appVersion, CancellationToken cancellationToken)
    {
        string? outputPath = null;
        string? dbPath = null;
        var wantsJson = Array.Exists(args, arg => arg == "--json");

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                wantsJson = true;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--db", arg, out var dbValue, out var dbError))
            {
                if (dbError != null)
                    return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_db_requires_value", dbError, "use `cdidx export <archive> --db <path>`.", "cdidx export <archive> [--db <path>] [--json]");
                dbPath = dbValue;
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
                return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_unknown_option", $"unknown export option `{arg}`.", "use `cdidx export <archive> [--db <path>]` or `cdidx export ctags`.", "cdidx export <archive> [--db <path>] [--json]");

            if (outputPath != null)
                return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_extra_archive_path", $"export accepts exactly one archive path, got extra `{arg}`.", "remove the extra argument.", "cdidx export <archive> [--db <path>] [--json]");
            outputPath = arg;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_archive_required", "export requires an output archive path.", "pass a destination such as `codeindex.cdidx.zip`, or use `cdidx export ctags`.", "cdidx export <archive> [--db <path>] [--json]");

        dbPath ??= DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath;
        var normalizedDbPath = DbPathResolver.NormalizeDbPath(dbPath);
        if (!DbContext.TryValidateExistingCodeIndexDb(normalizedDbPath, out var validationMessage, out _))
            return WriteExportError(wantsJson, jsonOptions, PhaseSqliteValidate, "export_database_invalid", validationMessage, "run `cdidx index <projectPath>` first or pass `--db <path>`.", "cdidx export <archive> [--db <path>] [--json]");

        var fullSourceDbPath = Path.GetFullPath(normalizedDbPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (IsDatabaseOrSqliteSidecarPath(fullOutputPath, fullSourceDbPath))
        {
            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_archive_overlaps_database", "export archive path must not be the source database or a SQLite sidecar.", "choose a separate archive path, for example `codeindex.cdidx.zip`.", "cdidx export <archive> [--db <path>] [--json]");
        }

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
            using (var snapshotConnection = new SqliteConnection(CreateUnpooledConnectionString(snapshotPath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshotConnection.Open();
                manifest = BuildManifest(snapshotConnection, appVersion, cancellationToken);
            }
            SqliteConnection.ClearAllPools();
            phase = PhaseSha256;
            manifest = manifest with { DatabaseSha256 = ComputeSha256(snapshotPath, cancellationToken) };
            phase = PhaseWriteArchive;
            WriteExportArchiveFile(fullOutputPath, snapshotPath, manifest, jsonOptions, cancellationToken);

            if (wantsJson)
                Console.WriteLine(JsonSerializer.Serialize(new ExportArchiveResult("1", fullOutputPath, fullSourceDbPath), jsonOptions));
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
                "cdidx export <archive> [--db <path>] [--json]",
                CommandExitCodes.CancelledBySignal);
        }
        catch (Exception ex)
        {
            return WriteExportError(wantsJson, jsonOptions, phase, "export_failed", $"export failed ({CommandErrorWriter.FormatSanitizedException(ex)}).", "check the database and output archive paths.", "cdidx export <archive> [--db <path>] [--json]");
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

            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "ctags_export_unknown_option", $"unknown ctags export option `{arg}`.", "use `--output`, `--db`, `--json`, or filter flags.", CtagsExportUsage);
        }

        dbPath ??= DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath;
        var normalizedDbPath = DbPathResolver.NormalizeDbPath(dbPath);
        var fullSourceDbPath = Path.GetFullPath(normalizedDbPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (IsDatabaseOrSqliteSidecarPath(fullOutputPath, fullSourceDbPath))
        {
            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "ctags_export_output_overlaps_database", "ctags output path must not be the source database or a SQLite sidecar.", "choose a separate tags path, for example `tags`.", CtagsExportUsage);
        }

        if (!DbContext.TryValidateExistingCodeIndexDb(normalizedDbPath, out var validationMessage, out _))
            return WriteExportError(wantsJson, jsonOptions, PhaseSqliteValidate, "ctags_export_database_invalid", validationMessage, "run `cdidx index <projectPath>` first or pass `--db <path>`.", CtagsExportUsage);

        var filters = new CtagsExportOptions(lang, pathPatterns.ToArray(), excludePathPatterns.ToArray(), excludeTests);
        try
        {
            using var db = new DbContext(normalizedDbPath);
            db.TryMigrateForRead();
            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            var totalTagCount = CountCtagsSymbols(db.Connection, CtagsExportOptions.Unfiltered);
            long emittedCount = 0;
            WriteCtagsFile(fullOutputPath, writer =>
            {
                writer.WriteLine("!_TAG_FILE_FORMAT\t2\t/extended format/");
                writer.WriteLine("!_TAG_FILE_SORTED\t1\t/0=unsorted, 1=sorted, 2=foldcase/");

                using var cmd = CreateCtagsSymbolCommand(db.Connection, filters, countOnly: false);
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
                    AppendCtagsExtensionField(tagLine, "language", GetNullableString(reader, 4));
                    AppendCtagsExtensionField(tagLine, "container_kind", GetNullableString(reader, 5));
                    AppendCtagsExtensionField(tagLine, "container", GetNullableString(reader, 6));
                    AppendCtagsExtensionField(tagLine, "visibility", GetNullableString(reader, 7));
                    writer.WriteLine(tagLine.ToString());
                    emittedCount++;
                }
            });

            if (wantsJson)
            {
                var skippedCount = Math.Max(0, totalTagCount - emittedCount);
                var result = new CtagsExportResult(
                    "1",
                    "success",
                    fullOutputPath,
                    fullSourceDbPath,
                    totalTagCount,
                    emittedCount,
                    skippedCount,
                    new CtagsExportFilterResult(filters.Lang, filters.PathPatterns, filters.ExcludePathPatterns, filters.ExcludeTests),
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

    private static SqliteCommand CreateCtagsSymbolCommand(SqliteConnection connection, CtagsExportOptions filters, bool countOnly)
    {
        var cmd = connection.CreateCommand();
        var sql = countOnly
            ? @"
                SELECT COUNT(*)
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE s.name IS NOT NULL AND s.name != ''"
            : @"
                SELECT
                    s.name,
                    f.path,
                    COALESCE(s.start_line, s.line, 1),
                    s.kind,
                    f.lang,
                    s.container_kind,
                    s.container_name,
                    s.visibility
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE s.name IS NOT NULL AND s.name != ''";
        AppendCtagsFilters(ref sql, filters);
        if (!countOnly)
            sql += " ORDER BY s.name COLLATE NOCASE, f.path, COALESCE(s.start_line, s.line, 1)";
        cmd.CommandText = sql;
        AddCtagsFilterParameters(cmd, filters);
        return cmd;
    }

    private static long CountCtagsSymbols(SqliteConnection connection, CtagsExportOptions filters)
    {
        using var cmd = CreateCtagsSymbolCommand(connection, filters, countOnly: true);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AppendCtagsFilters(ref string sql, CtagsExportOptions filters)
    {
        if (!string.IsNullOrWhiteSpace(filters.Lang))
            sql += " AND f.lang = @lang";

        if (filters.PathPatterns.Count > 0)
        {
            var ors = new List<string>(filters.PathPatterns.Count);
            for (var i = 0; i < filters.PathPatterns.Count; i++)
                ors.Add(DbReader.BuildPathFilterPredicate("f", "pathPattern", i, filters.PathPatterns[i]));
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }

        for (var i = 0; i < filters.ExcludePathPatterns.Count; i++)
            sql += $" AND NOT {DbReader.BuildPathFilterPredicate("f", "excludePathPattern", i, filters.ExcludePathPatterns[i])}";

        if (filters.ExcludeTests)
            sql += $" AND NOT {DbReader.TestPathCondition}";
    }

    private static void AddCtagsFilterParameters(SqliteCommand cmd, CtagsExportOptions filters)
    {
        if (!string.IsNullOrWhiteSpace(filters.Lang))
            SqliteCommandPolicy.Add(cmd, "@lang", filters.Lang);

        DbReader.AddPathFilterParameterSet(cmd, "pathPattern", filters.PathPatterns);
        DbReader.AddPathFilterParameterSet(cmd, "excludePathPattern", filters.ExcludePathPatterns);
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AppendCtagsExtensionField(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder
            .Append('\t')
            .Append(name)
            .Append(':')
            .Append(SanitizeCtagsField(value));
    }

    private static ExportManifest BuildManifest(SqliteConnection connection, string appVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userVersion = ReadSqliteUserVersion(connection);
        var projectRoot = ReadMetaString(connection, DbContext.IndexedProjectRootMetaKey);
        var indexedHead = ReadMetaString(connection, DbContext.IndexedHeadShaMetaKey);
        var unknownExtensionFiles = ReadUnknownExtensionFileSample(connection);
        cancellationToken.ThrowIfCancellationRequested();
        return new ExportManifest(
            "1",
            appVersion,
            userVersion,
            projectRoot,
            indexedHead,
            string.Empty,
            FileCount: ReadTableCount(connection, "files", cancellationToken),
            ChunkCount: ReadTableCount(connection, "chunks", cancellationToken),
            SymbolCount: ReadTableCount(connection, "symbols", cancellationToken),
            ReferenceCount: ReadTableCount(connection, "symbol_references", cancellationToken),
            GraphReady: (userVersion & DbContext.GraphReadyFlag) != 0,
            IssuesReady: (userVersion & DbContext.IssuesReadyFlag) != 0,
            FoldReady: (userVersion & DbContext.FoldReadyFlag) != 0,
            IndexWriterVersion: ReadMetaString(connection, DbContext.CdidxWriterVersionMetaKey),
            IndexedHeadBranch: ReadMetaString(connection, DbContext.IndexedHeadBranchMetaKey),
            IndexedHeadTimestamp: ReadMetaString(connection, DbContext.IndexedHeadTimestampMetaKey),
            CodeIndexMetaSchemaVersion: ReadMetaInt(connection, DbContext.CodeIndexMetaSchemaVersionMetaKey),
            CSharpSymbolNameContractVersion: ReadMetaInt(connection, DbContext.CSharpSymbolNameContractVersionMetaKey),
            SqlGraphContractVersion: ReadMetaInt(connection, DbContext.SqlGraphContractVersionMetaKey),
            HotspotFamilyVersion: ReadMetaInt(connection, DbContext.HotspotFamilyVersionMetaKey),
            UnknownExtensionFileCount: ReadMetaLong(connection, DbContext.UnknownExtensionFileCountMetaKey),
            UnknownExtensionFiles: unknownExtensionFiles.Files,
            UnknownExtensionFilesTruncated: ReadMetaBool(connection, DbContext.UnknownExtensionFilesTruncatedMetaKey),
            UnknownExtensionFilePathLimit: ReadMetaInt(connection, DbContext.UnknownExtensionFilePathLimitMetaKey),
            UnknownExtensionFileSampleCount: unknownExtensionFiles.Count,
            UnknownExtensionFileSampleLimit: unknownExtensionFiles.Limit,
            UnknownExtensionFileSampleTruncated: unknownExtensionFiles.Truncated);
    }

    private static void AddTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        entry.LastWriteTime = DeterministicZipTimestamp;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    internal static void WriteExportArchiveFile(string outputPath, string snapshotPath, ExportManifest manifest, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        AtomicFileWriter.Write(
            fullOutputPath,
            stream =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                AddTextEntry(archive, ManifestEntryName, JsonSerializer.Serialize(manifest, jsonOptions));
                var dbEntry = archive.CreateEntry(DatabaseEntryName, CompressionLevel.SmallestSize);
                dbEntry.LastWriteTime = DeterministicZipTimestamp;
                using var source = BoundedFile.OpenReadTrustedArchiveSource(snapshotPath);
                using var target = dbEntry.Open();
                CopyToExactLength(source, target, source.Length, DatabaseEntryName, cancellationToken);
            });
    }

    internal static void WriteCtagsFile(string outputPath, Action<TextWriter> writeContents)
    {
        ArgumentNullException.ThrowIfNull(writeContents);

        var fullOutputPath = Path.GetFullPath(outputPath);
        AtomicFileWriter.Write(
            fullOutputPath,
            stream =>
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true);
                writeContents(writer);
            });
    }

    private static string ComputeSha256(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = BoundedFile.OpenReadForHash(path);
        return Sha256StreamHasher.ComputeHex(stream, cancellationToken);
    }

    private static bool TryValidateImportArchiveEntries(
        ZipArchive archive,
        out ZipArchiveEntry manifestEntry,
        out ZipArchiveEntry? databaseEntry,
        out string phase,
        out string errorCode,
        out string message)
    {
        manifestEntry = null!;
        databaseEntry = null!;
        phase = PhaseOpenArchive;
        errorCode = string.Empty;
        message = string.Empty;

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!IsExpectedImportArchiveEntryName(entry.FullName))
            {
                errorCode = "import_archive_unexpected_entry";
                message = $"archive contains unexpected entry {ConsoleUi.FormatBoundedValue(entry.FullName)}; expected only {FormatExpectedImportArchiveEntryNames()}.";
                return false;
            }

            if (entries.ContainsKey(entry.FullName))
            {
                phase = GetImportArchiveEntryPhase(entry.FullName);
                errorCode = "import_archive_duplicate_entry";
                message = $"archive contains duplicate entry {ConsoleUi.FormatBoundedValue(entry.FullName)}.";
                return false;
            }

            entries.Add(entry.FullName, entry);
        }

        if (!entries.TryGetValue(ManifestEntryName, out var foundManifestEntry))
        {
            phase = PhaseManifest;
            errorCode = "import_manifest_missing";
            message = $"archive is missing {ManifestEntryName}.";
            return false;
        }

        manifestEntry = foundManifestEntry;
        entries.TryGetValue(DatabaseEntryName, out databaseEntry);
        return true;
    }

    private static bool IsExpectedImportArchiveEntryName(string name)
        => Array.Exists(ExpectedImportArchiveEntryNames, expected => string.Equals(expected, name, StringComparison.Ordinal));

    private static string GetImportArchiveEntryPhase(string name)
        => string.Equals(name, ManifestEntryName, StringComparison.Ordinal)
            ? PhaseManifest
            : string.Equals(name, DatabaseEntryName, StringComparison.Ordinal)
                ? PhaseDatabaseEntry
                : PhaseOpenArchive;

    private static string FormatExpectedImportArchiveEntryNames()
        => string.Join(", ", ExpectedImportArchiveEntryNames.Select(name => $"`{name}`"));

    internal static string FormatImportManifestReadException(Exception ex)
        => CommandErrorWriter.FormatSanitizedException(ex);

    private static bool TryReadManifest(ZipArchiveEntry manifestEntry, JsonSerializerOptions jsonOptions, out ExportManifest manifest, out string message, CancellationToken cancellationToken)
    {
        if (!TryValidateManifestEntrySize(manifestEntry, out message))
        {
            manifest = null!;
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = manifestEntry.Open();
            using var manifestBytes = new MemoryStream((int)Math.Min(Math.Max(manifestEntry.Length, 0), MaxImportManifestBytes));
            CopyToWithLimit(stream, manifestBytes, MaxImportManifestBytes, ManifestEntryName, cancellationToken);
            manifestBytes.Position = 0;
            cancellationToken.ThrowIfCancellationRequested();
            if (JsonExceedsDepthLimit(manifestBytes.GetBuffer().AsSpan(0, (int)manifestBytes.Length), MaxImportManifestJsonDepth))
            {
                manifest = null!;
                message = $"manifest.json exceeds the JSON depth limit of {MaxImportManifestJsonDepth}";
                return false;
            }

            var parsedManifest = BoundedJson.Deserialize<ExportManifest>(
                manifestBytes.GetBuffer().AsSpan(0, (int)manifestBytes.Length),
                MaxImportManifestBytes,
                CreateImportManifestJsonOptions(jsonOptions));
            if (parsedManifest == null)
            {
                manifest = null!;
                message = "manifest.json did not contain an object";
                return false;
            }

            manifest = parsedManifest;
            message = string.Empty;
            return true;
        }
        catch (InvalidDataException ex)
        {
            manifest = null!;
            message = FormatImportManifestReadException(ex);
            return false;
        }
        catch (JsonException)
        {
            manifest = null!;
            message = "manifest.json is not valid export manifest JSON";
            return false;
        }
        catch (NotSupportedException)
        {
            manifest = null!;
            message = "manifest.json contains unsupported export manifest JSON";
            return false;
        }
    }

    private static bool TryValidateManifestEntrySize(ZipArchiveEntry manifestEntry, out string message)
    {
        if (manifestEntry.Length < 0 || manifestEntry.CompressedLength < 0)
        {
            message = "archive manifest.json size metadata is invalid";
            return false;
        }

        if (manifestEntry.Length > MaxImportManifestBytes)
        {
            message = $"archive manifest.json is too large: {ConsoleUi.FormatBytes(manifestEntry.Length)} uncompressed exceeds the import limit of {ConsoleUi.FormatBytes(MaxImportManifestBytes)}";
            return false;
        }

        if (manifestEntry.CompressedLength > MaxImportManifestBytes)
        {
            message = $"archive manifest.json is too large: {ConsoleUi.FormatBytes(manifestEntry.CompressedLength)} compressed exceeds the import limit of {ConsoleUi.FormatBytes(MaxImportManifestBytes)}";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static JsonSerializerOptions CreateImportManifestJsonOptions(JsonSerializerOptions jsonOptions)
        => new(jsonOptions) { MaxDepth = MaxImportManifestJsonDepth };

    private static bool JsonExceedsDepthLimit(ReadOnlySpan<byte> json, int maxDepth)
    {
        var depth = 0;
        var inString = false;

        for (var i = 0; i < json.Length; i++)
        {
            var value = json[i];
            if (inString)
            {
                if (value == (byte)'\\')
                {
                    i++;
                    continue;
                }

                if (value == (byte)'"')
                    inString = false;
                continue;
            }

            if (value == (byte)'"')
            {
                inString = true;
                continue;
            }

            if (value is (byte)'{' or (byte)'[')
            {
                depth++;
                if (depth > maxDepth)
                    return true;
                continue;
            }

            if (value is (byte)'}' or (byte)']')
                depth = Math.Max(0, depth - 1);
        }

        return false;
    }

    private static bool TryValidateManifestHeader(ExportManifest manifest, out string message)
    {
        if (!string.Equals(manifest.FormatVersion, "1", StringComparison.Ordinal))
        {
            message = $"unsupported format_version `{manifest.FormatVersion}`";
            return false;
        }

        if (manifest.UserVersion < 0 || (manifest.UserVersion & ~DbContext.CurrentSchemaVersion) != 0)
        {
            message = $"unsupported user_version `{manifest.UserVersion}`";
            return false;
        }

        if (!IsSha256Hex(manifest.DatabaseSha256))
        {
            message = "database_sha256 is missing or invalid";
            return false;
        }

        if (!ValidateNonNegativeManifestLong(manifest.FileCount, "file_count", out message)
            || !ValidateNonNegativeManifestLong(manifest.ChunkCount, "chunk_count", out message)
            || !ValidateNonNegativeManifestLong(manifest.SymbolCount, "symbol_count", out message)
            || !ValidateNonNegativeManifestLong(manifest.ReferenceCount, "reference_count", out message)
            || !ValidateNonNegativeManifestLong(manifest.UnknownExtensionFileCount, "unknown_extension_file_count", out message))
        {
            return false;
        }

        if (!ValidateNonNegativeManifestInt(manifest.CodeIndexMetaSchemaVersion, "codeindex_meta_schema_version", out message)
            || !ValidateNonNegativeManifestInt(manifest.CSharpSymbolNameContractVersion, "csharp_symbol_name_contract_version", out message)
            || !ValidateNonNegativeManifestInt(manifest.SqlGraphContractVersion, "sql_graph_contract_version", out message)
            || !ValidateNonNegativeManifestInt(manifest.HotspotFamilyVersion, "hotspot_family_version", out message)
            || !ValidateNonNegativeManifestInt(manifest.UnknownExtensionFilePathLimit, "unknown_extension_file_path_limit", out message)
            || !ValidateNonNegativeManifestInt(manifest.UnknownExtensionFileSampleCount, "unknown_extension_file_sample_count", out message)
            || !ValidateNonNegativeManifestInt(manifest.UnknownExtensionFileSampleLimit, "unknown_extension_file_sample_limit", out message))
        {
            return false;
        }

        if (manifest.UnknownExtensionFileSampleCount.HasValue)
        {
            var sampleLength = manifest.UnknownExtensionFiles?.Length ?? 0;
            if (manifest.UnknownExtensionFileSampleCount.Value != sampleLength)
            {
                message = "unknown_extension_file_sample_count must match unknown_extension_files length";
                return false;
            }
        }

        if (manifest.UnknownExtensionFileSampleCount.HasValue
            && manifest.UnknownExtensionFileSampleLimit.HasValue
            && manifest.UnknownExtensionFileSampleCount.Value > manifest.UnknownExtensionFileSampleLimit.Value)
        {
            message = "unknown_extension_file_sample_count exceeds unknown_extension_file_sample_limit";
            return false;
        }

        if (manifest.UnknownExtensionFiles is { Length: > ManifestUnknownExtensionFileLimit })
        {
            message = $"unknown_extension_files exceeds the manifest limit of {ManifestUnknownExtensionFileLimit}";
            return false;
        }

        if (manifest.UnknownExtensionFiles != null)
        {
            var totalPathChars = 0;
            foreach (var path in manifest.UnknownExtensionFiles)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    message = "unknown_extension_files contains an empty path";
                    return false;
                }

                if (path.Length > ManifestUnknownExtensionPathCharLimit)
                {
                    message = $"unknown_extension_files contains a path longer than {ManifestUnknownExtensionPathCharLimit} characters";
                    return false;
                }

                totalPathChars += path.Length;
                if (totalPathChars > ManifestUnknownExtensionFilesTotalCharLimit)
                {
                    message = $"unknown_extension_files total path text exceeds the manifest limit of {ManifestUnknownExtensionFilesTotalCharLimit} characters";
                    return false;
                }
            }
        }

        message = string.Empty;
        return true;
    }

    private static bool TryValidateImportedManifest(
        ExportManifest manifest,
        string dbPath,
        out string message,
        out string phase,
        CancellationToken cancellationToken = default)
    {
        phase = PhaseSha256;
        var actualSha256 = ComputeSha256(dbPath, cancellationToken);
        if (!string.Equals(manifest.DatabaseSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            message = "database_sha256 does not match codeindex.db";
            return false;
        }

        phase = PhaseSqliteValidate;
        int actualUserVersion;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = new SqliteConnection(CreateUnpooledConnectionString(dbPath));
            connection.Open();
            actualUserVersion = ReadSqliteUserVersion(connection);
            if (!TryValidateManifestCount(manifest.FileCount, connection, "files", "file_count", out message, cancellationToken)
                || !TryValidateManifestCount(manifest.ChunkCount, connection, "chunks", "chunk_count", out message, cancellationToken)
                || !TryValidateManifestCount(manifest.SymbolCount, connection, "symbols", "symbol_count", out message, cancellationToken)
                || !TryValidateManifestCount(manifest.ReferenceCount, connection, "symbol_references", "reference_count", out message, cancellationToken))
            {
                return false;
            }
        }
        catch (SqliteException ex)
        {
            message = $"could not validate codeindex.db manifest metadata ({CommandErrorWriter.FormatSanitizedException(ex)})";
            return false;
        }

        if (actualUserVersion != manifest.UserVersion)
        {
            message = $"manifest user_version `{manifest.UserVersion}` does not match codeindex.db user_version `{actualUserVersion}`";
            return false;
        }

        phase = string.Empty;
        message = string.Empty;
        return true;
    }

    private static int ReadSqliteUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool TryValidateManifestCount(long? expected, SqliteConnection connection, string tableName, string fieldName, out string message, CancellationToken cancellationToken)
    {
        if (expected == null)
        {
            message = string.Empty;
            return true;
        }

        var actual = ReadTableCount(connection, tableName, cancellationToken);
        if (actual != expected.Value)
        {
            message = $"manifest {fieldName} `{expected.Value}` does not match codeindex.db {tableName} count `{actual}`";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool ValidateNonNegativeManifestLong(long? value, string fieldName, out string message)
    {
        if (value is < 0)
        {
            message = $"{fieldName} must be non-negative";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool ValidateNonNegativeManifestInt(int? value, string fieldName, out string message)
    {
        if (value is < 0)
        {
            message = $"{fieldName} must be non-negative";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static long ReadTableCount(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = tableName switch
        {
            "files" => "SELECT COUNT(*) FROM files",
            "chunks" => "SELECT COUNT(*) FROM chunks",
            "symbols" => "SELECT COUNT(*) FROM symbols",
            "symbol_references" => "SELECT COUNT(*) FROM symbol_references",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unsupported manifest count table."),
        };
        var count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        cancellationToken.ThrowIfCancellationRequested();
        return count;
    }

    private static string? ReadMetaString(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key LIMIT 1";
        SqliteCommandPolicy.Add(cmd, "@key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static int? ReadMetaInt(SqliteConnection connection, string key)
    {
        var value = ReadMetaString(connection, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
    }

    private static long? ReadMetaLong(SqliteConnection connection, string key)
    {
        var value = ReadMetaString(connection, key);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
    }

    private static bool? ReadMetaBool(SqliteConnection connection, string key)
    {
        var value = ReadMetaString(connection, key);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private readonly record struct UnknownExtensionFileSample(string[]? Files, int? Count, int? Limit, bool? Truncated);

    private static UnknownExtensionFileSample ReadUnknownExtensionFileSample(SqliteConnection connection)
    {
        var json = ReadMetaString(connection, DbContext.UnknownExtensionFilePathsMetaKey);
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxImportManifestBytes)
            return new(null, null, null, null);

        try
        {
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(
                jsonBytes,
                new JsonReaderOptions { MaxDepth = ManifestUnknownExtensionJsonDepth });
            if (!reader.Read())
                return new(null, null, null, null);
            if (reader.TokenType == JsonTokenType.Null)
            {
                if (reader.Read())
                    return new(null, null, null, null);

                return new(null, 0, ManifestUnknownExtensionFileLimit, false);
            }
            if (reader.TokenType != JsonTokenType.StartArray)
                return new(null, null, null, null);

            var sample = new List<string>(ManifestUnknownExtensionFileLimit);
            var decodedItems = 0;
            var truncated = false;
            var completed = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    completed = true;
                    break;
                }
                if (reader.TokenType != JsonTokenType.String)
                    return new(null, null, null, null);

                decodedItems++;
                if (decodedItems > ManifestUnknownExtensionDecodedItemLimit)
                {
                    truncated = true;
                    break;
                }

                var path = reader.GetString();
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (sample.Count >= ManifestUnknownExtensionFileLimit)
                {
                    truncated = true;
                    break;
                }

                sample.Add(path.Length <= ManifestUnknownExtensionPathCharLimit
                    ? path
                    : path[..ManifestUnknownExtensionPathCharLimit]);
            }

            if (!completed && !truncated)
                return new(null, null, null, null);
            if (completed && reader.Read())
                return new(null, null, null, null);
            if (sample.Count == 0)
                return new(null, 0, ManifestUnknownExtensionFileLimit, false);

            return new(sample.ToArray(), sample.Count, ManifestUnknownExtensionFileLimit, truncated);
        }
        catch (JsonException)
        {
            return new(null, null, null, null);
        }
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value == null || value.Length != 64)
            return false;

        foreach (var ch in value)
        {
            if (!char.IsAsciiHexDigit(ch))
                return false;
        }

        return true;
    }

    internal static bool TryValidateDatabaseEntrySize(long uncompressedLength, long compressedLength, out string message)
    {
        if (uncompressedLength < 0 || compressedLength < 0)
        {
            message = "archive codeindex.db size metadata is invalid";
            return false;
        }

        if (uncompressedLength > MaxImportDatabaseBytes)
        {
            message = $"archive codeindex.db is too large: {ConsoleUi.FormatBytes(uncompressedLength)} uncompressed exceeds the import limit of {ConsoleUi.FormatBytes(MaxImportDatabaseBytes)}";
            return false;
        }

        if (compressedLength > MaxImportDatabaseBytes)
        {
            message = $"archive codeindex.db is too large: {ConsoleUi.FormatBytes(compressedLength)} compressed exceeds the import limit of {ConsoleUi.FormatBytes(MaxImportDatabaseBytes)}";
            return false;
        }

        if (uncompressedLength > 0 && compressedLength == 0)
        {
            message = "archive codeindex.db compression metadata is invalid: non-empty entry has zero compressed bytes";
            return false;
        }

        if (compressedLength > 0 && uncompressedLength > compressedLength * MaxImportDatabaseCompressionRatio)
        {
            message = $"archive codeindex.db compression ratio exceeds the import limit of {MaxImportDatabaseCompressionRatio}:1";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static void ExtractDatabaseEntryToFile(ZipArchiveEntry dbEntry, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = dbEntry.Open();
        using var target = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        CopyToWithLimit(source, target, MaxImportDatabaseBytes, cancellationToken);
    }

    internal static long CopyToWithLimit(
        Stream source,
        Stream target,
        long maxBytes,
        CancellationToken cancellationToken = default)
        => CopyToWithLimit(source, target, maxBytes, DatabaseEntryName, cancellationToken);

    internal static long CopyToExactLength(
        Stream source,
        Stream target,
        long expectedBytes,
        string entryName,
        CancellationToken cancellationToken = default)
    {
        if (expectedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedBytes), expectedBytes, "Expected byte length must be non-negative.");

        var buffer = new byte[ImportCopyBufferSize];
        long totalBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = source.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                break;

            if (totalBytes > expectedBytes - bytesRead)
                throw new InvalidDataException($"archive {entryName} source grew beyond the expected snapshot length of {ConsoleUi.FormatBytes(expectedBytes)}.");

            target.Write(buffer, 0, bytesRead);
            totalBytes += bytesRead;
        }

        if (totalBytes != expectedBytes)
            throw new EndOfStreamException($"archive {entryName} source ended after {ConsoleUi.FormatBytes(totalBytes)}; expected {ConsoleUi.FormatBytes(expectedBytes)}.");

        return totalBytes;
    }

    internal static long CopyToWithLimit(
        Stream source,
        Stream target,
        long maxBytes,
        CancellationToken cancellationToken,
        IProgress<long>? progress = null)
        => CopyToWithLimit(source, target, maxBytes, DatabaseEntryName, cancellationToken, progress);

    private static long CopyToWithLimit(
        Stream source,
        Stream target,
        long maxBytes,
        string entryName,
        CancellationToken cancellationToken = default,
        IProgress<long>? progress = null)
    {
        var buffer = new byte[ImportCopyBufferSize];
        long totalBytes = 0;
        int bytesRead;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytesRead = source.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                break;

            if (totalBytes > maxBytes - bytesRead)
                throw new InvalidDataException($"archive {entryName} exceeds the import limit of {ConsoleUi.FormatBytes(maxBytes)}.");

            target.Write(buffer, 0, bytesRead);
            totalBytes += bytesRead;
            progress?.Report(totalBytes);
        }

        return totalBytes;
    }

    private static void RewriteImportedProjectRoot(string dbPath, string projectRoot)
    {
        using var connection = new SqliteConnection(CreateUnpooledConnectionString(dbPath));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO codeindex_meta(key, value)
            VALUES ('indexed_project_root', @projectRoot)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        SqliteCommandPolicy.Add(cmd, "@projectRoot", Path.GetFullPath(projectRoot));
        cmd.ExecuteNonQuery();
    }

    internal static string ResolveImportTargetProjectRoot(string fullDbPath)
    {
        var normalizedDbPath = Path.GetFullPath(fullDbPath);
        var dbDirectory = Path.GetDirectoryName(normalizedDbPath);
        if (!string.IsNullOrWhiteSpace(dbDirectory)
            && string.Equals(Path.GetFileName(normalizedDbPath), "codeindex.db", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(dbDirectory), ".cdidx", StringComparison.OrdinalIgnoreCase))
        {
            var siblingRoot = Path.GetDirectoryName(dbDirectory);
            if (!string.IsNullOrWhiteSpace(siblingRoot))
                return Path.GetFullPath(siblingRoot);
        }

        return Path.GetFullPath(Environment.CurrentDirectory);
    }

    private static string FormatImportSuccessMessage(string prefix, bool prunePaths, string importTargetProjectRoot)
        => prunePaths
            ? $"{prefix}; pruned paths to project root {importTargetProjectRoot}"
            : prefix;

    internal static void CreateDatabaseSnapshot(string sourceDbPath, string snapshotPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = new SqliteConnection(CreateUnpooledConnectionString(sourceDbPath));
        using var destination = new SqliteConnection(CreateUnpooledConnectionString(snapshotPath));
        source.Open();
        destination.Open();
        DataDirectorySecurity.ApplyPrivateFileMode(snapshotPath);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
        DataDirectorySecurity.ApplyPrivateFileMode(snapshotPath);
    }

    private static string CreateUnpooledConnectionString(string dbPath)
        => SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.Unpooled);

    private static string CreateReadOnlyUnpooledConnectionString(string dbPath)
        => SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ReadOnlyUnpooled);

    internal static void ReplaceImportedDatabase(string tempPath, string fullDbPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dbBackupPath = MoveExistingReplacementFileToBackup(fullDbPath);
        var sidecarBackups = new List<ReplacementBackup>(capacity: 2);
        try
        {
            AddReplacementBackup(sidecarBackups, fullDbPath + "-wal");
            AddReplacementBackup(sidecarBackups, fullDbPath + "-shm");

            cancellationToken.ThrowIfCancellationRequested();
            AtomicFileWriter.MoveFile(
                tempPath,
                fullDbPath,
                overwrite: false,
                applyDestinationMode: ApplyImportedDatabasePrivateFileMode);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            try
            {
                RollBackImportedDatabaseReplacement(fullDbPath, dbBackupPath, sidecarBackups);
            }
            catch (Exception rollbackEx) when (IsRecoverableReplacementException(rollbackEx))
            {
                CommandErrorWriter.WriteStderr($"Warning: failed to roll back cancelled imported database replacement ({CommandErrorWriter.FormatSanitizedException(rollbackEx)}).");
            }

            throw;
        }
        catch (Exception ex) when (IsRecoverableReplacementException(ex))
        {
            Exception? rollbackFailure = null;
            try
            {
                RollBackImportedDatabaseReplacement(fullDbPath, dbBackupPath, sidecarBackups);
            }
            catch (Exception rollbackEx) when (IsRecoverableReplacementException(rollbackEx))
            {
                rollbackFailure = rollbackEx;
                CommandErrorWriter.WriteStderr($"Warning: failed to roll back imported database replacement ({CommandErrorWriter.FormatSanitizedException(rollbackEx)}).");
            }

            throw new ImportReplacementException(
                "import database replacement failed; rolled back the previous destination database when possible.",
                ex,
                BuildReplacementDiagnostics(tempPath, fullDbPath, dbBackupPath, sidecarBackups, rollbackFailure));
        }

        DeleteReplacementBackup(dbBackupPath, "import replaced database backup");
        foreach (var backup in sidecarBackups)
            DeleteReplacementBackup(backup.BackupPath, "import replaced database sidecar backup", DeleteSqliteSidecarForTesting);
    }

    private static IReadOnlyList<ExportImportDiagnosticResult> BuildReplacementDiagnostics(
        string tempPath,
        string fullDbPath,
        string? dbBackupPath,
        IReadOnlyList<ReplacementBackup> sidecarBackups,
        Exception? rollbackFailure)
    {
        var diagnostics = new List<ExportImportDiagnosticResult>
        {
            CreateResidualStateDiagnostic("import_replace_destination_state", "destination database", fullDbPath),
            CreateResidualStateDiagnostic("import_replace_staged_state", "staged import database", tempPath),
        };

        if (dbBackupPath != null)
            diagnostics.Add(CreateResidualStateDiagnostic("import_replace_backup_state", "destination database backup", dbBackupPath));

        foreach (var backup in sidecarBackups)
            diagnostics.Add(CreateResidualStateDiagnostic("import_replace_sidecar_backup_state", "destination sidecar backup", backup.BackupPath));

        if (rollbackFailure != null)
        {
            diagnostics.Add(new ExportImportDiagnosticResult(
                "import_replace_rollback_failed",
                $"Rollback failed while restoring the previous destination database ({CommandErrorWriter.FormatSanitizedException(rollbackFailure)}).",
                ConsoleUi.FormatBoundedValue(fullDbPath)));
        }

        return diagnostics;
    }

    private static ExportImportDiagnosticResult CreateResidualStateDiagnostic(string code, string description, string path)
        => new(
            code,
            $"{description} exists after replacement failure: {(File.Exists(path) ? "true" : "false")}.",
            ConsoleUi.FormatBoundedValue(path));

    private static void ApplyImportedDatabasePrivateFileMode(string fullDbPath)
    {
        if (ApplyPrivateFileModeForTesting != null)
        {
            ApplyPrivateFileModeForTesting(fullDbPath);
            return;
        }

        DataDirectorySecurity.ApplyPrivateFileMode(fullDbPath);
    }

    private static void AddReplacementBackup(List<ReplacementBackup> backups, string path)
    {
        var backupPath = MoveExistingReplacementFileToBackup(path);
        if (backupPath != null)
            backups.Add(new ReplacementBackup(path, backupPath));
    }

    private static string? MoveExistingReplacementFileToBackup(string path)
    {
        if (!File.Exists(path))
            return null;

        var backupPath = $"{path}.replace-backup-{Guid.NewGuid():N}";
        AtomicFileWriter.MoveFile(path, backupPath, overwrite: false);
        return backupPath;
    }

    private static void RollBackImportedDatabaseReplacement(
        string fullDbPath,
        string? dbBackupPath,
        IReadOnlyList<ReplacementBackup> sidecarBackups)
    {
        if (dbBackupPath != null)
        {
            AtomicFileWriter.MoveReplacing(dbBackupPath, fullDbPath);
        }
        else if (File.Exists(fullDbPath))
        {
            AtomicFileWriter.DeleteFileIfExists(fullDbPath);
        }

        foreach (var backup in sidecarBackups)
            AtomicFileWriter.MoveReplacing(backup.BackupPath, backup.OriginalPath);
    }

    private static void DeleteReplacementBackup(string? path, string cleanupDescription, Action<string>? deleteOverride = null)
    {
        if (path != null)
            TryDeleteFile(path, cleanupDescription, deleteOverride);
    }

    private static bool IsRecoverableReplacementException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static void DeleteSqliteSidecars(string dbPath, string? cleanupDescription = null)
    {
        TryDeleteFile(dbPath + "-wal", cleanupDescription, DeleteSqliteSidecarForTesting);
        TryDeleteFile(dbPath + "-shm", cleanupDescription, DeleteSqliteSidecarForTesting);
    }

    private static void TryDeleteFile(string path, string? cleanupDescription = null, Action<string>? deleteOverride = null)
    {
        try
        {
            _ = AtomicFileWriter.TryDeleteFile(
                path,
                ex =>
                {
                    if (!string.IsNullOrWhiteSpace(cleanupDescription))
                        CommandErrorWriter.WriteStderr($"Warning: failed to delete {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
                },
                deleteOverride ?? DeleteFileForTesting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            if (!string.IsNullOrWhiteSpace(cleanupDescription))
                CommandErrorWriter.WriteStderr($"Warning: failed to delete {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
    }

    private static void TryDeleteDirectoryIfEmpty(
        string path,
        string? cleanupDescription,
        string safeRoot,
        string expectedNamePrefix)
    {
        try
        {
            var options = new DirectoryCleanupBoundaryOptions(
                expectedNamePrefix,
                "target is outside the expected cleanup root",
                "target name does not match the expected temporary-directory prefix",
                "target is not a regular temporary directory");
            if (!FileSystemBoundary.TryValidateDirectoryCleanupTarget(path, safeRoot, options, out var fullPath, out var validationFailure))
            {
                if (!string.IsNullOrWhiteSpace(cleanupDescription))
                    CommandErrorWriter.WriteStderr($"Warning: skipped deleting {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({validationFailure}).");
                return;
            }

            if (!Directory.Exists(LongPath.EnsureWindowsPrefix(fullPath)) || CodeIndex.FileSystemTraversalPolicy.HasAnyFileSystemEntry(fullPath))
                return;

            Directory.Delete(LongPath.EnsureWindowsPrefix(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            if (!string.IsNullOrWhiteSpace(cleanupDescription))
                CommandErrorWriter.WriteStderr($"Warning: failed to delete {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
    }

    internal static Action<string>? DeleteFileForTesting { get; set; }
    internal static Action<string>? DeleteSqliteSidecarForTesting { get; set; }
    internal static Action<string>? ApplyPrivateFileModeForTesting { get; set; }

    private readonly record struct ReplacementBackup(string OriginalPath, string BackupPath);

    internal static StringComparison ResolveDatabasePathComparison(string dbPath)
    {
        if (TryReadDatabasePathCaseSensitive(dbPath, out var pathCaseSensitive))
            return pathCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return PathCasing.ComparisonFor(dbPath);
    }

    private static bool TryReadDatabasePathCaseSensitive(string dbPath, out bool pathCaseSensitive)
    {
        pathCaseSensitive = false;
        try
        {
            using var connection = new SqliteConnection(CreateReadOnlyUnpooledConnectionString(dbPath));
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key LIMIT 1";
            SqliteCommandPolicy.Add(cmd, "@key", DbContext.WorkspacePathCaseSensitiveMetaKey);
            var raw = cmd.ExecuteScalar();
            return raw is string value && bool.TryParse(value, out pathCaseSensitive);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSamePath(string left, string right, StringComparison comparison)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);

    internal static bool IsDatabaseOrSqliteSidecarPath(string path, string dbPath, StringComparison comparison)
        => IsSamePath(path, dbPath, comparison)
            || IsSamePath(path, dbPath + "-wal", comparison)
            || IsSamePath(path, dbPath + "-shm", comparison);

    internal static bool IsDatabaseOrSqliteSidecarPath(string path, string dbPath)
    {
        var liveComparison = PathCasing.ComparisonFor(dbPath);
        if (IsDatabaseOrSqliteSidecarPath(path, dbPath, liveComparison))
            return true;

        if (!TryReadDatabasePathCaseSensitive(dbPath, out var pathCaseSensitive))
            return false;

        var stampedComparison = pathCaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return stampedComparison != liveComparison
            && IsDatabaseOrSqliteSidecarPath(path, dbPath, stampedComparison);
    }

    private static string SanitizeCtagsField(string value)
        => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static bool TryReadValueOption(string[] args, ref int index, string optionName, string arg, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (arg == optionName)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                error = $"{optionName} requires a non-empty value.";
                return true;
            }
            value = args[++index];
            return true;
        }

        var prefix = optionName + "=";
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            if (string.IsNullOrWhiteSpace(value))
                error = $"{optionName} requires a non-empty value.";
            return true;
        }

        return false;
    }

    private static int WriteImportError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string phase,
        string errorCode,
        string message,
        string hint,
        string usage,
        int exitCode = CommandExitCodes.UsageError,
        IReadOnlyList<ExportImportDiagnosticResult>? diagnostics = null)
        => WriteStructuredError(json, jsonOptions, ImportCommandName, phase, errorCode, message, hint, usage, exitCode, diagnostics);

    private static int WriteExportError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string phase,
        string errorCode,
        string message,
        string hint,
        string usage,
        int exitCode = CommandExitCodes.UsageError,
        IReadOnlyList<ExportImportDiagnosticResult>? diagnostics = null)
        => WriteStructuredError(json, jsonOptions, ExportCommandName, phase, errorCode, message, hint, usage, exitCode, diagnostics);

    private static int WriteStructuredError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string command,
        string phase,
        string errorCode,
        string message,
        string hint,
        string usage,
        int exitCode,
        IReadOnlyList<ExportImportDiagnosticResult>? diagnostics)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new ExportImportErrorResult("1", "error", command, phase, errorCode, message, hint, usage, diagnostics),
                CliJsonSerializerContextFactory.Create(jsonOptions).ExportImportErrorResult));
            return exitCode;
        }

        return CommandErrorWriter.Write(message, exitCode, hint, usage);
    }

    private static void AddImportValidationPhase(
        List<ImportValidationPhaseResult> validationPhases,
        string phase,
        string status = "success",
        string? message = null)
        => validationPhases.Add(new ImportValidationPhaseResult(phase, status, message));

    internal sealed record ExportManifest(
        [property: JsonPropertyName("format_version")]
        string FormatVersion,
        [property: JsonPropertyName("cdidx_version")]
        string CdidxVersion,
        [property: JsonPropertyName("user_version")]
        int UserVersion,
        [property: JsonPropertyName("project_root")]
        string? ProjectRoot,
        [property: JsonPropertyName("indexed_head_sha")]
        string? IndexedHeadSha,
        [property: JsonPropertyName("database_sha256")]
        string DatabaseSha256,
        [property: JsonPropertyName("file_count")]
        long? FileCount = null,
        [property: JsonPropertyName("chunk_count")]
        long? ChunkCount = null,
        [property: JsonPropertyName("symbol_count")]
        long? SymbolCount = null,
        [property: JsonPropertyName("reference_count")]
        long? ReferenceCount = null,
        [property: JsonPropertyName("graph_ready")]
        bool? GraphReady = null,
        [property: JsonPropertyName("issues_ready")]
        bool? IssuesReady = null,
        [property: JsonPropertyName("fold_ready")]
        bool? FoldReady = null,
        [property: JsonPropertyName("index_writer_version")]
        string? IndexWriterVersion = null,
        [property: JsonPropertyName("indexed_head_branch")]
        string? IndexedHeadBranch = null,
        [property: JsonPropertyName("indexed_head_timestamp")]
        string? IndexedHeadTimestamp = null,
        [property: JsonPropertyName("codeindex_meta_schema_version")]
        int? CodeIndexMetaSchemaVersion = null,
        [property: JsonPropertyName("csharp_symbol_name_contract_version")]
        int? CSharpSymbolNameContractVersion = null,
        [property: JsonPropertyName("sql_graph_contract_version")]
        int? SqlGraphContractVersion = null,
        [property: JsonPropertyName("hotspot_family_version")]
        int? HotspotFamilyVersion = null,
        [property: JsonPropertyName("unknown_extension_file_count")]
        long? UnknownExtensionFileCount = null,
        [property: JsonPropertyName("unknown_extension_files")]
        string[]? UnknownExtensionFiles = null,
        [property: JsonPropertyName("unknown_extension_files_truncated")]
        bool? UnknownExtensionFilesTruncated = null,
        [property: JsonPropertyName("unknown_extension_file_path_limit")]
        int? UnknownExtensionFilePathLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_count")]
        int? UnknownExtensionFileSampleCount = null,
        [property: JsonPropertyName("unknown_extension_file_sample_limit")]
        int? UnknownExtensionFileSampleLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_truncated")]
        bool? UnknownExtensionFileSampleTruncated = null);
    internal sealed record ExportImportErrorResult(
        [property: JsonPropertyName("api_version")] string ApiVersion,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("error_code")] string ErrorCode,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("hint")] string Hint,
        [property: JsonPropertyName("usage")] string Usage,
        [property: JsonPropertyName("diagnostics")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ExportImportDiagnosticResult>? Diagnostics = null);
    internal sealed record ExportImportDiagnosticResult(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("path")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Path = null);
    internal sealed record ImportValidationPhaseResult(
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("message")] string? Message);
    internal sealed record ImportDryRunResult(
        [property: JsonPropertyName("api_version")] string ApiVersion,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("archive_path")] string ArchivePath,
        [property: JsonPropertyName("db_path")] string DbPath,
        [property: JsonPropertyName("dry_run")] bool DryRun,
        [property: JsonPropertyName("pruned_paths")] bool PrunedPaths,
        [property: JsonPropertyName("pruned_project_root")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PrunedProjectRoot,
        [property: JsonPropertyName("replacement_would_be_allowed")] bool ReplacementWouldBeAllowed,
        [property: JsonPropertyName("validation_phases")] IReadOnlyList<ImportValidationPhaseResult> ValidationPhases,
        [property: JsonPropertyName("unknown_extension_file_count")] long? UnknownExtensionFileCount = null,
        [property: JsonPropertyName("unknown_extension_files")] string[]? UnknownExtensionFiles = null,
        [property: JsonPropertyName("unknown_extension_files_truncated")] bool? UnknownExtensionFilesTruncated = null,
        [property: JsonPropertyName("unknown_extension_file_path_limit")] int? UnknownExtensionFilePathLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_count")] int? UnknownExtensionFileSampleCount = null,
        [property: JsonPropertyName("unknown_extension_file_sample_limit")] int? UnknownExtensionFileSampleLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_truncated")] bool? UnknownExtensionFileSampleTruncated = null);
    internal sealed record ExportArchiveResult(string ApiVersion, string ArchivePath, string DbPath);
    private sealed record CtagsExportOptions(
        string? Lang,
        IReadOnlyList<string> PathPatterns,
        IReadOnlyList<string> ExcludePathPatterns,
        bool ExcludeTests)
    {
        internal static CtagsExportOptions Unfiltered { get; } = new(null, [], [], false);
    }
    internal sealed record CtagsExportFilterResult(
        [property: JsonPropertyName("lang")] string? Lang,
        [property: JsonPropertyName("path")] IReadOnlyList<string> PathPatterns,
        [property: JsonPropertyName("exclude_path")] IReadOnlyList<string> ExcludePathPatterns,
        [property: JsonPropertyName("exclude_tests")] bool ExcludeTests);
    internal sealed record CtagsExportResult(
        [property: JsonPropertyName("api_version")] string ApiVersion,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("output_path")] string OutputPath,
        [property: JsonPropertyName("db_path")] string DbPath,
        [property: JsonPropertyName("tag_count")] long TagCount,
        [property: JsonPropertyName("emitted_count")] long EmittedCount,
        [property: JsonPropertyName("skipped_count")] long SkippedCount,
        [property: JsonPropertyName("filters")] CtagsExportFilterResult Filters,
        [property: JsonPropertyName("metadata_fields")] IReadOnlyList<string> MetadataFields);
    internal sealed record ImportResult(
        string ApiVersion,
        string DbPath,
        bool PrunedPaths,
        [property: JsonPropertyName("pruned_project_root")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PrunedProjectRoot,
        [property: JsonPropertyName("unknown_extension_file_count")] long? UnknownExtensionFileCount = null,
        [property: JsonPropertyName("unknown_extension_files")] string[]? UnknownExtensionFiles = null,
        [property: JsonPropertyName("unknown_extension_files_truncated")] bool? UnknownExtensionFilesTruncated = null,
        [property: JsonPropertyName("unknown_extension_file_path_limit")] int? UnknownExtensionFilePathLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_count")] int? UnknownExtensionFileSampleCount = null,
        [property: JsonPropertyName("unknown_extension_file_sample_limit")] int? UnknownExtensionFileSampleLimit = null,
        [property: JsonPropertyName("unknown_extension_file_sample_truncated")] bool? UnknownExtensionFileSampleTruncated = null);

    private sealed class ImportReplacementException : IOException
    {
        internal ImportReplacementException(string message, Exception innerException, IReadOnlyList<ExportImportDiagnosticResult> diagnostics)
            : base(message, innerException)
        {
            Diagnostics = diagnostics;
        }

        internal IReadOnlyList<ExportImportDiagnosticResult> Diagnostics { get; }
    }
}
