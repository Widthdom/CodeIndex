using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal const int DefaultUnusedAnalysisTimeoutMs = 30_000;

    internal static int RunBoundedUnusedAnalysis(DbReader reader, QueryCommandOptions options,
        JsonSerializerOptions jsonOptions, int timeoutMs, CancellationToken cancellationToken, Func<int> action)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        using var cancellationScope = reader.BeginCancellationScope(deadline.Token);
        using var progress = ConsoleUi.ShouldUseProgressAnimation()
            && (options.Progress || ConsoleUi.ShouldUseInteractiveStandardError())
                ? new ConsoleUi.UnusedProgress(Console.Error)
                : null;
        int? completedExitCode = null;
        try
        {
            var exitCode = reader.RunWithCancellationInterrupt(() =>
            {
                var result = action();
                completedExitCode = result;
                return result;
            });
            progress?.Finish(exitCode == CommandExitCodes.Success ? "completed" : "finished");
            return exitCode;
        }
        catch (Exception exception) when (deadline.IsCancellationRequested
            && exception is OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Output is already complete if cancellation raced the interrupt scope's final check.
            if (completedExitCode.HasValue)
            {
                progress?.Finish("finished");
                return completedExitCode.Value;
            }
            var state = cancellationToken.IsCancellationRequested ? "cancelled" : "time_budget_exceeded";
            progress?.Finish(state);
            const string guidance = "Narrow --path/--lang or increase --analysis-timeout-ms and restart. --limit and --max-json-bytes only bound output.";
            if (options.Json)
            {
                var payload = new JsonObject
                {
                    ["analysis_complete"] = false,
                    ["analysis_state"] = state,
                    ["analysis_timeout_ms"] = timeoutMs,
                    ["total_count_authoritative"] = false,
                    ["results"] = new JsonArray(),
                    ["recovery_guidance"] = guidance
                };
                var writeCode = WriteJsonPayloadWithOptionalByteLimit(payload, options, jsonOptions,
                    "unused", "incomplete unused analysis", guidance);
                if (writeCode != CommandExitCodes.Success)
                    return writeCode;
            }
            else
                CommandErrorWriter.WriteStderr($"unused: analysis incomplete ({state}). {guidance}");
            return cancellationToken.IsCancellationRequested ? CommandExitCodes.CancelledBySignal : 11;
        }
    }
}
