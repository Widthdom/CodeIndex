using System.Runtime.CompilerServices;
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
    private static readonly ConditionalWeakTable<TextReader, BatchInputPump> s_batchInputPumps = [];
    internal static Action<int>? BatchParallelCommandStartedForTesting { get; set; }
    internal static Action<int>? BatchParallelCommandCompletedForTesting { get; set; }
    internal static Action<int>? BatchInputLineReadForTesting { get; set; }
    internal static Action<int>? BatchParallelItemPreparedForTesting { get; set; }
    internal static Action? BatchParallelSessionOpenedForTesting { get; set; }
    internal static Func<DbContext, DbReader>? BatchParallelReaderFactoryForTesting { get; set; }
    internal static Action? BatchParallelDatabaseValidatingForTesting { get; set; }

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
        if (exitCode is CommandExitCodes.CancelledBySignal or CommandExitCodes.LegacyInterrupted)
        {
            payload["error"] = BuildBatchTypedError(
                "batch processing was cancelled by the caller.",
                exitCode,
                "Retry the batch when the caller cancellation token is not cancelled.",
                CommandErrorCodes.Interrupted,
                "batch_cancelled",
                "batch");
        }

        output.WriteSummary(payload);
    }

    private static int WriteBatchSetupCancellationSummary(
        int maxInputLines,
        int maxOutputChars,
        int parallelism,
        JsonSerializerOptions jsonOptions)
    {
        var output = new BatchJsonOutputWriter(
            Console.Out,
            maxOutputChars,
            BatchTerminalOutputReserveChars,
            jsonOptions);
        WriteBatchSummaryJson(
            inputLinesRead: 0,
            commandsProcessed: 0,
            lineErrors: 0,
            commandFailures: 0,
            CommandExitCodes.CancelledBySignal,
            outputLimitReached: false,
            inputLimitReached: false,
            maxInputLines,
            maxOutputChars,
            parallelism,
            output);
        return CommandExitCodes.CancelledBySignal;
    }

    private static BatchCommandRunResult RunBatchQueryCommandWithJsonRecord(
        int lineNumber,
        string commandName,
        string[] subArgs,
        int outputCharLimit,
        bool includeRawStreams,
        BatchJsonOutputWriter output,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken)
    {
        using var capture = new BatchCommandOutputCapture();
        int exitCode;
        BatchOutputCaptureLimitExceededException? captureLimitExceeded = null;
        JsonObject? commandError = null;
        var cancellationObserved = false;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            exitCode = CommandExitCodes.CancelledBySignal;
            commandError = BuildBatchCancellationError();
            cancellationObserved = true;
        }
        catch (TimeoutException)
        {
            exitCode = CommandExitCodes.RuntimeError;
            commandError = BuildBatchTimeoutError();
        }
        catch (Exception ex)
        {
            exitCode = CommandExitCodes.RuntimeError;
            commandError = BuildBatchTypedError(
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
            capture.Stop();
        }

        if (captureLimitExceeded is not null)
            commandError = BuildBatchCaptureLimitError(captureLimitExceeded);

        var recordWritten = WriteBatchCommandRecordJson(
            lineNumber,
            commandName,
            subArgs,
            exitCode,
            capture.Stdout,
            capture.Stderr,
            commandError,
            ClassifyBatchOutput(commandName, subArgs),
            includeRawStreams,
            output);
        if (recordWritten)
            return new BatchCommandRunResult(
                exitCode,
                OutputLimitReached: false,
                CancellationObserved: cancellationObserved);

        WriteBatchOutputLimitErrorJson(lineNumber, commandName, exitCode, outputCharLimit, output);
        return new BatchCommandRunResult(
            CommandExitCodes.InvalidArgument,
            OutputLimitReached: true,
            CancellationObserved: cancellationObserved);
    }

    private static JsonObject BuildBatchCaptureLimitError(BatchOutputCaptureLimitExceededException exception)
    {
        return BuildBatchTypedError(
            $"batch command {exception.StreamName} exceeded {exception.MaxChars} captured characters.",
            CommandExitCodes.InvalidArgument,
            "Reduce the result set or run cdidx batch without --json-summary for streaming output.",
            CommandErrorCodes.UsageError,
            "batch_child_output_limit",
            "command",
            new JsonObject
            {
                ["max_chars"] = exception.MaxChars,
                ["stream"] = exception.StreamName,
            });
    }

    private static JsonObject BuildBatchCancellationError()
        => BuildBatchTypedError(
            "batch command was cancelled by the caller.",
            CommandExitCodes.CancelledBySignal,
            "Retry the batch when the caller cancellation token is not cancelled.",
            CommandErrorCodes.Interrupted,
            "batch_cancelled",
            "command");

    private static BatchLineError BuildBatchCancellationLineError(int lineNumber)
        => new(
            $"batch line {lineNumber} was not dispatched because the caller cancelled the batch.",
            CommandExitCodes.CancelledBySignal,
            Hint: "Retry the batch when the caller cancellation token is not cancelled.",
            ErrorCode: CommandErrorCodes.Interrupted,
            Category: "batch_cancelled");

    private static JsonObject BuildBatchTimeoutError()
        => BuildBatchTypedError(
            "batch command exceeded its execution deadline.",
            CommandExitCodes.RuntimeError,
            "Reduce the query scope or retry the command directly.",
            CommandErrorCodes.CommandFailed,
            "batch_command_timeout",
            "command");

    private static JsonObject BuildBatchTypedError(
        string message,
        int exitCode,
        string? hint,
        string? errorCode,
        string? category,
        string scope,
        JsonObject? additionalProperties = null)
    {
        var (resolvedErrorCode, resolvedCategory) = CommandErrorWriter.ResolveMachineContract(
            exitCode,
            errorCode,
            category);
        var payload = new JsonObject
        {
            ["message"] = message,
            ["hint"] = hint ?? BatchFailureHint(exitCode),
            ["error_code"] = resolvedErrorCode,
            ["category"] = resolvedCategory,
            ["scope"] = scope,
        };

        if (additionalProperties is not null)
        {
            foreach (var property in additionalProperties)
            {
                if (!payload.ContainsKey(property.Key))
                    payload[property.Key] = property.Value?.DeepClone();
            }
        }

        return payload;
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
        bool includeRawStreams,
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
        };

        if (exitCode == CommandExitCodes.Success)
        {
            payload["stderr"] = stderr;
            if (TryParseBatchStructuredOutput(stdout, outputKind, out var resultField, out var structuredOutput))
                payload[resultField] = structuredOutput;
            else
                payload["stdout"] = stdout;
        }
        else
        {
            payload["error"] = BuildBatchCommandFailureError(commandName, exitCode, error);
            if (includeRawStreams)
            {
                payload["raw_streams"] = new JsonObject
                {
                    ["stdout"] = stdout,
                    ["stderr"] = stderr,
                };
            }
        }

        return output.TryWrite(payload);
    }

    private static JsonObject BuildBatchCommandFailureError(
        string commandName,
        int exitCode,
        JsonObject? error)
    {
        if (error is null && !CliCommandCatalog.IsBatchReadOnlyCommand(commandName))
        {
            return BuildBatchTypedError(
                "batch command was rejected by the read-only dispatch policy.",
                exitCode,
                $"Use one of {string.Join(", ", CliCommandCatalog.BatchReadOnlyCommands)}.",
                CommandErrorCodes.UsageError,
                "batch_command_not_allowed",
                "command");
        }

        if (error is null)
        {
            var (_, category) = CommandErrorWriter.ResolveMachineContract(exitCode);
            return BuildBatchTypedError(
                "batch child command returned a non-zero exit code.",
                exitCode,
                BatchFailureHint(exitCode),
                errorCode: null,
                category: $"batch_child_{category}",
                scope: "command",
                new JsonObject
                {
                    ["child_exit_code"] = exitCode,
                });
        }

        var message = GetBatchErrorString(error, "message")
            ?? "batch child command returned a non-zero exit code.";
        var hint = GetBatchErrorString(error, "hint") ?? BatchFailureHint(exitCode);
        var errorCode = GetBatchErrorString(error, "error_code");
        var categoryValue = GetBatchErrorString(error, "category");
        var scope = GetBatchErrorString(error, "scope") ?? "command";
        return BuildBatchTypedError(
            message,
            exitCode,
            hint,
            errorCode,
            categoryValue,
            scope,
            error);
    }

    private static string? GetBatchErrorString(JsonObject error, string propertyName)
        => error[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var text)
                ? text
                : null;

    private static string BatchFailureHint(int exitCode)
        => exitCode switch
        {
            CommandExitCodes.UsageError or CommandExitCodes.InvalidArgument or CommandExitCodes.ExUsage
                => "Check the child command arguments with `cdidx <command> --help`.",
            CommandExitCodes.NotFound
                => "Broaden the query or remove strict not-found handling before retrying.",
            CommandExitCodes.DatabaseError or CommandExitCodes.TransientDatabaseError
                => "Run `cdidx status --check --json` and follow its repair guidance.",
            CommandExitCodes.FeatureUnavailable
                => "Use a build that includes the requested feature or choose a supported output mode.",
            CommandExitCodes.StaleIndex
                => "Refresh the index and retry the child command.",
            CommandExitCodes.CancelledBySignal or CommandExitCodes.LegacyInterrupted
                => "Retry the batch when the caller cancellation token is not cancelled.",
            CommandExitCodes.PartialResult
                => "Inspect the child command directly before relying on the partial result.",
            _ => "Retry the child command directly for command-specific diagnostics.",
        };

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
        var error = BuildBatchTypedError(
            $"batch serialized output reached the {outputCharLimit} character limit.",
            CommandExitCodes.InvalidArgument,
            "Split the request into smaller batches or reduce child output with --limit/--top.",
            CommandErrorCodes.UsageError,
            "batch_output_limit",
            "batch",
            new JsonObject
            {
                ["max_chars"] = outputCharLimit,
                ["attempted_exit_code"] = attemptedExitCode,
            });
        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["record"] = commandName is null ? "batch_error" : "batch_result",
            ["status"] = "error",
            ["line"] = lineNumber,
            ["exit_code"] = CommandExitCodes.InvalidArgument,
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
        => BuildBatchTypedError(
            error.Message,
            error.ExitCode,
            error.Hint,
            error.ErrorCode,
            error.Category,
            "input");

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
                    Hint: "Use a non-empty JSON string array or a {\"command\",\"args\"} object.",
                    ErrorCode: CommandErrorCodes.UsageError,
                    Category: "invalid_batch_input_shape");
                if (writeDiagnostics)
                    WriteBatchLineErrorDiagnostic(error, jsonOptions);
                return false;
            }
            if (document.RootElement.GetArrayLength() > BatchMaxArgumentCount + 1)
            {
                error = new BatchLineError(
                    $"batch line {lineNumber} must contain at most {BatchMaxArgumentCount} command arguments.",
                    CommandExitCodes.UsageError,
                    Hint: "Reduce the number of child command arguments.",
                    ErrorCode: CommandErrorCodes.UsageError,
                    Category: "batch_argument_count_limit");
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
                        Hint: "Encode the command and every argument as JSON strings.",
                        ErrorCode: CommandErrorCodes.UsageError,
                        Category: "invalid_batch_argument_type");
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
                        Hint: "Reduce the child command argument length.",
                        ErrorCode: CommandErrorCodes.UsageError,
                        Category: "batch_argument_length_limit");
                    if (writeDiagnostics)
                        WriteBatchLineErrorDiagnostic(error, jsonOptions);
                    return false;
                }
                values.Add(value);
            }

            commandName = values[0];
            subArgs = [.. values.Skip(1)];
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

        subArgs = [.. values];
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
            Hint: "Use a JSON string array or a {\"command\",\"args\"} object.",
            ErrorCode: CommandErrorCodes.UsageError,
            Category: "invalid_batch_input");

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

    private readonly record struct BatchCommandRunResult(
        int ExitCode,
        bool OutputLimitReached,
        bool CancellationObserved);

    private readonly record struct BatchPendingItem(
        int LineNumber,
        string? CommandName,
        string[] Arguments,
        BatchLineError? Error,
        bool Terminal);

    private static BatchInputPump GetBatchInputPump(TextReader reader)
        => s_batchInputPumps.GetValue(reader, static value => new BatchInputPump(value));

    private sealed class BatchInputPump
    {
        private readonly Channel<BatchPumpedLine> _lines;

        public BatchInputPump(TextReader reader)
        {
            _lines = Channel.CreateBounded<BatchPumpedLine>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = true,
                });

            // The pump is the sole owner of this reader. A cancelled batch only
            // cancels its channel read, so an in-flight line remains available
            // to the next batch invocation instead of being consumed by an
            // orphaned per-invocation producer.
            _ = Task.Run(() => Pump(reader));
        }

        public async ValueTask<BatchPumpedLine?> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException) when (_lines.Reader.Completion.IsCompletedSuccessfully)
            {
                return null;
            }
        }

        private void Pump(TextReader reader)
        {
            try
            {
                while (TryReadBatchLine(reader, out var line, out var exceededLimit))
                {
                    _lines.Writer.WriteAsync(new BatchPumpedLine(line, exceededLimit))
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
                _lines.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _lines.Writer.TryComplete(ex);
            }
        }
    }

    private readonly record struct BatchPumpedLine(string? Line, bool ExceededLimit);

    private readonly record struct BatchParallelCommandResult(
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

    private sealed class BatchParallelSession : IDisposable
    {
        private readonly string _dbPath;
        private DbContext? _db;
        private DbReader? _reader;
        private DbConnectionFactory.QueryOnlySnapshotSourceState? _sourceState;
        private bool _readerUsed;
        private bool _disposed;

        public BatchParallelSession(string dbPath)
        {
            _dbPath = dbPath;
        }

        private BatchParallelSession(
            string dbPath,
            DbContext validatedDb,
            DbReader reader,
            DbConnectionFactory.QueryOnlySnapshotSourceState? sourceState)
        {
            _dbPath = dbPath;
            _db = validatedDb;
            _reader = reader;
            _sourceState = sourceState;
        }

        public static BatchParallelSession FromValidated(
            string dbPath,
            DbContext validatedDb,
            CancellationToken cancellationToken)
        {
            DbReader? reader = null;
            try
            {
                reader = CreateReader(validatedDb);
                var sourceState = CaptureSourceState(dbPath, validatedDb, cancellationToken);
                BatchParallelSessionOpenedForTesting?.Invoke();
                return new BatchParallelSession(dbPath, validatedDb, reader, sourceState);
            }
            catch
            {
                reader?.Dispose();
                validatedDb.Dispose();
                throw;
            }
        }

        public bool TryGetReader(
            CancellationToken cancellationToken,
            out DbReader reader,
            out string? validationReason)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_reader is not null
                && _db is not null
                && (!_readerUsed
                    || (_sourceState is { } sourceState
                        && DbConnectionFactory.IsQuerySourceStateCurrent(
                            _dbPath,
                            sourceState,
                            cancellationToken))))
            {
                _readerUsed = true;
                reader = _reader;
                validationReason = null;
                return true;
            }

            DbContext? replacementDb = null;
            DbReader? replacementReader = null;
            try
            {
                replacementDb = new DbContext(
                    DbOpenIntent.QueryOnly,
                    _dbPath,
                    cancellationToken);
                if (!replacementDb.TryValidateIsCodeIndexDb(out validationReason))
                {
                    replacementDb.Dispose();
                    replacementDb = null;
                    reader = null!;
                    return false;
                }

                replacementReader = CreateReader(replacementDb);
                var replacementSourceState = CaptureSourceState(
                    _dbPath,
                    replacementDb,
                    cancellationToken);
                BatchParallelSessionOpenedForTesting?.Invoke();

                var previousReader = _reader;
                var previousDb = _db;
                _reader = replacementReader;
                _db = replacementDb;
                _sourceState = replacementSourceState;
                _readerUsed = true;
                replacementReader = null;
                replacementDb = null;
                try
                {
                    previousReader?.Dispose();
                }
                finally
                {
                    previousDb?.Dispose();
                }

                reader = _reader;
                return true;
            }
            catch
            {
                replacementReader?.Dispose();
                replacementDb?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _reader?.Dispose();
                _reader = null;
            }
            finally
            {
                _db?.Dispose();
                _db = null;
            }
        }

        private static DbConnectionFactory.QueryOnlySnapshotSourceState? CaptureSourceState(
            string dbPath,
            DbContext db,
            CancellationToken cancellationToken)
            => db.QueryOnlySnapshotSourceState
               ?? (DbConnectionFactory.TryCaptureQuerySourceState(
                       dbPath,
                       cancellationToken,
                       out var sourceState)
                   ? sourceState
                   : null);

        private static DbReader CreateReader(DbContext db)
            => BatchParallelReaderFactoryForTesting?.Invoke(db) ?? new DbReader(db);
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
        private readonly BatchBoundedStringWriter _stdout = new(BatchMaxCapturedOutputChars, "stdout");
        private readonly BatchBoundedStringWriter _stderr = new(BatchMaxCapturedOutputChars, "stderr");
        private TextWriter? _originalOut;
        private TextWriter? _originalError;
        private IDisposable? _ownership;
        private bool _started;
        private bool _stopped;

        public string Stdout => _stdout.ToString();
        public string Stderr => _stderr.ToString();

        public void Start()
        {
            if (_started)
                return;
            _started = true;
            var ownership = ConsoleStreamOwnership.Enter();
            try
            {
                _originalOut = Console.Out;
                _originalError = Console.Error;
                Console.SetOut(_stdout);
                Console.SetError(_stderr);
                _ownership = ownership;
            }
            catch
            {
                ownership.Dispose();
                throw;
            }
        }

        public void Stop()
        {
            if (!_started || _stopped)
                return;
            _stopped = true;
            try
            {
                ConsoleStreamOwnership.Restore(_originalOut!, _originalError!);
            }
            finally
            {
                _ownership?.Dispose();
                _ownership = null;
            }
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
