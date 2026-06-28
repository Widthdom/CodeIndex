using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
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
                    CommandErrorWriter.WriteStderr($"Error: batch line {lineNumber} exceeds the {BatchMaxLineChars} character limit.");
                    lineErrors++;
                    if (firstFailure == CommandExitCodes.Success)
                        firstFailure = CommandExitCodes.UsageError;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!TryParseBatchLine(line, lineNumber, jsonOptions, out var commandName, out var subArgs, out var parseExitCode))
                {
                    lineErrors++;
                    if (firstFailure == CommandExitCodes.Success)
                        firstFailure = parseExitCode;
                    continue;
                }

                commandsProcessed++;
                var exitCode = RunBatchQueryCommand(commandName, subArgs, jsonOptions);
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
            ["api_version"] = "1",
            ["command"] = "batch",
            ["input_lines_read"] = inputLinesRead,
            ["commands_processed"] = commandsProcessed,
            ["line_errors"] = lineErrors,
            ["command_failures"] = commandFailures,
            ["exit_code"] = exitCode,
        };

        Console.WriteLine(payload.ToJsonString(jsonOptions));
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

    private static bool TryParseBatchLine(string line, int lineNumber, JsonSerializerOptions jsonOptions, out string commandName, out string[] subArgs, out int exitCode)
    {
        commandName = string.Empty;
        subArgs = [];
        exitCode = CommandExitCodes.UsageError;

        try
        {
            using var document = BoundedJson.ParseDocument(line, BatchMaxLineUtf8Bytes, BatchMaxJsonDepth);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                CommandErrorWriter.WriteStderr($"Error: batch line {lineNumber} must be a non-empty JSON string array.");
                return false;
            }
            if (document.RootElement.GetArrayLength() > BatchMaxArgumentCount + 1)
            {
                CommandErrorWriter.WriteStderr($"Error: batch line {lineNumber} must contain at most {BatchMaxArgumentCount} command arguments.");
                return false;
            }

            var values = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    CommandErrorWriter.WriteStderr($"Error: batch line {lineNumber} must contain only strings.");
                    return false;
                }
                var value = element.GetString() ?? string.Empty;
                if (value.Length > BatchMaxArgumentChars)
                {
                    CommandErrorWriter.WriteStderr($"Error: batch line {lineNumber} argument {values.Count + 1} exceeds the {BatchMaxArgumentChars} character limit.");
                    return false;
                }
                values.Add(value);
            }

            commandName = values[0];
            subArgs = values.Skip(1).ToArray();
            return true;
        }
        catch (JsonException)
        {
            CommandErrorWriter.WriteJsonOrHuman(
                true,
                jsonOptions,
                $"batch line {lineNumber} {SafeDiagnosticFormatter.FormatCategoryType("invalid_batch_json", nameof(JsonException))}.",
                CommandExitCodes.UsageError,
                "ensure each batch input line is a JSON string array.",
                errorCode: CommandErrorCodes.UsageError,
                category: "invalid_batch_json");
            return false;
        }
    }

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
}
