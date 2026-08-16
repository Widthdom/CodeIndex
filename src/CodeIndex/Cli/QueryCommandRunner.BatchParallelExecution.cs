using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static int RunBatchParallel(
        BatchExecutionPlan plan,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        BatchParallelSession firstSession,
        CancellationToken cancellationToken)
    {
        var sessions = new BatchParallelSession[plan.Parallelism];
        sessions[0] = firstSession;
        for (var index = 1; index < sessions.Length; index++)
            sessions[index] = new BatchParallelSession(plan.DbPath);
        var availableSessions = new Queue<BatchParallelSession>(sessions);
        using var consoleOwnership = ConsoleStreamOwnership.Enter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdoutRouter = new BatchConsoleRouter(originalOut);
        var stderrRouter = new BatchConsoleRouter(originalError);
        var jsonOutput = new BatchJsonOutputWriter(
            originalOut,
            plan.MaxOutputChars,
            BatchTerminalOutputReserveChars,
            jsonOptions);
        var state = new BatchExecutionState();
        var replayTracker = new BatchParallelReplayTracker();
        using var stopProducing = new CancellationTokenSource();
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            stopProducing.Token);
        var batchInput = GetBatchInputPump(Console.In);
        var input = Channel.CreateBounded<BatchParallelPendingItem>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });

        Console.SetOut(stdoutRouter);
        Console.SetError(stderrRouter);
        try
        {
            var producerPlan = new BatchParallelProducerPlan(
                plan,
                jsonOptions,
                state,
                replayTracker,
                batchInput,
                input.Writer,
                new BatchParallelProducerCancellation(
                    stopProducing.Token,
                    producerCancellation.Token,
                    cancellationToken));
            var producer = Task.Run(
                () => ProduceBatchItemsAsync(producerPlan),
                CancellationToken.None);
            var active = new Queue<BatchActiveItem>();
            var consumerPlan = new BatchParallelConsumerPlan(
                plan,
                jsonOptions,
                appVersion,
                availableSessions,
                new BatchParallelOutputServices(stdoutRouter, stderrRouter, jsonOutput),
                state,
                replayTracker,
                cancellationToken);

            try
            {
                ConsumeBatchItemsInOrder(in consumerPlan, input.Reader, active);
            }
            catch
            {
                stopProducing.Cancel();
                DrainBatchWorkers(active, preserveEarlierFailure: true);

                try
                {
                    producer.GetAwaiter().GetResult();
                }
                catch
                {
                    // Preserve the first failure from the consumer or ordered worker.
                }
                throw;
            }

            if (state.OutputLimitReached)
            {
                stopProducing.Cancel();
                DrainBatchWorkers(active, preserveEarlierFailure: false);
            }

            try
            {
                producer.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (
                state.OutputLimitReached
                && !cancellationToken.IsCancellationRequested)
            {
            }

            if (state.OutputLimitReached && !cancellationToken.IsCancellationRequested)
            {
                batchInput.ReplayBeforeBufferedInput(
                    replayTracker.TakeForReplay(state));
            }

            if (state.CancellationObserved)
                state.FirstFailure = CommandExitCodes.CancelledBySignal;

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
                jsonOutput);
            return state.FirstFailure;
        }
        finally
        {
            try
            {
                foreach (var session in sessions)
                    session.Dispose();
            }
            finally
            {
                ConsoleStreamOwnership.Restore(originalOut, originalError);
            }
        }
    }

    private static async Task ProduceBatchItemsAsync(BatchParallelProducerPlan producer)
    {
        var plan = producer.ExecutionPlan;
        try
        {
            while (!producer.Cancellation.StopToken.IsCancellationRequested)
            {
                if (producer.Cancellation.CallerToken.IsCancellationRequested)
                {
                    producer.State.CancellationObserved = true;
                    break;
                }

                var pumpedLine = await producer.Input.ReadAsync(producer.Cancellation.ReadToken)
                    .ConfigureAwait(false);
                if (pumpedLine is null)
                    break;

                var currentLine = pumpedLine.Value;
                var preparation = PrepareBatchLine(
                    in currentLine,
                    in plan,
                    producer.State,
                    producer.JsonOptions,
                    writeDiagnostics: false,
                    producer.Cancellation.CallerToken,
                    out var item);
                if (preparation == BatchLinePreparationKind.Blank)
                    continue;
                if (preparation == BatchLinePreparationKind.CancellationWithoutRecord)
                    break;

                producer.ReplayTracker.Register(in currentLine, preparation);
                var pendingItem = new BatchParallelPendingItem(
                    item,
                    currentLine.Sequence);
                switch (preparation)
                {
                    case BatchLinePreparationKind.CancellationRecord:
                    case BatchLinePreparationKind.InputLimit:
                        await producer.Output.WriteAsync(
                                pendingItem,
                                producer.Cancellation.StopToken)
                            .ConfigureAwait(false);
                        RecordPreparedBatchLine(producer.State, preparation);
                        producer.ReplayTracker.MarkCountersRecorded(currentLine.Sequence);
                        break;

                    case BatchLinePreparationKind.LineLengthError:
                        await producer.Output.WriteAsync(
                                pendingItem,
                                producer.Cancellation.StopToken)
                            .ConfigureAwait(false);
                        RecordPreparedBatchLine(producer.State, preparation);
                        producer.ReplayTracker.MarkCountersRecorded(currentLine.Sequence);
                        continue;

                    case BatchLinePreparationKind.ParseError:
                    case BatchLinePreparationKind.Command:
                        RecordPreparedBatchLine(producer.State, preparation);
                        producer.ReplayTracker.MarkCountersRecorded(currentLine.Sequence);
                        BatchParallelItemPreparedForTesting?.Invoke(item.LineNumber);
                        await producer.Output.WriteAsync(
                                pendingItem,
                                producer.Cancellation.StopToken)
                            .ConfigureAwait(false);
                        continue;

                    default:
                        throw new InvalidOperationException(
                            $"Unknown batch line preparation kind '{preparation}'.");
                }

                break;
            }

            producer.Output.TryComplete();
        }
        catch (OperationCanceledException) when (producer.Cancellation.ReadToken.IsCancellationRequested)
        {
            if (producer.Cancellation.CallerToken.IsCancellationRequested)
                producer.State.CancellationObserved = true;
            producer.Output.TryComplete();
        }
        catch (Exception ex)
        {
            producer.Output.TryComplete(ex);
            throw;
        }
    }

    private static void ConsumeBatchItemsInOrder(
        in BatchParallelConsumerPlan consumer,
        ChannelReader<BatchParallelPendingItem> input,
        Queue<BatchActiveItem> active)
    {
        while (!consumer.State.OutputLimitReached)
        {
            FillBatchWorkerQueue(in consumer, input, active);
            if (ShouldConsumeOldestBatchItem(in consumer, input, active))
            {
                ConsumeOldestBatchItem(in consumer, active);
                continue;
            }

            if (active.Count == 0 && input.Completion.IsCompleted)
                break;
            if (active.Count > 0 && active.Peek().Result.IsCompleted)
                continue;
            if (!WaitForBatchInputOrOldestWorker(input, active))
                break;
        }
    }

    private static void FillBatchWorkerQueue(
        in BatchParallelConsumerPlan consumer,
        ChannelReader<BatchParallelPendingItem> input,
        Queue<BatchActiveItem> active)
    {
        while (active.Count < consumer.ExecutionPlan.Parallelism
               && input.TryRead(out var pendingItem))
        {
            consumer.ReplayTracker.Commit(pendingItem.InputSequence);
            var item = pendingItem.Item;
            BatchParallelSession? session = null;
            Task<BatchParallelCommandResult?> result;
            if (item.Error is not null)
            {
                result = Task.FromResult<BatchParallelCommandResult?>(null);
            }
            else
            {
                session = consumer.AvailableSessions.Dequeue();
                var assignedSession = session;
                var executionPlan = consumer.ExecutionPlan;
                var stdoutRouter = consumer.Output.StdoutRouter;
                var stderrRouter = consumer.Output.StderrRouter;
                var jsonOptions = consumer.JsonOptions;
                var appVersion = consumer.AppVersion;
                var cancellationToken = consumer.CancellationToken;
                result = Task.Run<BatchParallelCommandResult?>(
                    () => RunBatchParallelCommand(
                        item.LineNumber,
                        item.CommandName!,
                        item.Arguments,
                        executionPlan.DbPath,
                        executionPlan.DbPathExplicit,
                        assignedSession,
                        stdoutRouter,
                        stderrRouter,
                        jsonOptions,
                        appVersion,
                        cancellationToken));
            }
            active.Enqueue(new BatchActiveItem(item, result, session));
        }
    }

    private static bool ShouldConsumeOldestBatchItem(
        in BatchParallelConsumerPlan consumer,
        ChannelReader<BatchParallelPendingItem> input,
        Queue<BatchActiveItem> active)
        => active.Count > 0
           && (active.Peek().Result.IsCompleted
               || active.Count == consumer.ExecutionPlan.Parallelism
               || input.Completion.IsCompleted);

    private static void ConsumeOldestBatchItem(
        in BatchParallelConsumerPlan consumer,
        Queue<BatchActiveItem> active)
    {
        var activeItem = active.Dequeue();
        var item = activeItem.Item;
        var result = activeItem.Result.GetAwaiter().GetResult();
        if (activeItem.Session is not null)
            consumer.AvailableSessions.Enqueue(activeItem.Session);
        if (item.Error is not null)
        {
            if (item.Error.ExitCode is CommandExitCodes.CancelledBySignal
                or CommandExitCodes.LegacyInterrupted)
            {
                consumer.State.CancellationObserved = true;
            }
            RecordBatchFirstFailure(consumer.State, item.Error.ExitCode);

            if (item.Terminal)
            {
                consumer.Output.JsonOutput.WriteTerminal(
                    BuildBatchLineErrorJson(item.LineNumber, item.Error));
                return;
            }

            if (!WriteBatchLineErrorJson(item.LineNumber, item.Error, consumer.Output.JsonOutput))
            {
                WriteBatchOutputLimitErrorJson(
                    item.LineNumber,
                    commandName: null,
                    item.Error.ExitCode,
                    consumer.ExecutionPlan.MaxOutputChars,
                    consumer.Output.JsonOutput);
                consumer.State.OutputLimitReached = true;
            }
            return;
        }

        if (result is null)
        {
            throw new InvalidOperationException(
                "A parallel batch command completed without a result.");
        }
        var completedResult = result.Value;
        if (WriteBatchCommandRecordJson(
                item.LineNumber,
                item.CommandName!,
                item.Arguments,
                completedResult.ExitCode,
                completedResult.Stdout,
                completedResult.Stderr,
                completedResult.Error,
                ClassifyBatchOutput(item.CommandName!, item.Arguments),
                consumer.ExecutionPlan.IncludeRawStreams,
                consumer.Output.JsonOutput))
        {
            if (completedResult.ExitCode != CommandExitCodes.Success)
            {
                if (completedResult.ExitCode is CommandExitCodes.CancelledBySignal
                    or CommandExitCodes.LegacyInterrupted)
                {
                    consumer.State.CancellationObserved = true;
                }
                consumer.State.CommandFailures++;
                RecordBatchFirstFailure(consumer.State, completedResult.ExitCode);
            }
            return;
        }

        WriteBatchOutputLimitErrorJson(
            item.LineNumber,
            item.CommandName,
            completedResult.ExitCode,
            consumer.ExecutionPlan.MaxOutputChars,
            consumer.Output.JsonOutput);
        consumer.State.OutputLimitReached = true;
        consumer.State.CommandFailures++;
        RecordBatchFirstFailure(consumer.State, CommandExitCodes.InvalidArgument);
    }

    private static bool WaitForBatchInputOrOldestWorker(
        ChannelReader<BatchParallelPendingItem> input,
        Queue<BatchActiveItem> active)
    {
        using var waitCancellation = new CancellationTokenSource();
        var waitForInput = input.WaitToReadAsync(waitCancellation.Token).AsTask();
        if (active.Count == 0)
            return waitForInput.GetAwaiter().GetResult();

        var completed = Task.WhenAny(active.Peek().Result, waitForInput)
            .GetAwaiter()
            .GetResult();
        if (ReferenceEquals(completed, active.Peek().Result))
        {
            waitCancellation.Cancel();
            try
            {
                waitForInput.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
            {
            }
        }

        return true;
    }

    private static void DrainBatchWorkers(
        Queue<BatchActiveItem> active,
        bool preserveEarlierFailure)
    {
        while (active.Count > 0)
        {
            if (!preserveEarlierFailure)
            {
                active.Dequeue().Result.GetAwaiter().GetResult();
                continue;
            }

            try
            {
                active.Dequeue().Result.GetAwaiter().GetResult();
            }
            catch
            {
                // Preserve the first failure while ensuring sibling workers have exited.
            }
        }
    }

    private static BatchParallelCommandResult RunBatchParallelCommand(
        int lineNumber,
        string commandName,
        string[] subArgs,
        string dbPath,
        bool dbPathExplicit,
        BatchParallelSession session,
        BatchConsoleRouter stdoutRouter,
        BatchConsoleRouter stderrRouter,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken)
    {
        using var stdout = new BatchBoundedStringWriter(BatchMaxCapturedOutputChars, "stdout");
        using var stderr = new BatchBoundedStringWriter(BatchMaxCapturedOutputChars, "stderr");
        using var stdoutRouterRegistration = ScopedConsoleOutput.Register(stdoutRouter);
        using var stderrRouterRegistration = ScopedConsoleError.Register(stderrRouter);
        using var stdoutScope = stdoutRouter.Push(stdout);
        using var stderrScope = stderrRouter.Push(stderr);
        var exitCode = CommandExitCodes.DatabaseError;
        JsonObject? error = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.TryGetReader(cancellationToken, out var reader, out var validationReason))
            {
                exitCode = WriteInvalidCodeIndexDbError(dbPath, validationReason, json: false, jsonOptions);
            }
            else
            {
                s_batchDatabaseContext = new BatchDatabaseContext(
                    reader,
                    dbPath,
                    dbPathExplicit);
                BatchParallelCommandStartedForTesting?.Invoke(lineNumber);
                cancellationToken.ThrowIfCancellationRequested();
                exitCode = RunBatchQueryCommand(commandName, subArgs, jsonOptions, appVersion, cancellationToken);
                BatchParallelCommandCompletedForTesting?.Invoke(lineNumber);
            }
        }
        catch (BatchOutputCaptureLimitExceededException ex)
        {
            exitCode = CommandExitCodes.InvalidArgument;
            error = BuildBatchCaptureLimitError(ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            exitCode = CommandExitCodes.CancelledBySignal;
            error = BuildBatchCancellationError();
        }
        catch (TimeoutException)
        {
            exitCode = CommandExitCodes.RuntimeError;
            error = BuildBatchTimeoutError();
        }
        catch (Exception ex)
        {
            exitCode = CommandExitCodes.RuntimeError;
            error = BuildBatchTypedError(
                "batch command failed without affecting other batch items.",
                exitCode,
                "Retry the item directly if command-specific diagnostics are required.",
                CommandErrorCodes.CommandFailed,
                SafeDiagnosticFormatter.FormatCategoryType(
                    "batch_command_failure",
                    ex.GetType().Name),
                "command");
        }
        finally
        {
            s_batchDatabaseContext = null;
            s_activeQueryProjectRoot = null;
        }

        return new BatchParallelCommandResult(exitCode, stdout.ToString(), stderr.ToString(), error);
    }

    private readonly record struct BatchParallelProducerPlan(
        BatchExecutionPlan ExecutionPlan,
        JsonSerializerOptions JsonOptions,
        BatchExecutionState State,
        BatchParallelReplayTracker ReplayTracker,
        BatchInputPump Input,
        ChannelWriter<BatchParallelPendingItem> Output,
        BatchParallelProducerCancellation Cancellation);

    private readonly record struct BatchParallelProducerCancellation(
        CancellationToken StopToken,
        CancellationToken ReadToken,
        CancellationToken CallerToken);

    private readonly record struct BatchParallelConsumerPlan(
        BatchExecutionPlan ExecutionPlan,
        JsonSerializerOptions JsonOptions,
        string AppVersion,
        Queue<BatchParallelSession> AvailableSessions,
        BatchParallelOutputServices Output,
        BatchExecutionState State,
        BatchParallelReplayTracker ReplayTracker,
        CancellationToken CancellationToken);

    private readonly record struct BatchParallelOutputServices(
        BatchConsoleRouter StdoutRouter,
        BatchConsoleRouter StderrRouter,
        BatchJsonOutputWriter JsonOutput);

    private readonly record struct BatchActiveItem(
        BatchPendingItem Item,
        Task<BatchParallelCommandResult?> Result,
        BatchParallelSession? Session);

    private readonly record struct BatchParallelPendingItem(
        BatchPendingItem Item,
        long InputSequence);

    private readonly record struct BatchParallelReplayEntry(
        BatchPumpedLine Input,
        BatchLinePreparationKind Preparation,
        bool CountersRecorded);

    private sealed class BatchParallelReplayTracker
    {
        // A bounded channel slot, the producer's local item, and the consumer's
        // just-read item can overlap until the consumer records its commit.
        private const int Capacity = 3;
        private readonly object _gate = new();
        private readonly List<BatchParallelReplayEntry> _uncommitted = new(Capacity);

        public void Register(
            in BatchPumpedLine input,
            BatchLinePreparationKind preparation)
        {
            lock (_gate)
            {
                if (_uncommitted.Count == Capacity)
                {
                    throw new InvalidOperationException(
                        "Parallel batch input ownership exceeded its bounded channel window.");
                }

                _uncommitted.Add(new BatchParallelReplayEntry(
                    input,
                    preparation,
                    CountersRecorded: false));
            }
        }

        public void MarkCountersRecorded(long sequence)
        {
            lock (_gate)
            {
                for (var index = 0; index < _uncommitted.Count; index++)
                {
                    var entry = _uncommitted[index];
                    if (entry.Input.Sequence != sequence)
                        continue;

                    _uncommitted[index] = entry with { CountersRecorded = true };
                    return;
                }

                // The consumer can commit the line immediately after the
                // channel write completes, before the producer reaches here.
                // A committed line no longer needs rollback metadata.
            }
        }

        public void Commit(long sequence)
        {
            lock (_gate)
            {
                for (var index = 0; index < _uncommitted.Count; index++)
                {
                    if (_uncommitted[index].Input.Sequence != sequence)
                        continue;

                    _uncommitted.RemoveAt(index);
                    return;
                }
            }

            throw new InvalidOperationException(
                "Parallel batch input was dispatched without a matching ownership lease.");
        }

        public BatchPumpedLine[] TakeForReplay(BatchExecutionState state)
        {
            BatchParallelReplayEntry[] entries;
            lock (_gate)
            {
                entries = [.. _uncommitted];
                _uncommitted.Clear();
            }

            Array.Sort(
                entries,
                static (left, right) => left.Input.Sequence.CompareTo(right.Input.Sequence));
            var replay = new BatchPumpedLine[entries.Length];
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                replay[index] = entry.Input;
                state.InputLinesRead--;
                if (!entry.CountersRecorded)
                    continue;

                if (entry.Preparation == BatchLinePreparationKind.Command)
                {
                    state.CommandsProcessed--;
                }
                else
                {
                    state.LineErrors--;
                    if (entry.Preparation == BatchLinePreparationKind.InputLimit)
                        state.InputLimitReached = false;
                }
            }

            if (state.InputLinesRead < 0
                || state.CommandsProcessed < 0
                || state.LineErrors < 0)
            {
                throw new InvalidOperationException(
                    "Parallel batch replay produced invalid committed counters.");
            }

            return replay;
        }
    }
}
