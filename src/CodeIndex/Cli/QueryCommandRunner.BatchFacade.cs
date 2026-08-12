using System.Globalization;
using System.Text.Json;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
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
        var includeRawStreams = false;
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (arg == "--json-summary")
            {
                jsonSummary = true;
                continue;
            }

            if (arg == "--include-raw-streams")
            {
                includeRawStreams = true;
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
        if (includeRawStreams && !jsonSummary)
        {
            CommandErrorWriter.WriteStderr("Error: --include-raw-streams requires --json-summary.");
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

        var plan = new BatchExecutionPlan(
            dbPath,
            dbPathExplicit,
            jsonSummary,
            maxInputLines,
            maxOutputChars,
            parallelism,
            includeRawStreams);
        return ExecuteBatch(in plan, jsonOptions, appVersion, cancellationToken);
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
}
