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
}
