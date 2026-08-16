using System.Text.Json;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static int ExecuteBatch(
        in BatchExecutionPlan plan,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken)
    {
        if (plan.Parallelism > 1)
        {
            BatchParallelSession? firstSession = null;
            DbContext? validationDb = null;
            try
            {
                validationDb = new DbContext(DbOpenIntent.QueryOnly, plan.DbPath, cancellationToken);
                BatchParallelDatabaseValidatingForTesting?.Invoke();
                if (!validationDb.TryValidateIsCodeIndexDb(out var validationReason))
                    return WriteInvalidCodeIndexDbError(plan.DbPath, validationReason, json: false, jsonOptions);
                var transferredDb = validationDb;
                validationDb = null;
                firstSession = BatchParallelSession.FromValidated(
                    plan.DbPath,
                    transferredDb,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                firstSession?.Dispose();
                return WriteBatchSetupCancellationSummary(
                    plan.MaxInputLines,
                    plan.MaxOutputChars,
                    plan.Parallelism,
                    jsonOptions);
            }
            catch
            {
                firstSession?.Dispose();
                throw;
            }
            finally
            {
                validationDb?.Dispose();
            }

            try
            {
                return RunBatchParallel(
                    plan,
                    jsonOptions,
                    appVersion,
                    firstSession!,
                    cancellationToken);
            }
            catch
            {
                firstSession?.Dispose();
                throw;
            }
        }

        return RunBatchSerial(in plan, jsonOptions, appVersion, cancellationToken);
    }

    private static int RunBatchSerial(
        in BatchExecutionPlan plan,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            using var db = new DbContext(DbOpenIntent.QueryOnly, plan.DbPath, cancellationToken);
            if (!db.TryValidateIsCodeIndexDb(out var validationReason))
                return WriteInvalidCodeIndexDbError(plan.DbPath, validationReason, json: false, jsonOptions);

            s_batchDatabaseContext = new BatchDatabaseContext(
                new DbReader(db),
                plan.DbPath,
                plan.DbPathExplicit);
            var jsonOutput = plan.JsonSummary
                ? new BatchJsonOutputWriter(
                    Console.Out,
                    plan.MaxOutputChars,
                    BatchTerminalOutputReserveChars,
                    jsonOptions)
                : null;
            var state = new BatchExecutionState();
            var batchInput = GetBatchInputPump(Console.In);
            while (true)
            {
                BatchPumpedLine? pumpedLine;
                try
                {
                    pumpedLine = batchInput.ReadAsync(cancellationToken)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
                catch (OperationCanceledException) when (
                    plan.JsonSummary
                    && cancellationToken.IsCancellationRequested)
                {
                    state.FirstFailure = CommandExitCodes.CancelledBySignal;
                    state.CancellationObserved = true;
                    break;
                }
                if (pumpedLine is null)
                    break;

                var currentLine = pumpedLine.Value;
                var preparation = PrepareBatchLine(
                    in currentLine,
                    in plan,
                    state,
                    jsonOptions,
                    writeDiagnostics: !plan.JsonSummary,
                    cancellationToken,
                    out var item);
                switch (preparation)
                {
                    case BatchLinePreparationKind.Blank:
                        continue;

                    case BatchLinePreparationKind.CancellationWithoutRecord:
                        state.FirstFailure = CommandExitCodes.CancelledBySignal;
                        break;

                    case BatchLinePreparationKind.CancellationRecord:
                        state.FirstFailure = CommandExitCodes.CancelledBySignal;
                        if (!WriteBatchLineErrorJson(item.LineNumber, item.Error!, jsonOutput!))
                        {
                            WriteBatchOutputLimitErrorJson(
                                item.LineNumber,
                                commandName: null,
                                CommandExitCodes.CancelledBySignal,
                                plan.MaxOutputChars,
                                jsonOutput!);
                            state.OutputLimitReached = true;
                        }
                        RecordPreparedBatchLine(state, preparation);
                        break;

                    case BatchLinePreparationKind.InputLimit:
                        if (plan.JsonSummary)
                            jsonOutput!.WriteTerminal(BuildBatchLineErrorJson(item.LineNumber, item.Error!));
                        else
                            WriteBatchLineErrorDiagnostic(item.Error!, jsonOptions);
                        RecordPreparedBatchLine(state, preparation);
                        RecordBatchFirstFailure(state, CommandExitCodes.UsageError);
                        break;

                    case BatchLinePreparationKind.LineLengthError:
                        if (plan.JsonSummary)
                        {
                            if (!WriteBatchLineErrorJson(item.LineNumber, item.Error!, jsonOutput!))
                            {
                                WriteBatchOutputLimitErrorJson(
                                    item.LineNumber,
                                    commandName: null,
                                    CommandExitCodes.UsageError,
                                    plan.MaxOutputChars,
                                    jsonOutput!);
                                state.OutputLimitReached = true;
                            }
                        }
                        else
                        {
                            WriteBatchLineErrorDiagnostic(item.Error!, jsonOptions);
                        }
                        RecordPreparedBatchLine(state, preparation);
                        RecordBatchFirstFailure(state, CommandExitCodes.UsageError);
                        if (state.OutputLimitReached)
                            break;
                        continue;

                    case BatchLinePreparationKind.ParseError:
                        if (plan.JsonSummary
                            && !WriteBatchLineErrorJson(item.LineNumber, item.Error!, jsonOutput!))
                        {
                            WriteBatchOutputLimitErrorJson(
                                item.LineNumber,
                                commandName: null,
                                item.Error!.ExitCode,
                                plan.MaxOutputChars,
                                jsonOutput!);
                            state.OutputLimitReached = true;
                        }
                        RecordPreparedBatchLine(state, preparation);
                        RecordBatchFirstFailure(state, item.Error!.ExitCode);
                        if (state.OutputLimitReached)
                            break;
                        continue;

                    case BatchLinePreparationKind.Command:
                        RecordPreparedBatchLine(state, preparation);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unknown batch line preparation kind '{preparation}'.");
                }

                if (preparation != BatchLinePreparationKind.Command)
                    break;

                var batchResult = plan.JsonSummary
                    ? RunBatchQueryCommandWithJsonRecord(
                        item.LineNumber,
                        item.CommandName!,
                        item.Arguments,
                        plan.MaxOutputChars,
                        plan.IncludeRawStreams,
                        jsonOutput!,
                        jsonOptions,
                        appVersion,
                        cancellationToken)
                    : new BatchCommandRunResult(
                        RunBatchQueryCommand(
                            item.CommandName!,
                            item.Arguments,
                            jsonOptions,
                            appVersion,
                            cancellationToken),
                        OutputLimitReached: false,
                        CancellationObserved: false);
                if (batchResult.ExitCode != CommandExitCodes.Success)
                {
                    state.CommandFailures++;
                    RecordBatchFirstFailure(state, batchResult.ExitCode);
                }
                if (batchResult.OutputLimitReached)
                {
                    state.OutputLimitReached = true;
                    break;
                }
                if (batchResult.CancellationObserved)
                {
                    state.FirstFailure = CommandExitCodes.CancelledBySignal;
                    break;
                }
            }

            if (plan.JsonSummary)
            {
                WriteBatchSummaryJson(
                    state.InputLinesRead,
                    state.CommandsProcessed,
                    state.LineErrors,
                    state.CommandFailures,
                    state.FirstFailure,
                    state.OutputLimitReached,
                    state.InputLimitReached,
                    plan.MaxInputLines,
                    plan.MaxOutputChars,
                    plan.Parallelism,
                    jsonOutput!);
            }

            return state.FirstFailure;
        }
        catch (OperationCanceledException) when (
            plan.JsonSummary
            && cancellationToken.IsCancellationRequested)
        {
            return WriteBatchSetupCancellationSummary(
                plan.MaxInputLines,
                plan.MaxOutputChars,
                plan.Parallelism,
                jsonOptions);
        }
        finally
        {
            s_batchDatabaseContext = null;
        }
    }

    private static BatchLinePreparationKind PrepareBatchLine(
        in BatchPumpedLine pumpedLine,
        in BatchExecutionPlan plan,
        BatchExecutionState state,
        JsonSerializerOptions jsonOptions,
        bool writeDiagnostics,
        CancellationToken cancellationToken,
        out BatchPendingItem item)
    {
        state.InputLinesRead++;
        BatchInputLineReadForTesting?.Invoke(state.InputLinesRead);
        if (cancellationToken.IsCancellationRequested)
        {
            if (!plan.JsonSummary)
                cancellationToken.ThrowIfCancellationRequested();

            state.CancellationObserved = true;
            if (pumpedLine.ExceededLimit || !string.IsNullOrWhiteSpace(pumpedLine.Line))
            {
                item = new BatchPendingItem(
                    state.InputLinesRead,
                    null,
                    [],
                    BuildBatchCancellationLineError(state.InputLinesRead),
                    Terminal: true);
                return BatchLinePreparationKind.CancellationRecord;
            }

            item = default;
            return BatchLinePreparationKind.CancellationWithoutRecord;
        }

        if (state.InputLinesRead > plan.MaxInputLines)
        {
            var lineError = new BatchLineError(
                $"batch input exceeds the {plan.MaxInputLines} line limit.",
                CommandExitCodes.UsageError,
                Hint: "Split the request into smaller batch invocations.",
                ErrorCode: CommandErrorCodes.UsageError,
                Category: "batch_input_line_limit");
            item = new BatchPendingItem(
                state.InputLinesRead,
                null,
                [],
                lineError,
                Terminal: true);
            return BatchLinePreparationKind.InputLimit;
        }

        if (pumpedLine.ExceededLimit)
        {
            var lineError = new BatchLineError(
                $"batch line {state.InputLinesRead} exceeds the {BatchMaxLineChars} character limit.",
                CommandExitCodes.UsageError,
                Hint: "Split the command across smaller arguments or reduce the input record.",
                ErrorCode: CommandErrorCodes.UsageError,
                Category: "batch_input_line_length_limit");
            item = new BatchPendingItem(
                state.InputLinesRead,
                null,
                [],
                lineError,
                Terminal: false);
            return BatchLinePreparationKind.LineLengthError;
        }

        if (string.IsNullOrWhiteSpace(pumpedLine.Line))
        {
            item = default;
            return BatchLinePreparationKind.Blank;
        }

        if (!TryParseBatchLine(
                pumpedLine.Line,
                state.InputLinesRead,
                jsonOptions,
                writeDiagnostics,
                out var commandName,
                out var subArgs,
                out _,
                out var parseError))
        {
            item = new BatchPendingItem(
                state.InputLinesRead,
                null,
                [],
                parseError ?? BuildGenericBatchLineError(state.InputLinesRead),
                Terminal: false);
            return BatchLinePreparationKind.ParseError;
        }

        item = new BatchPendingItem(
            state.InputLinesRead,
            commandName,
            subArgs,
            null,
            Terminal: false);
        return BatchLinePreparationKind.Command;
    }

    private static void RecordPreparedBatchLine(
        BatchExecutionState state,
        BatchLinePreparationKind preparation)
    {
        if (preparation == BatchLinePreparationKind.Command)
        {
            state.CommandsProcessed++;
            return;
        }

        state.LineErrors++;
        if (preparation == BatchLinePreparationKind.InputLimit)
            state.InputLimitReached = true;
    }

    private static void RecordBatchFirstFailure(BatchExecutionState state, int exitCode)
    {
        if (state.FirstFailure == CommandExitCodes.Success)
            state.FirstFailure = exitCode;
    }

    private readonly record struct BatchExecutionPlan(
        string DbPath,
        bool DbPathExplicit,
        bool JsonSummary,
        int MaxInputLines,
        int MaxOutputChars,
        int Parallelism,
        bool IncludeRawStreams);

    private sealed class BatchExecutionState
    {
        public int InputLinesRead { get; set; }
        public int CommandsProcessed { get; set; }
        public int LineErrors { get; set; }
        public int CommandFailures { get; set; }
        public int FirstFailure { get; set; } = CommandExitCodes.Success;
        public bool OutputLimitReached { get; set; }
        public bool InputLimitReached { get; set; }
        public bool CancellationObserved { get; set; }
    }

    private enum BatchLinePreparationKind
    {
        Blank,
        Command,
        ParseError,
        LineLengthError,
        InputLimit,
        CancellationWithoutRecord,
        CancellationRecord,
    }
}
