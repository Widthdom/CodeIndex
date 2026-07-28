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

            if (!DbContext.TryValidateExistingCodeIndexDb(
                    options.DbPath,
                    requireWritable: !options.DryRun,
                    requireSupportedUserVersion: false,
                    out _,
                    out var isNotFound,
                    out var isSchemaTooNew,
                    out var validationException,
                    cancellationToken))
            {
                return MaintenanceDatabaseErrorWriter.Write(
                    options.Json,
                    jsonOptions,
                    MaintenanceDatabaseErrorClassifier.FromValidation(
                        "vacuum",
                        options.DbPath,
                        options.ShowPaths,
                        isNotFound,
                        isSchemaTooNew,
                        validationException));
            }

            using var db = new DbContext(
                options.DryRun ? DbOpenIntent.QueryOnly : DbOpenIntent.Repair,
                options.DbPath,
                cancellationToken);
            var result = db.RunIncrementalVacuum(options.DryRun, cancellationToken);
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
}
