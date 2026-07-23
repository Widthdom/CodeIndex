using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodeIndex.Database;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const int BatchMaxCapturedOutputChars = JsonEnvelopeWrapper.MaxCapturedOutputChars;
    internal static Action<int>? BatchParallelCommandStartedForTesting { get; set; }
    internal static Action<int>? BatchParallelCommandCompletedForTesting { get; set; }

    public static int RunBatch(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        string appVersion = "",
        CancellationToken cancellationToken = default)
    {
        var dbPath = Path.Combine(".cdidx", "codeindex.db");
        var dbPathExplicit = false;
        var jsonSummary = false;
        var maxInputLines = BatchDefaultInputLines;
        var maxOutputChars = BatchDefaultTotalOutputChars;
        var maxOutputCharsSpecified = false;
        var parallelism = 1;
        var parallelismSpecified = false;
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (arg == "--json-summary")
            {
                jsonSummary = true;
                continue;
            }

            if (arg == "--db")
            {
                if (i + 1 >= cmdArgs.Length || string.IsNullOrWhiteSpace(cmdArgs[i + 1]))
                {
                    CommandErrorWriter.WriteStderr(BuildMissingOptionValueError("--db"));
                    return CommandExitCodes.UsageError;
                }
                dbPath = cmdArgs[++i];
                dbPathExplicit = true;
                continue;
            }

            if (arg.StartsWith("--db=", StringComparison.Ordinal))
            {
                dbPath = arg["--db=".Length..];
                if (string.IsNullOrWhiteSpace(dbPath))
                {
                    CommandErrorWriter.WriteStderr(BuildMissingOptionValueError("--db"));
                    return CommandExitCodes.UsageError;
                }
                dbPathExplicit = true;
                continue;
            }

            if (arg == "--max-input-lines" || arg.StartsWith("--max-input-lines=", StringComparison.Ordinal))
            {
                if (!TryReadBatchBoundedOption(
                        cmdArgs,
                        ref i,
                        arg,
                        "--max-input-lines",
                        1,
                        BatchMaxInputLines,
                        out maxInputLines))
                {
                    return CommandExitCodes.UsageError;
                }
                continue;
            }

            if (arg == "--max-output-chars" || arg.StartsWith("--max-output-chars=", StringComparison.Ordinal))
            {
                if (!TryReadBatchBoundedOption(
                        cmdArgs,
                        ref i,
                        arg,
                        "--max-output-chars",
                        BatchMinTotalOutputChars,
                        BatchMaxTotalOutputChars,
                        out maxOutputChars))
                {
                    return CommandExitCodes.UsageError;
                }
                maxOutputCharsSpecified = true;
                continue;
            }

            if (arg == "--parallel" || arg.StartsWith("--parallel=", StringComparison.Ordinal))
            {
                if (!TryReadBatchBoundedOption(
                        cmdArgs,
                        ref i,
                        arg,
                        "--parallel",
                        1,
                        BatchMaxParallelism,
                        out parallelism))
                {
                    return CommandExitCodes.UsageError;
                }
                parallelismSpecified = true;
                continue;
            }

            CommandErrorWriter.WriteStderr($"Error: {ConsoleUi.FormatBoundedValue(arg)} is not supported for batch.");
            CommandErrorWriter.WriteStderr($"Usage: {ConsoleUi.GetUsageLine("batch")}");
            return CommandExitCodes.UsageError;
        }

        if (parallelismSpecified && !jsonSummary)
        {
            CommandErrorWriter.WriteStderr("Error: --parallel requires --json-summary so concurrent child output can be isolated and emitted in input order.");
            CommandErrorWriter.WriteStderr($"Usage: {ConsoleUi.GetUsageLine("batch")}");
            return CommandExitCodes.UsageError;
        }
        if (maxOutputCharsSpecified && !jsonSummary)
        {
            CommandErrorWriter.WriteStderr("Error: --max-output-chars requires --json-summary because ordinary batch output streams directly.");
            CommandErrorWriter.WriteStderr($"Usage: {ConsoleUi.GetUsageLine("batch")}");
            return CommandExitCodes.UsageError;
        }

        var isUri = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!isUri && !File.Exists(dbPath))
        {
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.DbNotFound}]: database not found at {FormatDbDiagnosticValue(Path.GetFullPath(dbPath))}");
            CommandErrorWriter.WriteStderr("Hint: create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command.");
            return CommandExitCodes.DatabaseError;
        }

        if (parallelism > 1)
        {
            using (var validationDb = new DbContext(DbOpenIntent.QueryOnly, dbPath, cancellationToken))
            {
                if (!validationDb.TryValidateIsCodeIndexDb(out var validationReason))
                    return WriteInvalidCodeIndexDbError(dbPath, validationReason, json: false, jsonOptions);
            }

            return RunBatchParallel(
                dbPath,
                dbPathExplicit,
                maxInputLines,
                maxOutputChars,
                parallelism,
                jsonOptions,
                appVersion,
                cancellationToken);
        }

        try
        {
            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath, cancellationToken);
            if (!db.TryValidateIsCodeIndexDb(out var validationReason))
                return WriteInvalidCodeIndexDbError(dbPath, validationReason, json: false, jsonOptions);

            s_batchReader = new DbReader(db);
            s_batchDbPath = dbPath;
            s_batchDbPathExplicit = dbPathExplicit;
            var jsonOutput = jsonSummary
                ? new BatchJsonOutputWriter(
                    Console.Out,
                    maxOutputChars,
                    BatchTerminalOutputReserveChars,
                    jsonOptions)
                : null;
            var firstFailure = CommandExitCodes.Success;
            var lineNumber = 0;
            var commandsProcessed = 0;
            var lineErrors = 0;
            var commandFailures = 0;
            var outputLimitReached = false;
            var inputLimitReached = false;
            while (TryReadBatchLine(Console.In, out var line, out var lineExceededLimit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                if (lineNumber > maxInputLines)
                {
                    var lineError = new BatchLineError(
                        $"batch input exceeds the {maxInputLines} line limit.",
                        CommandExitCodes.UsageError,
                        Hint: "Split the request into smaller batch invocations.",
                        ErrorCode: CommandErrorCodes.UsageError,
                        Category: "batch_input_line_limit");
                    if (jsonSummary)
                        jsonOutput!.WriteTerminal(BuildBatchLineErrorJson(lineNumber, lineError));
                    else
                        WriteBatchLineErrorDiagnostic(lineError, jsonOptions);
                    lineErrors++;
                    inputLimitReached = true;
                    if (firstFailure == CommandExitCodes.Success)
                        firstFailure = CommandExitCodes.UsageError;
                    break;
                }

                if (lineExceededLimit)
                {
                    var lineError = new BatchLineError(
                        $"batch line {lineNumber} exceeds the {BatchMaxLineChars} character limit.",
                        CommandExitCodes.UsageError,
                        ErrorCode: CommandErrorCodes.UsageError);
                    if (jsonSummary)
                    {
                        if (!WriteBatchLineErrorJson(lineNumber, lineError, jsonOutput!))
                        {
                            WriteBatchOutputLimitErrorJson(
                                lineNumber,
                                commandName: null,
                                CommandExitCodes.UsageError,
                                maxOutputChars,
                                jsonOutput!);
                            outputLimitReached = true;
                        }
                    }
                    else
                        WriteBatchLineErrorDiagnostic(lineError, jsonOptions);
                    lineErrors++;
                    if (firstFailure == CommandExitCodes.Success)
                        firstFailure = CommandExitCodes.UsageError;
                    if (outputLimitReached)
                        break;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!TryParseBatchLine(line, lineNumber, jsonOptions, !jsonSummary, out var commandName, out var subArgs, out var parseExitCode, out var parseError))
                {
                    if (jsonSummary)
                    {
                        if (!WriteBatchLineErrorJson(
                                lineNumber,
                                parseError ?? BuildGenericBatchLineError(lineNumber),
                                jsonOutput!))
                        {
                            WriteBatchOutputLimitErrorJson(
                                lineNumber,
                                commandName: null,
                                parseExitCode,
                                maxOutputChars,
                                jsonOutput!);
                            outputLimitReached = true;
                        }
                    }
                    lineErrors++;
                    if (firstFailure == CommandExitCodes.Success)
                        firstFailure = parseExitCode;
                    if (outputLimitReached)
                        break;
                    continue;
                }

                commandsProcessed++;
                var batchResult = jsonSummary
                    ? RunBatchQueryCommandWithJsonRecord(
                        lineNumber,
                        commandName,
                        subArgs,
                        maxOutputChars,
                        jsonOutput!,
                        jsonOptions,
                        appVersion,
                        cancellationToken)
                    : new BatchCommandRunResult(
                        RunBatchQueryCommand(commandName, subArgs, jsonOptions, appVersion, cancellationToken),
                        OutputLimitReached: false);
                var exitCode = batchResult.ExitCode;
                if (exitCode != CommandExitCodes.Success)
                {
                    commandFailures++;
                    if (firstFailure == CommandExitCodes.Success)
                        firstFailure = exitCode;
                }
                if (batchResult.OutputLimitReached)
                {
                    outputLimitReached = true;
                    break;
                }
            }

            if (jsonSummary)
                WriteBatchSummaryJson(
                    lineNumber,
                    commandsProcessed,
                    lineErrors,
                    commandFailures,
                    firstFailure,
                    outputLimitReached,
                    inputLimitReached,
                    maxInputLines,
                    maxOutputChars,
                    parallelism,
                    jsonOutput!);

            return firstFailure;
        }
        finally
        {
            s_batchReader = null;
            s_batchDbPath = null;
            s_batchDbPathExplicit = false;
        }
    }

    private static bool TryReadBatchBoundedOption(
        string[] args,
        ref int index,
        string currentArg,
        string optionName,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        string rawValue;
        if (currentArg == optionName)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                CommandErrorWriter.WriteStderr(BuildMissingOptionValueError(optionName));
                return false;
            }
            rawValue = args[++index];
        }
        else
        {
            rawValue = currentArg[(optionName.Length + 1)..];
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                CommandErrorWriter.WriteStderr(BuildMissingOptionValueError(optionName));
                return false;
            }
        }

        if (!int.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            || value < minimum
            || value > maximum)
        {
            CommandErrorWriter.WriteStderr($"Error: {optionName} must be an integer from {minimum} to {maximum}.");
            CommandErrorWriter.WriteStderr($"Usage: {ConsoleUi.GetUsageLine("batch")}");
            return false;
        }

        return true;
    }

    private static int RunBatchParallel(
        string dbPath,
        bool dbPathExplicit,
        int maxInputLines,
        int maxOutputChars,
        int parallelism,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdoutRouter = new BatchConsoleRouter(originalOut);
        var stderrRouter = new BatchConsoleRouter(originalError);
        var jsonOutput = new BatchJsonOutputWriter(
            originalOut,
            maxOutputChars,
            BatchTerminalOutputReserveChars,
            jsonOptions);
        var firstFailure = CommandExitCodes.Success;
        var lineNumber = 0;
        var commandsProcessed = 0;
        var lineErrors = 0;
        var commandFailures = 0;
        var outputLimitReached = false;
        var inputLimitReached = false;
        using var stopProducing = new CancellationTokenSource();
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            stopProducing.Token);
        var input = Channel.CreateBounded<BatchPendingItem>(
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
            var producer = Task.Run(() =>
            {
                try
                {
                    while (!stopProducing.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!TryReadBatchLine(Console.In, out var line, out var lineExceededLimit))
                            break;

                        cancellationToken.ThrowIfCancellationRequested();
                        lineNumber++;
                        if (lineNumber > maxInputLines)
                        {
                            var lineError = new BatchLineError(
                                $"batch input exceeds the {maxInputLines} line limit.",
                                CommandExitCodes.UsageError,
                                Hint: "Split the request into smaller batch invocations.",
                                ErrorCode: CommandErrorCodes.UsageError,
                                Category: "batch_input_line_limit");
                            input.Writer.WriteAsync(
                                    new BatchPendingItem(lineNumber, null, [], lineError, Terminal: true),
                                    producerCancellation.Token)
                                .AsTask()
                                .GetAwaiter()
                                .GetResult();
                            lineErrors++;
                            inputLimitReached = true;
                            break;
                        }

                        if (lineExceededLimit)
                        {
                            var lineError = new BatchLineError(
                                $"batch line {lineNumber} exceeds the {BatchMaxLineChars} character limit.",
                                CommandExitCodes.UsageError,
                                ErrorCode: CommandErrorCodes.UsageError);
                            input.Writer.WriteAsync(
                                    new BatchPendingItem(lineNumber, null, [], lineError, Terminal: false),
                                    producerCancellation.Token)
                                .AsTask()
                                .GetAwaiter()
                                .GetResult();
                            lineErrors++;
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        BatchPendingItem item;
                        if (!TryParseBatchLine(
                                line,
                                lineNumber,
                                jsonOptions,
                                writeDiagnostics: false,
                                out var commandName,
                                out var subArgs,
                                out _,
                                out var parseError))
                        {
                            item = new BatchPendingItem(
                                lineNumber,
                                null,
                                [],
                                parseError ?? BuildGenericBatchLineError(lineNumber),
                                Terminal: false);
                            lineErrors++;
                        }
                        else
                        {
                            item = new BatchPendingItem(
                                lineNumber,
                                commandName,
                                subArgs,
                                null,
                                Terminal: false);
                            commandsProcessed++;
                        }

                        input.Writer.WriteAsync(item, producerCancellation.Token)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult();
                    }

                    input.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    input.Writer.TryComplete(ex);
                    throw;
                }
            });
            var active = new Queue<(
                BatchPendingItem Item,
                Task<BatchParallelCommandResult?> Result)>();

            try
            {
                while (!outputLimitReached)
                {
                    while (active.Count < parallelism && input.Reader.TryRead(out var item))
                    {
                        var result = item.Error is not null
                            ? Task.FromResult<BatchParallelCommandResult?>(null)
                            : Task.Run<BatchParallelCommandResult?>(
                                () => RunBatchParallelCommand(
                                    item.LineNumber,
                                    item.CommandName!,
                                    item.Arguments,
                                    dbPath,
                                    dbPathExplicit,
                                    stdoutRouter,
                                    stderrRouter,
                                    jsonOptions,
                                    appVersion,
                                    cancellationToken),
                                cancellationToken);
                        active.Enqueue((item, result));
                    }

                    if (active.Count > 0
                        && (active.Peek().Result.IsCompleted
                            || active.Count == parallelism
                            || input.Reader.Completion.IsCompleted))
                    {
                        var (item, resultTask) = active.Dequeue();
                        var result = resultTask.GetAwaiter().GetResult();
                        if (item.Error is not null)
                        {
                            if (firstFailure == CommandExitCodes.Success)
                                firstFailure = item.Error.ExitCode;

                            if (item.Terminal)
                            {
                                jsonOutput.WriteTerminal(BuildBatchLineErrorJson(item.LineNumber, item.Error));
                                continue;
                            }

                            if (!WriteBatchLineErrorJson(item.LineNumber, item.Error, jsonOutput))
                            {
                                WriteBatchOutputLimitErrorJson(
                                    item.LineNumber,
                                    commandName: null,
                                    item.Error.ExitCode,
                                    maxOutputChars,
                                    jsonOutput);
                                outputLimitReached = true;
                                break;
                            }
                            continue;
                        }

                        if (result is null)
                        {
                            throw new InvalidOperationException(
                                "A parallel batch command completed without a result.");
                        }
                        if (WriteBatchCommandRecordJson(
                                item.LineNumber,
                                item.CommandName!,
                                item.Arguments,
                                result.ExitCode,
                                result.Stdout,
                                result.Stderr,
                                result.Error,
                                ClassifyBatchOutput(item.CommandName!, item.Arguments),
                                jsonOutput))
                        {
                            if (result.ExitCode != CommandExitCodes.Success)
                            {
                                commandFailures++;
                                if (firstFailure == CommandExitCodes.Success)
                                    firstFailure = result.ExitCode;
                            }
                            continue;
                        }

                        WriteBatchOutputLimitErrorJson(
                            item.LineNumber,
                            item.CommandName,
                            result.ExitCode,
                            maxOutputChars,
                            jsonOutput);
                        outputLimitReached = true;
                        commandFailures++;
                        if (firstFailure == CommandExitCodes.Success)
                            firstFailure = CommandExitCodes.InvalidArgument;
                        break;
                    }

                    if (outputLimitReached)
                        break;
                    if (active.Count == 0 && input.Reader.Completion.IsCompleted)
                        break;
                    if (active.Count > 0 && active.Peek().Result.IsCompleted)
                        continue;

                    using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    var waitForInput = input.Reader.WaitToReadAsync(waitCancellation.Token).AsTask();
                    if (active.Count == 0)
                    {
                        if (!waitForInput.GetAwaiter().GetResult())
                            break;
                        continue;
                    }

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
                }
            }
            catch
            {
                stopProducing.Cancel();
                while (active.Count > 0)
                {
                    try
                    {
                        active.Dequeue().Result.GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Preserve the first failure while ensuring sibling workers have exited.
                    }
                }

                if (producer.IsCompleted)
                {
                    try
                    {
                        producer.GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Preserve the first failure from the consumer or ordered worker.
                    }
                }
                else
                {
                    _ = producer.ContinueWith(
                        static completedProducer => _ = completedProducer.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }
                throw;
            }

            if (outputLimitReached)
            {
                stopProducing.Cancel();
                while (active.Count > 0)
                    active.Dequeue().Result.GetAwaiter().GetResult();
            }

            try
            {
                producer.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (
                outputLimitReached
                && !cancellationToken.IsCancellationRequested)
            {
            }

            WriteBatchSummaryJson(
                lineNumber,
                commandsProcessed,
                lineErrors,
                commandFailures,
                firstFailure,
                outputLimitReached,
                inputLimitReached,
                maxInputLines,
                maxOutputChars,
                parallelism,
                jsonOutput);
            return firstFailure;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static BatchParallelCommandResult RunBatchParallelCommand(
        int lineNumber,
        string commandName,
        string[] subArgs,
        string dbPath,
        bool dbPathExplicit,
        BatchConsoleRouter stdoutRouter,
        BatchConsoleRouter stderrRouter,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken)
    {
        using var stdout = new BatchBoundedStringWriter(BatchMaxCapturedOutputChars, "stdout");
        using var stderr = new BatchBoundedStringWriter(BatchMaxCapturedOutputChars, "stderr");
        using var stdoutRouterRegistration = ScopedConsoleOutput.Register(stdoutRouter);
        using var stdoutScope = stdoutRouter.Push(stdout);
        using var stderrScope = stderrRouter.Push(stderr);
        var exitCode = CommandExitCodes.DatabaseError;
        JsonObject? error = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath, cancellationToken);
            if (!db.TryValidateIsCodeIndexDb(out var validationReason))
            {
                exitCode = WriteInvalidCodeIndexDbError(dbPath, validationReason, json: false, jsonOptions);
            }
            else
            {
                s_batchReader = new DbReader(db);
                s_batchDbPath = dbPath;
                s_batchDbPathExplicit = dbPathExplicit;
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
            throw;
        }
        catch (Exception ex)
        {
            exitCode = CommandExitCodes.DatabaseError;
            error = new JsonObject
            {
                ["message"] = "batch command failed without affecting other batch items.",
                ["error_code"] = CommandErrorCodes.DbError,
                ["category"] = SafeDiagnosticFormatter.FormatCategoryType(
                    "batch_command_failure",
                    ex.GetType().Name),
                ["scope"] = "command",
            };
        }
        finally
        {
            s_batchReader = null;
            s_batchDbPath = null;
            s_batchDbPathExplicit = false;
            s_activeQueryProjectRoot = null;
        }

        return new BatchParallelCommandResult(exitCode, stdout.ToString(), stderr.ToString(), error);
    }

    private static void WriteBatchSummaryJson(
        int inputLinesRead,
        int commandsProcessed,
        int lineErrors,
        int commandFailures,
        int exitCode,
        bool outputLimitReached,
        bool inputLimitReached,
        int inputLineLimit,
        int outputCharLimit,
        int parallelism,
        BatchJsonOutputWriter output)
    {
        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["record"] = "batch_summary",
            ["command"] = "batch",
            ["input_lines_read"] = inputLinesRead,
            ["commands_processed"] = commandsProcessed,
            ["line_errors"] = lineErrors,
            ["command_failures"] = commandFailures,
            ["exit_code"] = exitCode,
            ["output_chars"] = 0,
            ["output_char_limit"] = outputCharLimit,
            ["output_limit_reached"] = outputLimitReached,
            ["input_line_limit"] = inputLineLimit,
            ["input_limit_reached"] = inputLimitReached,
            ["parallelism"] = parallelism,
        };

        output.WriteSummary(payload);
    }

    private static BatchCommandRunResult RunBatchQueryCommandWithJsonRecord(
        int lineNumber,
        string commandName,
        string[] subArgs,
        int outputCharLimit,
        BatchJsonOutputWriter output,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken)
    {
        using var capture = new BatchCommandOutputCapture();
        int exitCode;
        BatchOutputCaptureLimitExceededException? captureLimitExceeded = null;
        try
        {
            capture.Start();
            exitCode = RunBatchQueryCommand(commandName, subArgs, jsonOptions, appVersion, cancellationToken);
        }
        catch (BatchOutputCaptureLimitExceededException ex)
        {
            captureLimitExceeded = ex;
            exitCode = CommandExitCodes.InvalidArgument;
        }
        finally
        {
            capture.Stop();
        }

        JsonObject? error = null;
        if (captureLimitExceeded is not null)
            error = BuildBatchCaptureLimitError(captureLimitExceeded);

        var recordWritten = WriteBatchCommandRecordJson(
            lineNumber,
            commandName,
            subArgs,
            exitCode,
            capture.Stdout,
            capture.Stderr,
            error,
            ClassifyBatchOutput(commandName, subArgs),
            output);
        if (recordWritten)
            return new BatchCommandRunResult(exitCode, OutputLimitReached: false);

        WriteBatchOutputLimitErrorJson(lineNumber, commandName, exitCode, outputCharLimit, output);
        return new BatchCommandRunResult(CommandExitCodes.InvalidArgument, OutputLimitReached: true);
    }

    private static JsonObject BuildBatchCaptureLimitError(BatchOutputCaptureLimitExceededException exception)
    {
        return new JsonObject
        {
            ["message"] = $"batch command {exception.StreamName} exceeded {exception.MaxChars} captured characters.",
            ["hint"] = "Reduce the result set or run cdidx batch without --json-summary for streaming output.",
            ["error_code"] = CommandErrorCodes.UsageError,
            ["max_chars"] = exception.MaxChars,
            ["stream"] = exception.StreamName,
            ["scope"] = "command",
        };
    }

    private static bool WriteBatchCommandRecordJson(
        int lineNumber,
        string commandName,
        string[] subArgs,
        int exitCode,
        string stdout,
        string stderr,
        JsonObject? error,
        BatchOutputKind outputKind,
        BatchJsonOutputWriter output)
    {
        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["record"] = "batch_result",
            ["status"] = exitCode == CommandExitCodes.Success ? "ok" : "error",
            ["line"] = lineNumber,
            ["command"] = commandName,
            ["arguments"] = ToJsonStringArray(subArgs),
            ["exit_code"] = exitCode,
            ["stderr"] = stderr,
        };

        if (exitCode == CommandExitCodes.Success
            && TryParseBatchStructuredOutput(stdout, outputKind, out var resultField, out var structuredOutput))
        {
            payload[resultField] = structuredOutput;
        }
        else
        {
            payload["stdout"] = stdout;
        }

        if (error is not null)
            payload["error"] = error;

        return output.TryWrite(payload);
    }

    private static bool TryParseBatchStructuredOutput(
        string stdout,
        BatchOutputKind outputKind,
        out string resultField,
        out JsonNode? structuredOutput)
    {
        resultField = string.Empty;
        structuredOutput = null;
        if (outputKind == BatchOutputKind.Text || string.IsNullOrWhiteSpace(stdout))
            return false;

        var maxUtf8Bytes = BatchMaxCapturedOutputChars * 4;
        if (outputKind == BatchOutputKind.JsonDocument)
        {
            try
            {
                structuredOutput = BoundedJson.ParseNode(stdout, maxUtf8Bytes, BatchMaxJsonDepth);
                resultField = "result";
                return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return false;
            }
        }

        var results = new JsonArray();
        using var reader = new StringReader(stdout);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                results.Add(BoundedJson.ParseNode(line, maxUtf8Bytes, BatchMaxJsonDepth));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return false;
            }
        }

        if (results.Count == 0)
            return false;

        resultField = "results";
        structuredOutput = results;
        return true;
    }

    private static BatchOutputKind ClassifyBatchOutput(string commandName, string[] args)
    {
        if (commandName == "goto")
            return BatchOutputKind.JsonDocument;

        var jsonRequested = false;
        string? jsonMode = null;
        string? outputFormat = null;
        var compactRequested = false;
        for (var i = 0; i < args.Length && args[i] != "--"; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                jsonRequested = true;
                jsonMode = "ndjson";
            }
            else if (arg.StartsWith("--json=", StringComparison.Ordinal))
            {
                jsonRequested = true;
                jsonMode = arg["--json=".Length..].ToLowerInvariant();
            }
            else if (arg == "--format" && i + 1 < args.Length)
            {
                outputFormat = args[++i].ToLowerInvariant();
            }
            else if (arg.StartsWith("--format=", StringComparison.Ordinal))
            {
                outputFormat = arg["--format=".Length..].ToLowerInvariant();
            }
            else if (arg == "--compact")
            {
                compactRequested = true;
            }
        }

        if (compactRequested)
            return BatchOutputKind.JsonDocument;

        if (outputFormat == "json")
            jsonRequested = true;
        else if (outputFormat is not null
                 && CliOutputFormatCapabilities.TryGet(outputFormat, out var formatCapability)
                 && formatCapability.IsJsonContract)
        {
            return BatchOutputKind.JsonDocument;
        }

        // `audit --summary-only` injects `--format compact` before dispatch, so its
        // effective output contract is JSON even though the original batch argv does
        // not contain an explicit format flag.
        if (commandName == "audit"
            && outputFormat is null
            && HasBatchArgument(args, "--summary-only"))
        {
            return BatchOutputKind.JsonDocument;
        }

        if (!jsonRequested)
            return BatchOutputKind.Text;
        if (jsonMode == "array")
            return BatchOutputKind.JsonDocument;
        if (HasBatchArgument(args, "--count"))
            return BatchOutputKind.JsonDocument;

        if (commandName == "search"
            && HasBatchArgument(args, "--recipe", "--list-recipes", "--group-by", "--count-by", "--summary-only"))
        {
            return BatchOutputKind.JsonDocument;
        }

        if (commandName is "search" or "references" or "callers" or "callees" or "symbols" or "files" or "validate")
            return BatchOutputKind.Ndjson;
        if (commandName == "find" && HasBatchArgument(args, "--all"))
            return BatchOutputKind.Ndjson;

        return BatchOutputKind.JsonDocument;
    }

    private static bool HasBatchArgument(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length && args[i] != "--"; i++)
        {
            if (names.Contains(args[i], StringComparer.Ordinal)
                || names.Any(name => args[i].StartsWith(name + "=", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WriteBatchLineErrorJson(int lineNumber, BatchLineError error, BatchJsonOutputWriter output)
        => output.TryWrite(BuildBatchLineErrorJson(lineNumber, error));

    private static JsonObject BuildBatchLineErrorJson(int lineNumber, BatchLineError error)
    {
        return new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["record"] = "batch_error",
            ["status"] = "error",
            ["line"] = lineNumber,
            ["exit_code"] = error.ExitCode,
            ["stdout"] = string.Empty,
            ["stderr"] = string.Empty,
            ["error"] = ToBatchErrorJson(error),
        };
    }

    private static void WriteBatchOutputLimitErrorJson(
        int lineNumber,
        string? commandName,
        int attemptedExitCode,
        int outputCharLimit,
        BatchJsonOutputWriter output)
    {
        var error = new JsonObject
        {
            ["message"] = $"batch serialized output reached the {outputCharLimit} character limit.",
            ["hint"] = "Split the request into smaller batches or reduce child output with --limit/--top.",
            ["error_code"] = CommandErrorCodes.UsageError,
            ["category"] = "batch_output_limit",
            ["scope"] = "batch",
            ["max_chars"] = outputCharLimit,
            ["attempted_exit_code"] = attemptedExitCode,
        };
        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["record"] = commandName is null ? "batch_error" : "batch_result",
            ["status"] = "error",
            ["line"] = lineNumber,
            ["exit_code"] = CommandExitCodes.InvalidArgument,
            ["stdout"] = string.Empty,
            ["stderr"] = string.Empty,
            ["error"] = error,
        };
        if (commandName is not null)
        {
            payload["command"] = ConsoleUi.FormatBoundedValue(commandName);
            payload["arguments_omitted"] = true;
        }

        output.WriteTerminal(payload);
    }

    private static JsonObject ToBatchErrorJson(BatchLineError error)
    {
        var payload = new JsonObject
        {
            ["message"] = error.Message,
        };

        if (!string.IsNullOrWhiteSpace(error.Hint))
            payload["hint"] = error.Hint;
        if (!string.IsNullOrWhiteSpace(error.ErrorCode))
            payload["error_code"] = error.ErrorCode;
        if (!string.IsNullOrWhiteSpace(error.Category))
            payload["category"] = error.Category;

        return payload;
    }

    private static JsonArray ToJsonStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static bool TryReadBatchLine(TextReader reader, out string? line, out bool exceededLimit)
    {
        line = null;
        exceededLimit = false;
        var builder = new StringBuilder();
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                if (builder.Length == 0 && !exceededLimit)
                    return false;
                line = exceededLimit ? string.Empty : builder.ToString();
                return true;
            }

            var ch = (char)next;
            if (ch == '\n')
            {
                line = exceededLimit ? string.Empty : builder.ToString();
                return true;
            }

            if (exceededLimit)
                continue;
            if (builder.Length >= BatchMaxLineChars)
            {
                exceededLimit = true;
                continue;
            }

            builder.Append(ch);
        }
    }

    private static bool TryParseBatchLine(
        string line,
        int lineNumber,
        JsonSerializerOptions jsonOptions,
        bool writeDiagnostics,
        out string commandName,
        out string[] subArgs,
        out int exitCode,
        out BatchLineError? error)
    {
        commandName = string.Empty;
        subArgs = [];
        exitCode = CommandExitCodes.UsageError;
        error = null;

        try
        {
            using var document = BoundedJson.ParseDocument(line, BatchMaxLineUtf8Bytes, BatchMaxJsonDepth);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return TryParseBatchCommandObject(
                    document.RootElement,
                    lineNumber,
                    jsonOptions,
                    writeDiagnostics,
                    out commandName,
                    out subArgs,
                    out exitCode,
                    out error);
            }

            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                error = new BatchLineError(
                    $"batch line {lineNumber} must be a non-empty JSON string array or a command object.",
                    CommandExitCodes.UsageError,
                    ErrorCode: CommandErrorCodes.UsageError);
                if (writeDiagnostics)
                    WriteBatchLineErrorDiagnostic(error, jsonOptions);
                return false;
            }
            if (document.RootElement.GetArrayLength() > BatchMaxArgumentCount + 1)
            {
                error = new BatchLineError(
                    $"batch line {lineNumber} must contain at most {BatchMaxArgumentCount} command arguments.",
                    CommandExitCodes.UsageError,
                    ErrorCode: CommandErrorCodes.UsageError);
                if (writeDiagnostics)
                    WriteBatchLineErrorDiagnostic(error, jsonOptions);
                return false;
            }

            var values = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    error = new BatchLineError(
                        $"batch line {lineNumber} must contain only strings.",
                        CommandExitCodes.UsageError,
                        ErrorCode: CommandErrorCodes.UsageError);
                    if (writeDiagnostics)
                        WriteBatchLineErrorDiagnostic(error, jsonOptions);
                    return false;
                }
                var value = element.GetString() ?? string.Empty;
                if (value.Length > BatchMaxArgumentChars)
                {
                    error = new BatchLineError(
                        $"batch line {lineNumber} argument {values.Count + 1} exceeds the {BatchMaxArgumentChars} character limit.",
                        CommandExitCodes.UsageError,
                        ErrorCode: CommandErrorCodes.UsageError);
                    if (writeDiagnostics)
                        WriteBatchLineErrorDiagnostic(error, jsonOptions);
                    return false;
                }
                values.Add(value);
            }

            commandName = values[0];
            subArgs = values.Skip(1).ToArray();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            error = new BatchLineError(
                $"batch line {lineNumber} {SafeDiagnosticFormatter.FormatCategoryType("invalid_batch_json", nameof(JsonException))}.",
                CommandExitCodes.UsageError,
                Hint: "ensure each batch input line is a JSON string array or a {\"command\",\"args\"} object.",
                ErrorCode: CommandErrorCodes.UsageError,
                Category: "invalid_batch_json",
                WriteAsJson: true);
            if (writeDiagnostics)
                WriteBatchLineErrorDiagnostic(error, jsonOptions);
            return false;
        }
    }

    private static bool TryParseBatchCommandObject(
        JsonElement root,
        int lineNumber,
        JsonSerializerOptions jsonOptions,
        bool writeDiagnostics,
        out string commandName,
        out string[] subArgs,
        out int exitCode,
        out BatchLineError? error)
    {
        commandName = string.Empty;
        subArgs = [];
        exitCode = CommandExitCodes.UsageError;
        error = null;
        JsonElement commandElement = default;
        JsonElement argumentsElement = default;
        var commandSeen = false;
        var argumentsSeen = false;

        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("command"))
            {
                if (commandSeen)
                {
                    error = BuildBatchObjectError(lineNumber, "must not repeat the command property.");
                    return WriteBatchObjectErrorIfNeeded(error, jsonOptions, writeDiagnostics);
                }
                commandSeen = true;
                commandElement = property.Value;
                continue;
            }

            if (property.NameEquals("args"))
            {
                if (argumentsSeen)
                {
                    error = BuildBatchObjectError(lineNumber, "must not repeat the args property.");
                    return WriteBatchObjectErrorIfNeeded(error, jsonOptions, writeDiagnostics);
                }
                argumentsSeen = true;
                argumentsElement = property.Value;
                continue;
            }

            error = BuildBatchObjectError(lineNumber, "contains an unsupported property.");
            return WriteBatchObjectErrorIfNeeded(error, jsonOptions, writeDiagnostics);
        }

        if (!commandSeen
            || commandElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(commandElement.GetString()))
        {
            error = BuildBatchObjectError(lineNumber, "requires a non-empty string command property.");
            return WriteBatchObjectErrorIfNeeded(error, jsonOptions, writeDiagnostics);
        }

        commandName = commandElement.GetString()!;
        if (commandName.Length > BatchMaxArgumentChars)
        {
            error = BuildBatchObjectError(
                lineNumber,
                $"command exceeds the {BatchMaxArgumentChars} character limit.");
            return WriteBatchObjectErrorIfNeeded(error, jsonOptions, writeDiagnostics);
        }

        if (!argumentsSeen)
            return true;
        if (argumentsElement.ValueKind != JsonValueKind.Array)
        {
            error = BuildBatchObjectError(lineNumber, "requires args to be a JSON string array.");
            return WriteBatchObjectErrorIfNeeded(error, jsonOptions, writeDiagnostics);
        }
        if (argumentsElement.GetArrayLength() > BatchMaxArgumentCount)
        {
            error = BuildBatchObjectError(
                lineNumber,
                $"must contain at most {BatchMaxArgumentCount} command arguments.");
            return WriteBatchObjectErrorIfNeeded(error, jsonOptions, writeDiagnostics);
        }

        var values = new List<string>(argumentsElement.GetArrayLength());
        foreach (var element in argumentsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                error = BuildBatchObjectError(lineNumber, "args must contain only strings.");
                return WriteBatchObjectErrorIfNeeded(error, jsonOptions, writeDiagnostics);
            }

            var value = element.GetString() ?? string.Empty;
            if (value.Length > BatchMaxArgumentChars)
            {
                error = BuildBatchObjectError(
                    lineNumber,
                    $"argument {values.Count + 1} exceeds the {BatchMaxArgumentChars} character limit.");
                return WriteBatchObjectErrorIfNeeded(error, jsonOptions, writeDiagnostics);
            }
            values.Add(value);
        }

        subArgs = values.ToArray();
        return true;
    }

    private static BatchLineError BuildBatchObjectError(int lineNumber, string detail)
        => new(
            $"batch line {lineNumber} command object {detail}",
            CommandExitCodes.UsageError,
            ErrorCode: CommandErrorCodes.UsageError,
            Category: "invalid_batch_command_object");

    private static bool WriteBatchObjectErrorIfNeeded(
        BatchLineError error,
        JsonSerializerOptions jsonOptions,
        bool writeDiagnostics)
    {
        if (writeDiagnostics)
            WriteBatchLineErrorDiagnostic(error, jsonOptions);
        return false;
    }

    private static void WriteBatchLineErrorDiagnostic(BatchLineError error, JsonSerializerOptions jsonOptions)
    {
        if (error.WriteAsJson)
        {
            CommandErrorWriter.WriteJsonOrHuman(
                true,
                jsonOptions,
                error.Message,
                error.ExitCode,
                error.Hint,
                errorCode: error.ErrorCode,
                category: error.Category);
            return;
        }

        CommandErrorWriter.WriteStderr($"Error: {error.Message}");
        if (!string.IsNullOrWhiteSpace(error.Hint))
            CommandErrorWriter.WriteStderr($"Hint: {error.Hint}");
    }

    private static BatchLineError BuildGenericBatchLineError(int lineNumber)
        => new(
            $"batch line {lineNumber} could not be parsed.",
            CommandExitCodes.UsageError,
            ErrorCode: CommandErrorCodes.UsageError);

    private static int RunBatchQueryCommand(
        string commandName,
        string[] subArgs,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken)
    {
        if (!CliCommandCatalog.IsBatchReadOnlyCommand(commandName))
            return WriteBatchUnsupportedCommand(commandName);

        Func<string[], int> runner = commandName switch
        {
            "search" => args => RunSearch(args, jsonOptions, cancellationToken),
            "recipes" => args => RunRecipes(args, jsonOptions, cancellationToken),
            "audit" => args => RunAudit(args, jsonOptions, cancellationToken),
            "definition" => args => RunDefinition(args, jsonOptions),
            "goto" => args => RunGoto(args, jsonOptions),
            "references" => args => RunReferences(args, jsonOptions),
            "callers" => args => RunCallers(args, jsonOptions),
            "callees" => args => RunCallees(args, jsonOptions),
            "symbols" => args => RunSymbols(args, jsonOptions),
            "files" => args => RunFiles(args, jsonOptions),
            "find" => args => RunFind(args, jsonOptions),
            "excerpt" => args => RunExcerpt(args, jsonOptions),
            "map" => args => RunMap(args, jsonOptions),
            "inspect" => args => RunInspect(args, jsonOptions),
            "outline" => args => RunOutline(args, jsonOptions),
            "status" => args => RunStatus(args, jsonOptions, appVersion, cancellationToken),
            "validate" => args => RunValidate(args, jsonOptions),
            "languages" => args => RunLanguages(args, jsonOptions),
            "impact" => args => RunImpact(args, jsonOptions),
            "deps" => args => RunDeps(args, jsonOptions, cancellationToken),
            "unused" => args => RunUnused(args, jsonOptions),
            "hotspots" => args => RunHotspots(args, jsonOptions),
            _ => throw new InvalidOperationException($"Batch schema command '{commandName}' has no dispatcher."),
        };

        return JsonEnvelopeWrapper.ShouldWrap(commandName, subArgs)
            ? JsonEnvelopeWrapper.RunWrapped(commandName, subArgs, appVersion, jsonOptions, runner)
            : runner(subArgs);
    }

    private static int WriteBatchUnsupportedCommand(string commandName)
    {
        CommandErrorWriter.WriteStderr($"Error: batch only supports query and read-only discovery commands; '{commandName}' is not supported.");
        CommandErrorWriter.WriteStderr($"Hint: use one of {string.Join(", ", CliCommandCatalog.BatchReadOnlyCommands)}.");
        return CommandExitCodes.UsageError;
    }

    private sealed record BatchLineError(
        string Message,
        int ExitCode,
        string? Hint = null,
        string? ErrorCode = null,
        string? Category = null,
        bool WriteAsJson = false);

    private sealed record BatchCommandRunResult(int ExitCode, bool OutputLimitReached);

    private sealed record BatchPendingItem(
        int LineNumber,
        string? CommandName,
        string[] Arguments,
        BatchLineError? Error,
        bool Terminal);

    private sealed record BatchParallelCommandResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        JsonObject? Error);

    private enum BatchOutputKind
    {
        Text,
        JsonDocument,
        Ndjson,
    }

    private sealed class BatchConsoleRouter(TextWriter fallback) : TextWriter, IScopedConsoleOutputRouter
    {
        private readonly AsyncLocal<TextWriter?> _target = new();

        public override Encoding Encoding => fallback.Encoding;

        public IDisposable Push(TextWriter target)
        {
            var previous = _target.Value;
            _target.Value = target;
            return new BatchConsoleRouteScope(this, previous);
        }

        public override void Write(char value)
            => Current.Write(value);

        public override void Write(string? value)
            => Current.Write(value);

        public override void Write(char[]? buffer, int index, int count)
        {
            if (buffer is null)
                return;
            Current.Write(buffer, index, count);
        }

        public override void Write(ReadOnlySpan<char> buffer)
            => Current.Write(buffer);

        public override void Flush()
            => Current.Flush();

        private TextWriter Current => _target.Value ?? fallback;

        private sealed class BatchConsoleRouteScope(
            BatchConsoleRouter owner,
            TextWriter? previous) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                owner._target.Value = previous;
            }
        }
    }

    private sealed class BatchJsonOutputWriter(
        TextWriter output,
        int maxChars,
        int terminalReserveChars,
        JsonSerializerOptions jsonOptions)
    {
        public int WrittenChars { get; private set; }

        public bool TryWrite(JsonNode payload)
            => TryWriteSerialized(Serialize(payload), maxChars - terminalReserveChars);

        public void WriteTerminal(JsonNode payload)
        {
            if (!TryWriteSerialized(Serialize(payload), maxChars))
                throw new InvalidOperationException("Reserved batch terminal output exceeded its configured character budget.");
        }

        public void WriteSummary(JsonObject payload)
        {
            var projectedTotal = 0;
            string serialized;
            for (var attempt = 0; ; attempt++)
            {
                payload["output_chars"] = projectedTotal;
                serialized = Serialize(payload);
                var nextTotal = WrittenChars + serialized.Length + output.NewLine.Length;
                if (nextTotal == projectedTotal)
                    break;
                if (attempt >= 4)
                    throw new InvalidOperationException("Batch output character count did not stabilize.");
                projectedTotal = nextTotal;
            }

            if (!TryWriteSerialized(serialized, maxChars))
                throw new InvalidOperationException("Reserved batch summary output exceeded its configured character budget.");
        }

        private bool TryWriteSerialized(string serialized, int effectiveLimit)
        {
            var recordChars = serialized.Length + output.NewLine.Length;
            if (recordChars < 0 || WrittenChars > effectiveLimit - recordChars)
                return false;

            output.WriteLine(serialized);
            WrittenChars += recordChars;
            return true;
        }

        private string Serialize(JsonNode payload)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Encoder = jsonOptions.Encoder,
                Indented = jsonOptions.WriteIndented,
            }))
            {
                payload.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private sealed class BatchCommandOutputCapture : IDisposable
    {
        private readonly TextWriter _originalOut = Console.Out;
        private readonly TextWriter _originalError = Console.Error;
        private readonly BatchBoundedStringWriter _stdout = new(BatchMaxCapturedOutputChars, "stdout");
        private readonly BatchBoundedStringWriter _stderr = new(BatchMaxCapturedOutputChars, "stderr");
        private bool _started;
        private bool _stopped;

        public string Stdout => _stdout.ToString();
        public string Stderr => _stderr.ToString();

        public void Start()
        {
            if (_started)
                return;
            _started = true;
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }

        public void Stop()
        {
            if (!_started || _stopped)
                return;
            _stopped = true;
            Console.SetOut(_originalOut);
            Console.SetError(_originalError);
        }

        public void Dispose()
        {
            Stop();
            _stdout.Dispose();
            _stderr.Dispose();
        }
    }

    private sealed class BatchBoundedStringWriter(
        int maxChars,
        string streamName) : StringWriter
    {
        private int _writtenChars;

        public override void Write(char value)
        {
            EnsureCapacity(1);
            base.Write(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;
            EnsureCapacity(value.Length);
            base.Write(value);
        }

        public override void Write(char[]? buffer, int index, int count)
        {
            if (buffer is null)
                return;
            EnsureCapacity(count);
            base.Write(buffer, index, count);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
        }

        private void EnsureCapacity(int charCount)
        {
            if (charCount < 0 || _writtenChars > maxChars - charCount)
                throw new BatchOutputCaptureLimitExceededException(
                    maxChars,
                    streamName);
            _writtenChars += charCount;
        }
    }

    private sealed class BatchOutputCaptureLimitExceededException(
        int maxChars,
        string streamName) : Exception
    {
        public int MaxChars { get; } = maxChars;
        public string StreamName { get; } = streamName;
    }
}
