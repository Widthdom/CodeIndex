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
        var overwrite = false;
        var redactPaths = false;
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
            if (arg == "--overwrite")
            {
                overwrite = true;
                continue;
            }
            if (arg == "--redact-paths")
            {
                redactPaths = true;
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
        if (!overwrite && AtomicFileWriter.PathEntryExists(fullOutputPath))
        {
            return WriteExportError(
                wantsJson,
                jsonOptions,
                PhaseWriteArchive,
                "export_archive_exists",
                "export archive destination already exists.",
                "pass `--overwrite` to atomically replace the existing destination.",
                ArchiveExportUsage);
        }

        var scopeOptions = new ArchiveExportOptions(
            lang,
            pathPatterns.ToArray(),
            excludePathPatterns.ToArray(),
            projects.ToArray(),
            solution,
            excludeTests,
            redactPaths);
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
                var redaction = ApplyArchivePathRedaction(snapshotConnection, scope, scopeOptions.RedactPaths, cancellationToken);
                manifest = BuildManifest(
                    snapshotConnection,
                    appVersion,
                    redaction.Scope,
                    scopeOptions.RedactPaths,
                    redaction.OmittedCategories,
                    cancellationToken);
            }
            else
            {
                using var snapshotConnection = new SqliteConnection(CreateUnpooledConnectionString(snapshotPath));
                cancellationToken.ThrowIfCancellationRequested();
                snapshotConnection.Open();
                phase = PhaseScopeArchive;
                var scope = ApplyArchiveScope(snapshotConnection, scopeOptions, cancellationToken);
                var redaction = ApplyArchivePathRedaction(snapshotConnection, scope, scopeOptions.RedactPaths, cancellationToken);
                manifest = BuildManifest(
                    snapshotConnection,
                    appVersion,
                    redaction.Scope,
                    scopeOptions.RedactPaths,
                    redaction.OmittedCategories,
                    cancellationToken);
            }
            SqliteConnection.ClearAllPools();
            phase = PhaseSha256;
            manifest = manifest with { DatabaseSha256 = ComputeSha256(snapshotPath, cancellationToken) };
            phase = PhaseWriteArchive;
            var artifact = WriteExportArchiveFile(
                fullOutputPath,
                snapshotPath,
                manifest,
                jsonOptions,
                cancellationToken,
                overwrite,
                includeArtifactMetadata: wantsJson);

            if (wantsJson)
            {
                var attestation = artifact
                    ?? throw new InvalidDataException("export archive artifact metadata was not created");
                Console.WriteLine(JsonSerializer.Serialize(
                    new ExportArchiveResult(
                        "1",
                        redactPaths ? RedactedArchivePath : fullOutputPath,
                        redactPaths ? RedactedArchivePath : fullSourceDbPath,
                        attestation.SizeBytes,
                        attestation.Sha256,
                        manifest,
                        manifest.Scope ?? throw new InvalidDataException("export scope metadata was not created"),
                        manifest.PathRedactionRequested,
                        manifest.PathRedactionComplete,
                        manifest.PathRedactionOmittedCategories ?? []),
                    jsonOptions));
            }
            else
                Console.WriteLine(redactPaths
                    ? "Exported path-redacted CodeIndex archive."
                    : $"Exported CodeIndex archive to {fullOutputPath}");
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
        catch (AtomicFileWriter.DestinationAlreadyExistsException)
        {
            return WriteExportError(
                wantsJson,
                jsonOptions,
                PhaseWriteArchive,
                "export_archive_exists",
                "export archive destination was created before the archive could be published.",
                "pass `--overwrite` to atomically replace the destination, or choose another path.",
                ArchiveExportUsage);
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
}
