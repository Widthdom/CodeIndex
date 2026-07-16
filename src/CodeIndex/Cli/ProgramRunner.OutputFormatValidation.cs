namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    private static bool TryValidateOutputFormatOptions(
        string[] args,
        out string error,
        out string hint,
        out string usage)
    {
        error = string.Empty;
        hint = string.Empty;
        usage = string.Empty;
        if (args.Length < 2
            || !TryResolveOutputValidationCommand(args, out _, out var commandName))
            return true;

        // Suggestions owns a structured JSON usage-error contract for export format conflicts.
        // Let its command runner validate the combination after it has parsed the export verb.
        if (string.Equals(commandName, "suggestions", StringComparison.Ordinal))
            return true;

        string? outputFormat = null;
        string? jsonStreamMode = null;
        string? jsonStreamOption = null;
        var jsonRequested = false;
        var prettyRequested = false;
        var usesSingleDocumentSearchMode = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
                break;
            var tokenRole = GetQueryCommandTokenRole(args, i);

            if (string.Equals(commandName, "search", StringComparison.Ordinal)
                && tokenRole != QueryCommandTokenRole.CommandOptionValue
                && (arg is "--count" or "--count-by" or "--named-query" or "--list-recipes"
                    || arg.StartsWith("--count-by=", StringComparison.Ordinal)
                    || arg.StartsWith("--named-query=", StringComparison.Ordinal)))
            {
                usesSingleDocumentSearchMode = true;
            }

            if (arg == "--json")
            {
                if (tokenRole == QueryCommandTokenRole.CommandOptionValue)
                    continue;
                jsonRequested = true;
                jsonStreamOption = "--json";
                continue;
            }
            if (arg.StartsWith("--json=", StringComparison.Ordinal))
            {
                if (tokenRole == QueryCommandTokenRole.CommandOptionValue)
                    continue;
                jsonRequested = true;
                jsonStreamMode = arg["--json=".Length..].ToLowerInvariant();
                jsonStreamOption = arg;
                continue;
            }
            if (arg == "--pretty")
            {
                if (!ShouldPreserveQueryCommandToken(args, i))
                    prettyRequested = true;
                continue;
            }
            if (arg.StartsWith("--format=", StringComparison.Ordinal))
            {
                if (tokenRole == QueryCommandTokenRole.CommandOptionValue)
                    continue;
                outputFormat = arg["--format=".Length..].ToLowerInvariant();
                continue;
            }
            if (arg == "--format"
                && tokenRole != QueryCommandTokenRole.CommandOptionValue
                && i + 1 < args.Length
                && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                outputFormat = args[++i].ToLowerInvariant();
            }
        }

        if (jsonRequested
            && jsonStreamMode == null
            && string.Equals(commandName, "search", StringComparison.Ordinal)
            && !usesSingleDocumentSearchMode)
        {
            jsonStreamMode = "ndjson";
            jsonStreamOption = "--json";
        }

        if (jsonRequested
            && outputFormat != null
            && CliOutputFormatCapabilities.TryGet(outputFormat, out var formatCapability)
            && !formatCapability.IsJsonContract)
        {
            error = $"--json cannot be combined with non-JSON --format {outputFormat}.";
            hint = $"remove --json to keep --format {outputFormat}, or use --format json for the generic JSON contract.";
            usage = ConsoleUi.GetUsageLine(commandName) ?? $"cdidx {commandName} --help";
            return false;
        }

        if (jsonStreamMode != null
            && outputFormat != null
            && CliOutputFormatCapabilities.TryGet(outputFormat, out var streamCapability)
            && !streamCapability.SupportsJsonStreamMode)
        {
            var streamOption = jsonStreamOption ?? $"--json={jsonStreamMode}";
            error = $"{streamOption} cannot be combined with --format {outputFormat} because that format defines its own output schema.";
            hint = $"remove {streamOption} to keep --format {outputFormat}, or use --format json.";
            usage = ConsoleUi.GetUsageLine(commandName) ?? $"cdidx {commandName} --help";
            return false;
        }

        if (prettyRequested && string.Equals(jsonStreamMode, "ndjson", StringComparison.Ordinal))
        {
            var streamOption = jsonStreamOption ?? "--json=ndjson";
            error = $"--pretty cannot be combined with {streamOption} because NDJSON requires one JSON value per line.";
            hint = "use --json=array --pretty for one indented JSON array, or remove --pretty to keep streaming NDJSON.";
            usage = ConsoleUi.GetUsageLine(commandName) ?? $"cdidx {commandName} --help";
            return false;
        }

        if (prettyRequested
            && outputFormat != null
            && CliOutputFormatCapabilities.TryGet(outputFormat, out var prettyCapability)
            && !prettyCapability.SupportsPretty)
        {
            error = $"--pretty cannot be combined with --format {outputFormat} because that format does not emit an indentable JSON document.";
            hint = $"remove --pretty to keep --format {outputFormat}, or use --format json --pretty.";
            usage = ConsoleUi.GetUsageLine(commandName) ?? $"cdidx {commandName} --help";
            return false;
        }

        return true;
    }

    private static bool TryResolveOutputValidationCommand(
        string[] args,
        out int commandIndex,
        out string commandName)
    {
        commandIndex = -1;
        commandName = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
                return false;
            if (CliFlagSchema.AllCommands.Contains(arg))
            {
                commandIndex = i;
                commandName = arg;
                return true;
            }

            if (TryGetInlineOptionName(arg, out var inlineName)
                && (TopLevelValueOptionNames.Contains(inlineName)
                    || NonLogGlobalOptionNames.Contains(inlineName)))
            {
                continue;
            }
            if (TopLevelValueOptionNames.Contains(arg))
            {
                i++;
                continue;
            }
            if (NonLogGlobalOptionNames.Contains(arg))
                continue;

            return false;
        }

        return false;
    }
}
