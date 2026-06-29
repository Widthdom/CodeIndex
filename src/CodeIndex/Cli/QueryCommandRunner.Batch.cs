using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const int BatchMaxCapturedOutputChars = JsonEnvelopeWrapper.MaxCapturedOutputChars;

    public static int RunBatch(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var dbPath = Path.Combine(".cdidx", "codeindex.db");
        var dbPathExplicit = false;
        var jsonSummary = false;
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

            CommandErrorWriter.WriteStderr($"Error: {ConsoleUi.FormatBoundedValue(arg)} is not supported for batch.");
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

        try
        {
            using var db = new DbContext(dbPath);
            if (!db.TryValidateIsCodeIndexDb(out var validationReason))
                return WriteInvalidCodeIndexDbError(dbPath, validationReason);

            db.TryMigrateForRead();
            s_batchReader = new DbReader(db);
            s_batchDbPath = dbPath;
            s_batchDbPathExplicit = dbPathExplicit;
            var firstFailure = CommandExitCodes.Success;
            var lineNumber = 0;
            var commandsProcessed = 0;
            var lineErrors = 0;
            var commandFailures = 0;
            while (TryReadBatchLine(Console.In, out var line, out var lineExceededLimit))
            {
                lineNumber++;
                if (lineExceededLimit)
                {
                    var lineError = new BatchLineError(
                        $"batch line {lineNumber} exceeds the {BatchMaxLineChars} character limit.",
                        CommandExitCodes.UsageError,
                        ErrorCode: CommandErrorCodes.UsageError);
                    if (jsonSummary)
                        WriteBatchLineErrorJson(lineNumber, lineError, jsonOptions);
                    else
                        WriteBatchLineErrorDiagnostic(lineError, jsonOptions);
                    lineErrors++;
                    if (firstFailure == CommandExitCodes.Success)
                        firstFailure = CommandExitCodes.UsageError;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!TryParseBatchLine(line, lineNumber, jsonOptions, !jsonSummary, out var commandName, out var subArgs, out var parseExitCode, out var parseError))
                {
                    if (jsonSummary)
                        WriteBatchLineErrorJson(lineNumber, parseError ?? BuildGenericBatchLineError(lineNumber), jsonOptions);
                    lineErrors++;
                    if (firstFailure == CommandExitCodes.Success)
                        firstFailure = parseExitCode;
                    continue;
                }

                commandsProcessed++;
                var exitCode = jsonSummary
                    ? RunBatchQueryCommandWithJsonRecord(lineNumber, commandName, subArgs, jsonOptions)
                    : RunBatchQueryCommand(commandName, subArgs, jsonOptions);
                if (exitCode != CommandExitCodes.Success)
                {
                    commandFailures++;
                    if (firstFailure == CommandExitCodes.Success)
                        firstFailure = exitCode;
                }
            }

            if (jsonSummary)
                WriteBatchSummaryJson(lineNumber, commandsProcessed, lineErrors, commandFailures, firstFailure, jsonOptions);

            return firstFailure;
        }
        finally
        {
            s_batchReader = null;
            s_batchDbPath = null;
            s_batchDbPathExplicit = false;
        }
    }

    private static void WriteBatchSummaryJson(int inputLinesRead, int commandsProcessed, int lineErrors, int commandFailures, int exitCode, JsonSerializerOptions jsonOptions)
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
        };

        CommandOutputWriter.WriteJsonNode(payload, jsonOptions);
    }

    private static int RunBatchQueryCommandWithJsonRecord(int lineNumber, string commandName, string[] subArgs, JsonSerializerOptions jsonOptions)
    {
        using var capture = new BatchCommandOutputCapture();
        int exitCode;
        BatchOutputCaptureLimitExceededException? captureLimitExceeded = null;
        try
        {
            capture.Start();
            exitCode = RunBatchQueryCommand(commandName, subArgs, jsonOptions);
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
        {
            error = new JsonObject
            {
                ["message"] = $"batch command {captureLimitExceeded.StreamName} exceeded {captureLimitExceeded.MaxChars} captured characters.",
                ["hint"] = "Reduce the result set or run cdidx batch without --json-summary for streaming output.",
                ["error_code"] = CommandErrorCodes.UsageError,
                ["max_chars"] = captureLimitExceeded.MaxChars,
                ["stream"] = captureLimitExceeded.StreamName,
            };
        }

        WriteBatchCommandRecordJson(
            lineNumber,
            commandName,
            subArgs,
            exitCode,
            capture.Stdout,
            capture.Stderr,
            error,
            jsonOptions);
        return exitCode;
    }

    private static void WriteBatchCommandRecordJson(
        int lineNumber,
        string commandName,
        string[] subArgs,
        int exitCode,
        string stdout,
        string stderr,
        JsonObject? error,
        JsonSerializerOptions jsonOptions)
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
            ["stdout"] = stdout,
            ["stderr"] = stderr,
        };

        if (error is not null)
            payload["error"] = error;

        CommandOutputWriter.WriteJsonNode(payload, jsonOptions);
    }

    private static void WriteBatchLineErrorJson(int lineNumber, BatchLineError error, JsonSerializerOptions jsonOptions)
    {
        var payload = new JsonObject
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

        CommandOutputWriter.WriteJsonNode(payload, jsonOptions);
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
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                error = new BatchLineError(
                    $"batch line {lineNumber} must be a non-empty JSON string array.",
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
                Hint: "ensure each batch input line is a JSON string array.",
                ErrorCode: CommandErrorCodes.UsageError,
                Category: "invalid_batch_json",
                WriteAsJson: true);
            if (writeDiagnostics)
                WriteBatchLineErrorDiagnostic(error, jsonOptions);
            return false;
        }
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

    private static int RunBatchQueryCommand(string commandName, string[] subArgs, JsonSerializerOptions jsonOptions)
        => commandName switch
        {
            "search" => RunSearch(subArgs, jsonOptions),
            "definition" => RunDefinition(subArgs, jsonOptions),
            "references" => RunReferences(subArgs, jsonOptions),
            "callers" => RunCallers(subArgs, jsonOptions),
            "callees" => RunCallees(subArgs, jsonOptions),
            "symbols" => RunSymbols(subArgs, jsonOptions),
            "files" => RunFiles(subArgs, jsonOptions),
            "find" => RunFind(subArgs, jsonOptions),
            "excerpt" => RunExcerpt(subArgs, jsonOptions),
            "map" => RunMap(subArgs, jsonOptions),
            "inspect" => RunInspect(subArgs, jsonOptions),
            "outline" => RunOutline(subArgs, jsonOptions),
            "status" => RunStatus(subArgs, jsonOptions),
            "validate" => RunValidate(subArgs, jsonOptions),
            "impact" => RunImpact(subArgs, jsonOptions),
            "deps" => RunDeps(subArgs, jsonOptions),
            "unused" => RunUnused(subArgs, jsonOptions),
            "hotspots" => RunHotspots(subArgs, jsonOptions),
            _ => WriteBatchUnsupportedCommand(commandName),
        };

    private static int WriteBatchUnsupportedCommand(string commandName)
    {
        CommandErrorWriter.WriteStderr($"Error: batch only supports query commands; '{commandName}' is not supported.");
        CommandErrorWriter.WriteStderr("Hint: use one of search, definition, references, callers, callees, symbols, files, find, excerpt, map, inspect, outline, status, validate, impact, deps, unused, or hotspots.");
        return CommandExitCodes.UsageError;
    }

    private sealed record BatchLineError(
        string Message,
        int ExitCode,
        string? Hint = null,
        string? ErrorCode = null,
        string? Category = null,
        bool WriteAsJson = false);

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

    private sealed class BatchBoundedStringWriter(int maxChars, string streamName) : StringWriter
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
                throw new BatchOutputCaptureLimitExceededException(maxChars, streamName);
            _writtenChars += charCount;
        }
    }

    private sealed class BatchOutputCaptureLimitExceededException(int maxChars, string streamName) : Exception
    {
        public int MaxChars { get; } = maxChars;
        public string StreamName { get; } = streamName;
    }
}
