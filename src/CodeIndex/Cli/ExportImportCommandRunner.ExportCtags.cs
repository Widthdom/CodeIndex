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
