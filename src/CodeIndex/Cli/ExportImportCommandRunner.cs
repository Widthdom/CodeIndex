using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static class ExportImportCommandRunner
{
    private const string ManifestEntryName = "manifest.json";
    private const string DatabaseEntryName = "codeindex.db";
    internal const int MaxImportManifestBytes = 64 * 1024;
    internal const int MaxImportManifestJsonDepth = 16;
    internal const long MaxImportDatabaseBytes = 8L * 1024 * 1024 * 1024;
    private const int ImportCopyBufferSize = 81920;
    private const int ManifestUnknownExtensionFileLimit = DbContext.UnknownExtensionFilePathSampleLimit;
    private const int ManifestUnknownExtensionPathCharLimit = 4096;
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
    private const string ImportUsage = "cdidx import <archive> [--db <path>] [--prune-paths] [--dry-run|--check] [--json]";

    public static int RunExport(string[] args, JsonSerializerOptions jsonOptions, string appVersion)
    {
        if (args.Length > 0 && args[0] == "ctags")
            return RunExportCtags(args[1..]);

        return RunExportArchive(args, jsonOptions, appVersion);
    }

    public static int RunImport(string[] args, JsonSerializerOptions jsonOptions)
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
        var validationPhases = new List<ImportValidationPhaseResult>();
        var phase = PhaseOpenArchive;
        try
        {
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
                AddImportValidationPhase(validationPhases, PhaseOpenArchive);
                phase = PhaseManifest;
                var manifestEntry = archive.GetEntry(ManifestEntryName);
                if (manifestEntry == null)
                    return WriteImportError(wantsJson, jsonOptions, PhaseManifest, "import_manifest_missing", "archive is missing manifest.json.", "use an archive produced by `cdidx export <archive>`.", ImportUsage);
                if (!TryReadManifest(manifestEntry, jsonOptions, out var manifest, out var manifestError))
                    return WriteImportError(wantsJson, jsonOptions, PhaseManifest, "import_manifest_invalid", $"archive manifest is invalid: {manifestError}.", "use an archive produced by `cdidx export <archive>`.", ImportUsage);
                if (!TryValidateManifestHeader(manifest, out var manifestHeaderError))
                    return WriteImportError(wantsJson, jsonOptions, PhaseManifest, "import_manifest_incompatible", $"archive manifest is invalid: {manifestHeaderError}.", "re-export from a compatible CodeIndex database.", ImportUsage);
                AddImportValidationPhase(validationPhases, PhaseManifest);

                phase = PhaseDatabaseEntry;
                var dbEntry = archive.GetEntry(DatabaseEntryName);
                if (dbEntry == null)
                    return WriteImportError(wantsJson, jsonOptions, PhaseDatabaseEntry, "import_database_entry_missing", "archive is missing codeindex.db.", "use an archive produced by `cdidx export <archive>`.", ImportUsage);
                if (!TryValidateDatabaseEntrySize(dbEntry.Length, dbEntry.CompressedLength, out var sizeValidationMessage))
                    return WriteImportError(wantsJson, jsonOptions, PhaseDatabaseEntry, "import_database_entry_too_large", sizeValidationMessage, "re-export a smaller CodeIndex database or rebuild a smaller index.", ImportUsage);

                ExtractDatabaseEntryToFile(dbEntry, tempPath);
                AddImportValidationPhase(validationPhases, PhaseDatabaseEntry);

                phase = PhaseSha256;
                if (!TryValidateImportedManifest(manifest, tempPath, out var manifestValidationMessage, out var manifestValidationPhase))
                    return WriteImportError(wantsJson, jsonOptions, manifestValidationPhase, "import_manifest_mismatch", $"archive manifest mismatch: {manifestValidationMessage}.", "re-export from a compatible CodeIndex database.", ImportUsage);
                AddImportValidationPhase(validationPhases, PhaseSha256);
            }

            phase = PhaseSqliteValidate;
            if (!DbContext.TryValidateExistingCodeIndexDb(tempPath, out var validationMessage, out _))
                return WriteImportError(wantsJson, jsonOptions, PhaseSqliteValidate, "import_database_invalid", $"archive database is invalid: {validationMessage}.", "re-export from a compatible CodeIndex database.", ImportUsage);
            AddImportValidationPhase(validationPhases, PhaseSqliteValidate);
            SqliteConnection.ClearAllPools();

            if (prunePaths)
            {
                phase = PhasePrunePaths;
                RewriteImportedProjectRoot(tempPath, importTargetProjectRoot);
                AddImportValidationPhase(validationPhases, PhasePrunePaths);
                SqliteConnection.ClearAllPools();
            }

            if (dryRun)
            {
                AddImportValidationPhase(validationPhases, PhaseReplaceDb, "skipped", "dry-run does not replace the destination database");
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
                            validationPhases),
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
            ReplaceImportedDatabase(tempPath, fullDbPath);
            if (wantsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new ImportResult("1", fullDbPath, prunePaths, prunePaths ? importTargetProjectRoot : null),
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
                TryDeleteDirectoryIfEmpty(tempDirectory, "import temporary directory");
        }
    }

    private static int RunExportArchive(string[] args, JsonSerializerOptions jsonOptions, string appVersion)
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
        var dbPathComparison = ResolveDatabasePathComparison(normalizedDbPath);
        if (IsDatabaseOrSqliteSidecarPath(fullOutputPath, fullSourceDbPath, dbPathComparison))
        {
            return WriteExportError(wantsJson, jsonOptions, PhaseParseArgs, "export_archive_overlaps_database", "export archive path must not be the source database or a SQLite sidecar.", "choose a separate archive path, for example `codeindex.cdidx.zip`.", "cdidx export <archive> [--db <path>] [--json]");
        }

        string? snapshotDirectory = null;
        string? snapshotPath = null;
        var phase = PhaseWriteArchive;
        try
        {
            snapshotDirectory = DataDirectorySecurity.CreateSensitiveTempDirectory("codeindex-export-").FullName;
            snapshotPath = Path.Combine(snapshotDirectory, "codeindex.db");
            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            phase = PhaseSqliteValidate;
            CreateDatabaseSnapshot(normalizedDbPath, snapshotPath);
            ExportManifest manifest;
            using (var snapshotConnection = new SqliteConnection(CreateUnpooledConnectionString(snapshotPath)))
            {
                snapshotConnection.Open();
                manifest = BuildManifest(snapshotConnection, appVersion);
            }
            SqliteConnection.ClearAllPools();
            phase = PhaseSha256;
            manifest = manifest with { DatabaseSha256 = ComputeSha256(snapshotPath) };
            phase = PhaseWriteArchive;
            WriteExportArchiveFile(fullOutputPath, snapshotPath, manifest, jsonOptions);

            if (wantsJson)
                Console.WriteLine(JsonSerializer.Serialize(new ExportArchiveResult("1", fullOutputPath, fullSourceDbPath), jsonOptions));
            else
                Console.WriteLine($"Exported CodeIndex archive to {fullOutputPath}");
            return CommandExitCodes.Success;
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
                TryDeleteDirectoryIfEmpty(snapshotDirectory, "export temporary directory");
        }
    }

    private static int RunExportCtags(string[] args)
    {
        var outputPath = "tags";
        string? dbPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (TryReadValueOption(args, ref i, "--output", arg, out var outputValue, out var outputError))
            {
                if (outputError != null)
                    return WriteError(outputError, "use `cdidx export ctags --output tags`.", "cdidx export ctags [--output <path>] [--db <path>]");
                outputPath = outputValue!;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--db", arg, out var dbValue, out var dbError))
            {
                if (dbError != null)
                    return WriteError(dbError, "use `cdidx export ctags --db <path>`.", "cdidx export ctags [--output <path>] [--db <path>]");
                dbPath = dbValue;
                continue;
            }

            return WriteError($"unknown ctags export option `{arg}`.", "use `--output <path>` or `--db <path>`.", "cdidx export ctags [--output <path>] [--db <path>]");
        }

        dbPath ??= DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath;
        var normalizedDbPath = DbPathResolver.NormalizeDbPath(dbPath);
        var fullSourceDbPath = Path.GetFullPath(normalizedDbPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var dbPathComparison = ResolveDatabasePathComparison(normalizedDbPath);
        if (IsDatabaseOrSqliteSidecarPath(fullOutputPath, fullSourceDbPath, dbPathComparison))
        {
            return WriteError("ctags output path must not be the source database or a SQLite sidecar.", "choose a separate tags path, for example `tags`.", "cdidx export ctags [--output <path>] [--db <path>]");
        }

        if (!DbContext.TryValidateExistingCodeIndexDb(normalizedDbPath, out var validationMessage, out _))
            return WriteError(validationMessage, "run `cdidx index <projectPath>` first or pass `--db <path>`.", "cdidx export ctags [--output <path>] [--db <path>]");

        try
        {
            using var db = new DbContext(normalizedDbPath);
            db.TryMigrateForRead();
            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            WriteCtagsFile(fullOutputPath, writer =>
            {
                writer.WriteLine("!_TAG_FILE_FORMAT\t2\t/extended format/");
                writer.WriteLine("!_TAG_FILE_SORTED\t1\t/0=unsorted, 1=sorted, 2=foldcase/");

                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT s.name, f.path, COALESCE(s.start_line, s.line, 1), s.kind
                    FROM symbols s
                    JOIN files f ON s.file_id = f.id
                    WHERE s.name IS NOT NULL AND s.name != ''
                    ORDER BY s.name COLLATE NOCASE, f.path, COALESCE(s.start_line, s.line, 1)";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = SanitizeCtagsField(reader.GetString(0));
                    var path = SanitizeCtagsField(reader.GetString(1));
                    var line = Math.Max(1, reader.GetInt32(2));
                    var kind = SanitizeCtagsField(reader.GetString(3));
                    writer.WriteLine($"{name}\t{path}\t{line};\"\tkind:{kind}\tline:{line}");
                }
            });

            Console.WriteLine($"Exported ctags to {fullOutputPath}");
            return CommandExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return WriteError($"ctags export failed ({CommandErrorWriter.FormatSanitizedException(ex)}).", "check the database and output paths.", "cdidx export ctags [--output <path>] [--db <path>]");
        }
    }

    private static ExportManifest BuildManifest(SqliteConnection connection, string appVersion)
    {
        var userVersion = ReadSqliteUserVersion(connection);
        var projectRoot = ReadMetaString(connection, DbContext.IndexedProjectRootMetaKey);
        var indexedHead = ReadMetaString(connection, DbContext.IndexedHeadShaMetaKey);
        return new ExportManifest(
            "1",
            appVersion,
            userVersion,
            projectRoot,
            indexedHead,
            string.Empty,
            FileCount: ReadTableCount(connection, "files"),
            ChunkCount: ReadTableCount(connection, "chunks"),
            SymbolCount: ReadTableCount(connection, "symbols"),
            ReferenceCount: ReadTableCount(connection, "symbol_references"),
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
            UnknownExtensionFiles: ReadUnknownExtensionFiles(connection),
            UnknownExtensionFilesTruncated: ReadMetaBool(connection, DbContext.UnknownExtensionFilesTruncatedMetaKey),
            UnknownExtensionFilePathLimit: ReadMetaInt(connection, DbContext.UnknownExtensionFilePathLimitMetaKey));
    }

    private static void AddTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        entry.LastWriteTime = DeterministicZipTimestamp;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    internal static void WriteExportArchiveFile(string outputPath, string snapshotPath, ExportManifest manifest, JsonSerializerOptions jsonOptions)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        AtomicFileWriter.Write(
            fullOutputPath,
            stream =>
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                AddTextEntry(archive, ManifestEntryName, JsonSerializer.Serialize(manifest, jsonOptions));
                var dbEntry = archive.CreateEntry(DatabaseEntryName, CompressionLevel.SmallestSize);
                dbEntry.LastWriteTime = DeterministicZipTimestamp;
                using var source = File.OpenRead(snapshotPath);
                using var target = dbEntry.Open();
                source.CopyTo(target);
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

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool TryReadManifest(ZipArchiveEntry manifestEntry, JsonSerializerOptions jsonOptions, out ExportManifest manifest, out string message)
    {
        if (!TryValidateManifestEntrySize(manifestEntry, out message))
        {
            manifest = null!;
            return false;
        }

        try
        {
            using var stream = manifestEntry.Open();
            using var manifestBytes = new MemoryStream((int)Math.Min(Math.Max(manifestEntry.Length, 0), MaxImportManifestBytes));
            CopyToWithLimit(stream, manifestBytes, MaxImportManifestBytes, ManifestEntryName);
            manifestBytes.Position = 0;
            var parsedManifest = JsonSerializer.Deserialize<ExportManifest>(manifestBytes, CreateImportManifestJsonOptions(jsonOptions));
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
            message = ex.Message;
            return false;
        }
        catch (JsonException ex)
        {
            manifest = null!;
            message = IsJsonDepthLimitException(ex)
                ? $"manifest.json exceeds the JSON depth limit of {MaxImportManifestJsonDepth}"
                : "manifest.json is not valid export manifest JSON";
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

    private static bool IsJsonDepthLimitException(JsonException ex)
        => ex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase);

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
            || !ValidateNonNegativeManifestInt(manifest.UnknownExtensionFilePathLimit, "unknown_extension_file_path_limit", out message))
        {
            return false;
        }

        if (manifest.UnknownExtensionFiles is { Length: > ManifestUnknownExtensionFileLimit })
        {
            message = $"unknown_extension_files exceeds the manifest limit of {ManifestUnknownExtensionFileLimit}";
            return false;
        }

        if (manifest.UnknownExtensionFiles != null)
        {
            foreach (var path in manifest.UnknownExtensionFiles)
            {
                if (path.Length > ManifestUnknownExtensionPathCharLimit)
                {
                    message = $"unknown_extension_files contains a path longer than {ManifestUnknownExtensionPathCharLimit} characters";
                    return false;
                }
            }
        }

        message = string.Empty;
        return true;
    }

    private static bool TryValidateImportedManifest(ExportManifest manifest, string dbPath, out string message, out string phase)
    {
        phase = PhaseSha256;
        var actualSha256 = ComputeSha256(dbPath);
        if (!string.Equals(manifest.DatabaseSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            message = "database_sha256 does not match codeindex.db";
            return false;
        }

        phase = PhaseSqliteValidate;
        int actualUserVersion;
        try
        {
            using var connection = new SqliteConnection(CreateUnpooledConnectionString(dbPath));
            connection.Open();
            actualUserVersion = ReadSqliteUserVersion(connection);
            if (!TryValidateManifestCount(manifest.FileCount, connection, "files", "file_count", out message)
                || !TryValidateManifestCount(manifest.ChunkCount, connection, "chunks", "chunk_count", out message)
                || !TryValidateManifestCount(manifest.SymbolCount, connection, "symbols", "symbol_count", out message)
                || !TryValidateManifestCount(manifest.ReferenceCount, connection, "symbol_references", "reference_count", out message))
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

    private static bool TryValidateManifestCount(long? expected, SqliteConnection connection, string tableName, string fieldName, out string message)
    {
        if (expected == null)
        {
            message = string.Empty;
            return true;
        }

        var actual = ReadTableCount(connection, tableName);
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

    private static long ReadTableCount(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = tableName switch
        {
            "files" => "SELECT COUNT(*) FROM files",
            "chunks" => "SELECT COUNT(*) FROM chunks",
            "symbols" => "SELECT COUNT(*) FROM symbols",
            "symbol_references" => "SELECT COUNT(*) FROM symbol_references",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unsupported manifest count table."),
        };
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? ReadMetaString(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key LIMIT 1";
        cmd.Parameters.AddWithValue("@key", key);
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

    private static string[]? ReadUnknownExtensionFiles(SqliteConnection connection)
    {
        var json = ReadMetaString(connection, DbContext.UnknownExtensionFilePathsMetaKey);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxImportManifestBytes)
            return null;

        try
        {
            var files = JsonSerializer.Deserialize<string[]>(json);
            if (files == null || files.Length == 0)
                return null;

            return files
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(ManifestUnknownExtensionFileLimit)
                .Select(path => path.Length <= ManifestUnknownExtensionPathCharLimit ? path : path[..ManifestUnknownExtensionPathCharLimit])
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
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

        message = string.Empty;
        return true;
    }

    private static void ExtractDatabaseEntryToFile(ZipArchiveEntry dbEntry, string destinationPath)
    {
        using var source = dbEntry.Open();
        using var target = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        CopyToWithLimit(source, target, MaxImportDatabaseBytes);
    }

    internal static long CopyToWithLimit(Stream source, Stream target, long maxBytes)
        => CopyToWithLimit(source, target, maxBytes, DatabaseEntryName);

    private static long CopyToWithLimit(Stream source, Stream target, long maxBytes, string entryName)
    {
        var buffer = new byte[ImportCopyBufferSize];
        long totalBytes = 0;
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (totalBytes > maxBytes - bytesRead)
                throw new InvalidDataException($"archive {entryName} exceeds the import limit of {ConsoleUi.FormatBytes(maxBytes)}.");

            target.Write(buffer, 0, bytesRead);
            totalBytes += bytesRead;
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
        cmd.Parameters.AddWithValue("@projectRoot", Path.GetFullPath(projectRoot));
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

    internal static void CreateDatabaseSnapshot(string sourceDbPath, string snapshotPath)
    {
        using var source = new SqliteConnection(CreateUnpooledConnectionString(sourceDbPath));
        using var destination = new SqliteConnection(CreateUnpooledConnectionString(snapshotPath));
        source.Open();
        destination.Open();
        DataDirectorySecurity.ApplyPrivateFileMode(snapshotPath);
        source.BackupDatabase(destination);
        DataDirectorySecurity.ApplyPrivateFileMode(snapshotPath);
    }

    private static string CreateUnpooledConnectionString(string dbPath)
        => new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ConnectionString;

    internal static void ReplaceImportedDatabase(string tempPath, string fullDbPath)
    {
        File.Move(tempPath, fullDbPath, overwrite: true);
        DataDirectorySecurity.ApplyPrivateFileMode(fullDbPath);
        DeleteSqliteSidecars(fullDbPath);
    }

    private static void DeleteSqliteSidecars(string dbPath, string? cleanupDescription = null)
    {
        TryDeleteFile(dbPath + "-wal", cleanupDescription, DeleteSqliteSidecarForTesting);
        TryDeleteFile(dbPath + "-shm", cleanupDescription, DeleteSqliteSidecarForTesting);
    }

    private static void TryDeleteFile(string path, string? cleanupDescription = null, Action<string>? deleteOverride = null)
    {
        try
        {
            if (!File.Exists(path))
                return;

            if (deleteOverride != null)
                deleteOverride(path);
            else if (DeleteFileForTesting != null)
                DeleteFileForTesting(path);
            else
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            if (!string.IsNullOrWhiteSpace(cleanupDescription))
                Console.Error.WriteLine($"Warning: failed to delete {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string path, string? cleanupDescription = null)
    {
        try
        {
            if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any())
                return;

            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            if (!string.IsNullOrWhiteSpace(cleanupDescription))
                Console.Error.WriteLine($"Warning: failed to delete {cleanupDescription} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
    }

    internal static Action<string>? DeleteFileForTesting { get; set; }
    internal static Action<string>? DeleteSqliteSidecarForTesting { get; set; }

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
            using var connection = new SqliteConnection(CreateUnpooledConnectionString(dbPath));
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key LIMIT 1";
            cmd.Parameters.AddWithValue("@key", DbContext.WorkspacePathCaseSensitiveMetaKey);
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

    private static int WriteError(string message, string hint, string usage)
        => CommandErrorWriter.Write(message, CommandExitCodes.UsageError, hint, usage);

    private static int WriteImportError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string phase,
        string errorCode,
        string message,
        string hint,
        string usage)
        => WriteStructuredError(json, jsonOptions, ImportCommandName, phase, errorCode, message, hint, usage);

    private static int WriteExportError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string phase,
        string errorCode,
        string message,
        string hint,
        string usage)
        => WriteStructuredError(json, jsonOptions, ExportCommandName, phase, errorCode, message, hint, usage);

    private static int WriteStructuredError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string command,
        string phase,
        string errorCode,
        string message,
        string hint,
        string usage)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new ExportImportErrorResult("1", "error", command, phase, errorCode, message, hint, usage),
                CliJsonSerializerContextFactory.Create(jsonOptions).ExportImportErrorResult));
            return CommandExitCodes.UsageError;
        }

        return WriteError(message, hint, usage);
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
        int? UnknownExtensionFilePathLimit = null);
    internal sealed record ExportImportErrorResult(
        [property: JsonPropertyName("api_version")] string ApiVersion,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("error_code")] string ErrorCode,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("hint")] string Hint,
        [property: JsonPropertyName("usage")] string Usage);
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
        [property: JsonPropertyName("validation_phases")] IReadOnlyList<ImportValidationPhaseResult> ValidationPhases);
    internal sealed record ExportArchiveResult(string ApiVersion, string ArchivePath, string DbPath);
    internal sealed record ImportResult(
        string ApiVersion,
        string DbPath,
        bool PrunedPaths,
        [property: JsonPropertyName("pruned_project_root")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PrunedProjectRoot);
}
