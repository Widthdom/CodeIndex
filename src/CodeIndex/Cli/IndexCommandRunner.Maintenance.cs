using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static readonly string[] AcceptedBackfillFoldFlags =
    [
        "--db", "--json", "--dry-run", "--help",
        "--no-checkpoint",
    ];

    public static int RunBackfillFold(string[] cmdArgs, JsonSerializerOptions jsonOptions) =>
        RunBackfillFold(cmdArgs, jsonOptions, cancellationForTesting: null);

    public static int RunOptimizeFts(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseOptimizeFtsArgs(cmdArgs);
        if (options.ShowHelp)
        {
            ConsoleUi.PrintUsage();
            return CommandExitCodes.Success;
        }

        if (options.ParseError != null)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                options.ParseError,
                CommandExitCodes.UsageError,
                "Run `cdidx optimize --help` to see the supported command shape.",
                CommandErrorCodes.UsageError);

        var dbUriValidationExitCode = ValidateDbPathFileUri(options.DbPath, "optimize", options.Json, jsonOptions);
        if (dbUriValidationExitCode != null)
            return dbUriValidationExitCode.Value;

        if (DbPathResolver.UriRequestsReadOnly(options.DbPath))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"database must be writable for optimize: {options.DbPath}",
                CommandExitCodes.DatabaseError,
                "Point `--db` at a writable filesystem path, or omit read-only URI parameters such as `immutable=1` / `mode=ro`.",
                CommandErrorCodes.DbNotWritable);

        return RunOptimizeFtsForDb(Path.GetFullPath(DbPathResolver.NormalizeDbPath(options.DbPath)), options.Json, jsonOptions, projectPath: null);
    }

    private static int RunOptimizeFtsForDb(string dbPath, bool json, JsonSerializerOptions jsonOptions, string? projectPath)
    {
        if (!DbContext.TryValidateExistingCodeIndexDb(dbPath, out var validationMessage, out var isNotFound))
            return WriteCommandError(
                json,
                jsonOptions,
                validationMessage,
                isNotFound ? CommandExitCodes.NotFound : CommandExitCodes.DatabaseError,
                isNotFound
                    ? "Point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one."
                    : "Point `--db` at an existing CodeIndex database created by `cdidx index`, then retry `cdidx optimize`.",
                isNotFound ? CommandErrorCodes.DbNotFound : CommandErrorCodes.DbError);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var lockPath = IndexLock.GetLockPath(dbPath);
            using var indexLock = IndexLock.Acquire(lockPath, projectPath ?? Path.GetDirectoryName(dbPath) ?? Environment.CurrentDirectory);
            using var db = new DbContext(dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db);
            var before = writer.GetFtsIncrementalWritesSinceOptimize();
            writer.OptimizeFts();
            stopwatch.Stop();
            var after = writer.GetFtsIncrementalWritesSinceOptimize();

            if (json)
            {
                var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
                Console.WriteLine(JsonSerializer.Serialize(
                    new OptimizeFtsJsonResult("success", dbPath, before, after, stopwatch.ElapsedMilliseconds),
                    jsonContext.OptimizeFtsJsonResult));
            }
            else
            {
                Console.WriteLine("Optimized FTS5 index.");
                Console.WriteLine(ConsoleUi.FormatSummaryLine("DB", dbPath, indent: "  "));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Writes before", before.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), indent: "  "));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Writes after", after.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), indent: "  "));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Elapsed", ConsoleUi.FormatDuration(stopwatch.Elapsed), indent: "  "));
            }

            return CommandExitCodes.Success;
        }
        catch (IndexLockConflictException ex)
        {
            var holderDescription = DescribeLockHolder(ex.Holder);
            var message = string.IsNullOrEmpty(holderDescription)
                ? "another cdidx index is already running on this database"
                : $"another cdidx index is already running on this database ({holderDescription})";
            return WriteCommandError(
                json,
                jsonOptions,
                message,
                CommandExitCodes.DatabaseError,
                "Wait for the running index to finish, then retry `cdidx optimize`.",
                CommandErrorCodes.DbLocked);
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return WriteCommandError(
                json,
                jsonOptions,
                $"failed to optimize FTS5 index: {ex.Message}",
                CommandExitCodes.DatabaseError,
                "Ensure no other writer is holding the database lock, then retry `cdidx optimize`.",
                CommandErrorCodes.DbError);
        }
    }

    internal static int RunBackfillFold(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        CancellationTokenSource? cancellationForTesting)
    {
        var options = ParseBackfillFoldArgs(cmdArgs);
        var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
        using var ownedCancellation = cancellationForTesting == null ? new CancellationTokenSource() : null;
        var backfillCancellation = cancellationForTesting ?? ownedCancellation!;
        using var cancelKeyPressRegistration = cancellationForTesting == null
            ? RegisterIndexCancelKeyPress(backfillCancellation)
            : NullDisposable.Instance;
        using var terminateSignalRegistration = cancellationForTesting == null
            ? RegisterIndexTerminateSignal(backfillCancellation)
            : NullDisposable.Instance;

        if (options.ShowHelp)
        {
            ConsoleUi.PrintUsage();
            return CommandExitCodes.Success;
        }

        if (options.ParseError != null)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                options.ParseError,
                CommandExitCodes.UsageError,
                "Run `cdidx backfill-fold --help` to see the supported command shape.",
                CommandErrorCodes.UsageError);

        if (!DbContext.TryValidateExistingCodeIndexDb(options.DbPath, out var validationMessage, out var isNotFound))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                validationMessage,
                isNotFound ? CommandExitCodes.NotFound : CommandExitCodes.DatabaseError,
                isNotFound
                    ? "Point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one."
                    : "Point `--db` at an existing CodeIndex database created by `cdidx index`, then retry `cdidx backfill-fold`.",
                isNotFound ? CommandErrorCodes.DbNotFound : CommandErrorCodes.DbError);

        try
        {
            using var db = new DbContext(options.DbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db);

            var userVersionBefore = db.GetUserVersion();
            var foldReadyBefore = (userVersionBefore & DbContext.FoldReadyFlag) != 0;
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var storedFoldVersion = db.GetMetaString("fold_key_version");
            var storedFoldFingerprint = db.GetMetaString("fold_key_fingerprint");
            var foldMetadataCurrentBefore = storedFoldVersion == currentFoldVersion
                && storedFoldFingerprint == currentFoldFingerprint;
            foldReadyBefore = foldReadyBefore && foldMetadataCurrentBefore;
            // Missing or mismatched fold metadata means persisted keys may have been generated
            // by a different fold algorithm/runtime, so refresh every row from source names.
            // fold metadata 未記録 / 不一致時は全行再計算して version/runtime skew を解消する。
            var rewriteAll = !foldMetadataCurrentBefore;

            var symbols = 0;
            var symbolReferences = 0;
            var verified = false;
            var userVersionAfter = userVersionBefore;

            if (options.DryRun)
            {
                (symbols, symbolReferences) = writer.CountBackfillFoldedColumns(rewriteAll);
            }
            else
            {
                if (!options.NoCheckpoint)
                    DbCommandRunner.CreateAutomaticCheckpoint(options.DbPath);

                (symbols, symbolReferences) = writer.BackfillFoldedColumns(
                    rewriteAll,
                    backfillCancellation.Token);
                // Row rewrites commit before the final FoldReady stamp so interrupted
                // backfills can resume from the remaining rows.
                // 行更新は FoldReady stamp より前に永続化し、中断後に残り行から再開できるようにする。
                using var transaction = writer.BeginTransaction();
                verified = writer.MarkFoldReady();
                if (!verified)
                {
                    return WriteCommandError(
                        options.Json,
                        jsonOptions,
                        "folded-name backfill verification failed: some rows still have NULL folded values",
                        CommandExitCodes.DatabaseError,
                        "Retry `cdidx backfill-fold`. If the DB still does not verify, rebuild it with `cdidx index <projectPath> --rebuild`.",
                        CommandErrorCodes.DbError);
                }

                transaction.Commit();
                userVersionAfter = db.GetUserVersion();
            }
            var foldMetadataCurrentAfter = options.DryRun
                ? foldMetadataCurrentBefore
                : true;
            var foldReadyAfter = (userVersionAfter & DbContext.FoldReadyFlag) != 0
                && foldMetadataCurrentAfter;
            var wasAlreadyComplete = foldReadyBefore && !rewriteAll && symbols == 0 && symbolReferences == 0;

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new BackfillFoldJsonResult(
                    symbols,
                    symbolReferences,
                    rewriteAll,
                    options.DryRun,
                    wasAlreadyComplete,
                    foldReadyBefore,
                    foldReadyAfter,
                    verified,
                    userVersionBefore,
                    userVersionAfter,
                    foldReadyAfter), jsonContext.BackfillFoldJsonResult));
            }
            else
            {
                Console.WriteLine(options.DryRun
                    ? "Previewing folded-name column backfill ..."
                    : "Backfilling folded-name columns ...");
                var verb = options.DryRun ? "would be rewritten" : "rewritten";
                Console.WriteLine($"  symbols:            {ConsoleUi.Counted(symbols, "row", format: "N0")} {verb}");
                Console.WriteLine($"  symbol_references:  {ConsoleUi.Counted(symbolReferences, "row", format: "N0")} {verb}");
                if (rewriteAll)
                    Console.WriteLine("  mode:               full folded-key refresh (fold metadata missing or mismatched)");
                Console.WriteLine($"  already complete:   {(wasAlreadyComplete ? "yes" : "no")}");
                Console.WriteLine($"  fold_ready:         {foldReadyBefore} -> {foldReadyAfter}");
                if (!options.DryRun)
                {
                    Console.WriteLine($"  verified:           {(verified ? "yes" : "no")}");
                    Console.WriteLine($"  stamp:              FoldReady bit set (user_version: {userVersionBefore} -> {userVersionAfter})");
                }
            }

            return CommandExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "folded-name backfill cancelled before it could complete.",
                CommandExitCodes.CancelledBySignal,
                "Rerun `cdidx backfill-fold` when you are ready to resume; the cancelled transaction was rolled back.",
                CommandErrorCodes.Interrupted);
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to backfill folded-name columns: {ex.Message}",
                CommandExitCodes.DatabaseError,
                "Retry `cdidx backfill-fold`. If this persists, rebuild the index with `cdidx index <projectPath> --rebuild`.",
                CommandErrorCodes.DbError);
        }
    }

    private static OptimizeFtsCommandOptions ParseOptimizeFtsArgs(string[] args)
    {
        var dbPath = Path.Combine(".cdidx", "codeindex.db");
        bool json = false;
        string? parseError = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help" or "-h":
                    return new OptimizeFtsCommandOptions { ShowHelp = true };
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                        parseError ??= $"unknown option '{args[i]}'";
                    else
                        dbPath = args[i];
                    break;
            }
        }

        return new OptimizeFtsCommandOptions
        {
            DbPath = dbPath,
            Json = json,
            ParseError = parseError,
        };
    }

    private static BackfillFoldCommandOptions ParseBackfillFoldArgs(string[] args)
    {
        var dbPath = Path.Combine(".cdidx", "codeindex.db");
        var json = false;
        var dryRun = false;
        var noCheckpoint = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--no-checkpoint":
                    noCheckpoint = true;
                    break;
                case "--help" or "-h":
                    return new BackfillFoldCommandOptions { ShowHelp = true, DbPath = dbPath, Json = json, DryRun = dryRun, NoCheckpoint = noCheckpoint };
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Warning: unknown option '{args[i]}' (ignored) / 不明なオプション '{args[i]}'（無視されます）");
                        WriteUnknownBackfillFoldOptionSuggestion(args[i]);
                    }
                    else
                        return new BackfillFoldCommandOptions
                        {
                            DbPath = dbPath,
                            Json = json,
                            DryRun = dryRun,
                            ParseError = $"backfill-fold does not accept positional arguments: '{args[i]}'"
                        };
                    break;
            }
        }

        return new BackfillFoldCommandOptions
        {
            DbPath = dbPath,
            Json = json,
            DryRun = dryRun,
            NoCheckpoint = noCheckpoint,
        };
    }

    private static void WriteUnknownBackfillFoldOptionSuggestion(string token)
    {
        var name = TrimInlineValue(token);
        var suggestion = ConsoleUi.FindClosestMatch(name, AcceptedBackfillFoldFlags);
        if (suggestion != null)
            Console.Error.WriteLine($"Did you mean: {suggestion}?");
    }
}
