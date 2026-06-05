using System.Text.Json;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static int? ValidateIndexRunOptions(
        IndexCommandOptions options,
        bool isUpdateMode,
        string dbPath,
        JsonSerializerOptions jsonOptions)
    {
        if (options.Watch)
        {
            var watchConflictExitCode = ValidateWatchOptions(options, jsonOptions);
            if (watchConflictExitCode != null)
                return watchConflictExitCode.Value;
        }

        if (options.OptimizeOnly && (options.DryRun || options.Watch || options.Rebuild || isUpdateMode))
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "--optimize cannot be combined with --dry-run, --watch, --rebuild, --commits, --changed-between, or --files",
                CommandExitCodes.UsageError,
                "Use `cdidx optimize --db <path>` or `cdidx index <projectPath> --optimize` by itself.",
                CommandErrorCodes.UsageError);
        }

        if (options.Rebuild && isUpdateMode)
            return WriteRebuildUpdateModeConflict(options, jsonOptions);

        if (options.ParseError != null)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                options.ParseError,
                CommandExitCodes.UsageError,
                "Run `cdidx index --help` to see supported options.",
                CommandErrorCodes.UsageError);
        }

        var commitRefValidationExitCode = ValidateCommitRefsBeforeIndexSetup(options, jsonOptions);
        if (commitRefValidationExitCode != null)
            return commitRefValidationExitCode.Value;

        if (options.SymbolKindFilter.ParseError != null)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                options.SymbolKindFilter.ParseError,
                CommandExitCodes.UsageError,
                "Pass comma-separated symbol kinds such as `--exclude-symbol-kind function,test_method`, or remove the empty value.",
                CommandErrorCodes.UsageError);
        }

        var dbUriValidationExitCode = ValidateDbPathFileUri(dbPath, "index", options.Json, jsonOptions);
        if (dbUriValidationExitCode != null)
            return dbUriValidationExitCode.Value;

        var rebuildConfirmationExitCode = ConfirmRebuildIfNeeded(options, dbPath, jsonOptions);
        if (rebuildConfirmationExitCode != null)
            return rebuildConfirmationExitCode.Value;

        if (options.ChangedBetweenSpecified && options.ChangedBetweenRefs.Count != 2)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "--changed-between requires exactly two refs",
                CommandExitCodes.UsageError,
                "Rerun `cdidx index <projectPath> --changed-between <old-ref> <new-ref>`.",
                CommandErrorCodes.UsageError);
        }

        if (!options.DryRun && DbPathResolver.UriRequestsReadOnly(dbPath))
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"database must be writable for index: {dbPath}",
                CommandExitCodes.DatabaseError,
                "Point `--db` at a writable filesystem path, or omit `--db` to use `<projectPath>/.cdidx/codeindex.db`.",
                CommandErrorCodes.DbNotWritable);
        }

        return null;
    }

    private static int? ValidateDbPathFileUri(
        string dbPath,
        string command,
        bool json,
        JsonSerializerOptions jsonOptions)
    {
        if (DbPathResolver.TryNormalizeDbPath(dbPath, out _, out var parseError))
            return null;

        return WriteCommandError(
            json,
            jsonOptions,
            $"invalid --db file URI for {command}: {SqliteFileUri.FormatParseError(parseError)}",
            CommandExitCodes.DatabaseError,
            "Pass a valid SQLite file URI whose full value and query string fit within the supported limits.",
            CommandErrorCodes.DbError);
    }

    private static int? ValidateCommitRefsBeforeIndexSetup(IndexCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (options.Commits.Count == 0)
            return null;

        try
        {
            foreach (var commit in options.Commits)
                GitHelper.ValidateCommitRef(options.ProjectPath!, commit);
            return null;
        }
        catch (Exception ex)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to resolve changed files from git commits: {ex.Message}",
                CommandExitCodes.UsageError,
                "Check the commit refs and rerun `cdidx index <projectPath> --commits <commit-ref> [commit-ref ...]`.",
                CommandErrorCodes.UsageError);
        }
    }

    private static int? ValidateWatchOptions(IndexCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        // --watch is the only mode that holds the process open after the initial scan, so
        // combining it with --commits, --changed-between, --files, or --dry-run produces ambiguous semantics:
        // those flags describe a one-shot snapshot. Reject the combination up front with
        // an actionable hint instead of silently picking one behavior.
        // --watch のみが初回スキャン後も常駐するため、ワンショット用の --commits / --changed-between / --files /
        // --dry-run と併用すると挙動が曖昧になる。ヒント付きで早期に拒否する。
        if (options.Commits.Count == 0 && !options.ChangedBetweenSpecified && options.UpdateFiles.Count == 0 && !options.DryRun)
            return null;

        const string watchConflictSynopsis =
            "`cdidx index <projectPath> --watch [--debounce <ms>]` "
            + "(omit --commits / --changed-between / --files / --dry-run; the initial scan plus continuous watch handles incremental refresh)";
        return WriteCommandError(
            options.Json,
            jsonOptions,
            "--watch cannot be combined with --commits, --changed-between, --files, or --dry-run (watch already drives continuous incremental updates)",
            CommandExitCodes.UsageError,
            "Use " + watchConflictSynopsis + ".",
            CommandErrorCodes.UsageError);
    }

    private static int WriteRebuildUpdateModeConflict(IndexCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        // Enumerate the three mutually-exclusive usage forms so the conflict error
        // does not require a second `--help` round-trip to find the correct command.
        const string rebuildConflictSynopsis =
            "`cdidx index <projectPath> --rebuild`, "
            + "`cdidx index <projectPath> --commits <commit-ref> [commit-ref ...]`, "
            + "`cdidx index <projectPath> --changed-between <old-ref> <new-ref>`, "
            + "or `cdidx index <projectPath> --files <path> [path ...]`";
        if (options.Json)
        {
            var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
            Console.WriteLine(JsonSerializer.Serialize(new CommandErrorJsonResult(
                "error",
                "--rebuild cannot be used with --commits, --changed-between, or --files (rebuild requires a full rescan)",
                "Use one of: " + rebuildConflictSynopsis + ".",
                CommandErrorCodes.UsageError),
                jsonContext.CommandErrorJsonResult));
        }
        else
        {
            Console.Error.WriteLine($"Error [{CommandErrorCodes.UsageError}]: --rebuild cannot be used with --commits, --changed-between, or --files (rebuild requires a full rescan)");
            Console.Error.WriteLine("Hint: use one of: " + rebuildConflictSynopsis + ".");
        }
        return CommandExitCodes.UsageError;
    }

    private static int? ConfirmRebuildIfNeeded(
        IndexCommandOptions options,
        string dbPath,
        JsonSerializerOptions jsonOptions)
    {
        if (!options.Rebuild || options.Yes || options.Force)
            return null;

        var resolvedPreviewDbPath = Path.GetFullPath(DbPathResolver.NormalizeDbPath(dbPath));
        var estimate = TryReadPriorFullScanEstimate(resolvedPreviewDbPath);
        var estimateSuffix = estimate == null
            ? string.Empty
            : $" Estimated time on prior full scan: {ConsoleUi.FormatDuration(estimate.Value, options.DurationFormat)}.";
        var warning = $"This will DELETE the existing index at {resolvedPreviewDbPath} and re-scan from scratch.{estimateSuffix}";

        if (IsInputRedirectedForTesting())
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"{warning} Pass --yes to confirm --rebuild in non-interactive environments.",
                CommandExitCodes.ExUsage,
                "Rerun with `--yes` to rebuild, or use `--files`, `--commits`, or `--changed-between` for an incremental refresh.",
                CommandErrorCodes.UsageError);
        }

        Console.Error.Write($"{warning} Proceed? [y/N] ");
        var answer = ReadLineForTesting();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return WriteCommandError(
            options.Json,
            jsonOptions,
            "index rebuild cancelled",
            CommandExitCodes.ExUsage,
            "Rerun with `--yes` to confirm, or use an incremental refresh mode.",
            CommandErrorCodes.UsageError);
    }

    private static TimeSpan? TryReadPriorFullScanEstimate(string resolvedDbPath)
    {
        if (!File.Exists(resolvedDbPath))
            return null;

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = resolvedDbPath,
                Mode = SqliteOpenMode.ReadOnly,
            };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
            command.Parameters.AddWithValue("@key", DbContext.LastFullScanElapsedMsMetaKey);
            var raw = command.ExecuteScalar() as string;
            if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var elapsedMs)
                && elapsedMs >= 0)
                return TimeSpan.FromMilliseconds(elapsedMs);
        }
        catch
        {
            // Legacy, corrupt, or locked DBs should not hide the destructive rebuild warning.
        }

        return null;
    }
}
