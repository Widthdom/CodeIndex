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
}
