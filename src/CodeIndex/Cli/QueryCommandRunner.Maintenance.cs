using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunVacuum(string[] cmdArgs, JsonSerializerOptions jsonOptions)
        => RunVacuum(cmdArgs, jsonOptions, CancellationToken.None);

    public static int RunVacuum(string[] cmdArgs, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("vacuum", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("vacuum")))
            return CommandExitCodes.UsageError;
        if (options.ParseError == null
            && options.DbPathExplicit
            && !string.IsNullOrWhiteSpace(options.DbPath)
            && !SqliteFileUri.StartsWithFileScheme(options.DbPath)
            && !File.Exists(LongPath.EnsureWindowsPrefix(options.DbPath)))
        {
            return MaintenanceDatabaseErrorWriter.Write(
                options.Json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.Create(
                    "vacuum",
                    options.DbPath,
                    options.ShowPaths,
                    MaintenanceDatabaseFailureKind.Missing));
        }
        if (TryWriteParseError(options, "vacuum"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("vacuum", options))
            return CommandExitCodes.UsageError;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!options.DryRun && DbPathResolver.UriRequestsReadOnly(options.DbPath))
            {
                return MaintenanceDatabaseErrorWriter.Write(
                    options.Json,
                    jsonOptions,
                    MaintenanceDatabaseErrorClassifier.Create(
                        "vacuum",
                        options.DbPath,
                        options.ShowPaths,
                        MaintenanceDatabaseFailureKind.NotWritable));
            }

            var queryOnlyDbPath = options.DryRun
                ? StripFileUriFragment(options.DbPath)
                : options.DbPath;
            if (options.DryRun
                && TryCanonicalizeSingleSlashFileUri(queryOnlyDbPath, out var canonicalDbUri))
            {
                queryOnlyDbPath = canonicalDbUri;
            }

            var commandEntryFileSet = DbContext.CaptureVacuumCommandEntryFileSet(
                options.DryRun ? queryOnlyDbPath : options.DbPath,
                cancellationToken);

            if (!DbContext.TryValidateExistingCodeIndexDb(
                    options.DryRun ? queryOnlyDbPath : options.DbPath,
                    requireWritable: !options.DryRun,
                    requireSupportedUserVersion: false,
                    out _,
                    out _,
                    out _,
                    out var validationFailure,
                    out var validationException,
                    cancellationToken,
                    useConnectionPooling: false))
            {
                return MaintenanceDatabaseErrorWriter.Write(
                    options.Json,
                    jsonOptions,
                    MaintenanceDatabaseErrorClassifier.FromValidation(
                        "vacuum",
                        options.DbPath,
                        options.ShowPaths,
                        validationFailure,
                        validationException));
            }

            VacuumResult result;
            string vacuumDataSource;
            DbContext.VacuumGenerationWitness? vacuumGenerationWitness;
            using (var db = DbContext.CreateUnpooled(
                options.DryRun ? DbOpenIntent.QueryOnly : DbOpenIntent.Repair,
                options.DryRun ? queryOnlyDbPath : options.DbPath,
                cancellationToken))
            {
                db.SuppressPlannerStatisticsMaintenanceOnClose();
                result = db.RunIncrementalVacuum(options.DryRun, cancellationToken);
                result = DbContext.ApplyVacuumCommandEntryFileSet(result, commandEntryFileSet);
                if (!options.DryRun)
                    db.CheckpointWalTruncate(cancellationToken);
                vacuumDataSource = db.Connection.DataSource;
                vacuumGenerationWitness = options.DryRun
                    ? null
                    : db.CaptureVacuumGenerationWitness(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!options.DryRun)
            {
                result = DbContext.FinalizeVacuumFileMetricsAfterConnectionClose(
                    result,
                    vacuumDataSource,
                    vacuumGenerationWitness,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    result,
                    CliJsonSerializerContextFactory.Create(jsonOptions).VacuumResult));
            }
            else
            {
                Console.WriteLine(result.DryRun
                    ? $"Vacuum dry run: estimated reclaimable {result.EstimatedPagesReclaimable:N0} page(s) ({result.EstimatedBytesReclaimable:N0} bytes)."
                    : $"Vacuum complete: reclaimed {result.PagesReclaimed:N0} page(s) ({result.BytesReclaimed:N0} bytes).");
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Page size", $"{result.PageSize:N0} bytes"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Pages", $"{result.PageCountBefore:N0} -> {result.PageCountAfter:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Freelist", $"{result.FreelistCountBefore:N0} -> {result.FreelistCountAfter:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("AutoVac", $"{result.AutoVacuumModeBeforeName} -> {result.AutoVacuumModeAfterName}"));
                WriteVacuumByteTransition("Logical DB", result.LogicalDatabaseBytesBefore, result.LogicalDatabaseBytesAfter);
                WriteVacuumByteTransition("Main file", result.MainFileBytesBefore, result.MainFileBytesAfter);
                WriteVacuumByteTransition("WAL", result.WalFileBytesBefore, result.WalFileBytesAfter);
                WriteVacuumByteTransition("SHM", result.ShmFileBytesBefore, result.ShmFileBytesAfter);
                WriteVacuumByteTransition("File set", result.PhysicalFileSetBytesBefore, result.PhysicalFileSetBytesAfter);
                if (result.MaintenanceGuidance.RecommendedCommand != "none")
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Recommend", result.MaintenanceGuidance.RecommendedCommand));
                if (!string.IsNullOrWhiteSpace(result.MaintenanceGuidance.PostMaintenanceFollowUp))
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Follow-up", result.MaintenanceGuidance.PostMaintenanceFollowUp));
            }

            return CommandExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                "vacuum cancelled before it could complete",
                CommandExitCodes.CancelledBySignal,
                "Retry `cdidx vacuum` after the cancelling operation completes.",
                errorCode: CommandErrorCodes.Interrupted);
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return MaintenanceDatabaseErrorWriter.Write(
                options.Json,
                jsonOptions,
                MaintenanceDatabaseErrorClassifier.FromException(
                    "vacuum",
                    options.DbPath,
                    options.ShowPaths,
                    ex));
        }
    }

    private static void WriteVacuumByteTransition(string label, long? before, long? after)
    {
        if (before.HasValue && after.HasValue)
            Console.WriteLine(ConsoleUi.FormatSummaryLine(label, $"{before.Value:N0} -> {after.Value:N0} bytes"));
    }

    private static bool TryCanonicalizeSingleSlashFileUri(string originalDbUri, out string canonicalDbUri)
    {
        canonicalDbUri = originalDbUri;
        if (!originalDbUri.StartsWith("file:/", StringComparison.OrdinalIgnoreCase)
            || originalDbUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!DbPathResolver.TryNormalizeDbPath(originalDbUri, out var normalizedDbPath, out _)
            || SqliteFileUri.StartsWithFileScheme(normalizedDbPath))
        {
            return false;
        }

        var queryIndex = originalDbUri.IndexOf('?', StringComparison.Ordinal);
        var querySuffix = queryIndex >= 0 ? originalDbUri[queryIndex..] : string.Empty;
        canonicalDbUri = CodeIndex.FileUriPolicy.PathToFileUri(Path.GetFullPath(normalizedDbPath)) + querySuffix;
        return true;
    }

    private static string StripFileUriFragment(string dbPath)
    {
        if (!SqliteFileUri.StartsWithFileScheme(dbPath))
            return dbPath;

        var fragmentIndex = dbPath.IndexOf('#', StringComparison.Ordinal);
        return fragmentIndex >= 0 ? dbPath[..fragmentIndex] : dbPath;
    }
}
