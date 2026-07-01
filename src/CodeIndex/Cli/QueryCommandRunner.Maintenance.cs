using System.Text.Json;
using CodeIndex.Database;

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
        var explicitDbPathError = BuildExplicitDbPathParseError(options);
        if (explicitDbPathError != null && explicitDbPathError.Contains(CommandErrorCodes.DbNotFound, StringComparison.Ordinal))
        {
            CommandErrorWriter.WriteStderr(explicitDbPathError);
            CommandErrorWriter.WriteStderr("Hint: point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one.");
            return CommandExitCodes.NotFound;
        }
        if (TryWriteParseError(options, "vacuum"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("vacuum", options))
            return CommandExitCodes.UsageError;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DbContext.TryValidateExistingCodeIndexDb(options.DbPath, out var validationMessage, out var isNotFound, cancellationToken: cancellationToken))
            {
                CommandErrorWriter.WriteStderr($"Error [{(isNotFound ? CommandErrorCodes.DbNotFound : CommandErrorCodes.DbError)}]: {validationMessage}");
                CommandErrorWriter.WriteStderr(isNotFound
                    ? "Hint: point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one."
                    : "Hint: point `--db` at an existing CodeIndex database created by `cdidx index`, then retry `cdidx vacuum`.");
                return isNotFound ? CommandExitCodes.NotFound : CommandExitCodes.DatabaseError;
            }

            using var db = new DbContext(options.DbPath, cancellationToken);
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
    }
}
