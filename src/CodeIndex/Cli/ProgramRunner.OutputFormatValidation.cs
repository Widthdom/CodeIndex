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
            || !TryResolveOutputValidationCommand(args, out var commandIndex, out var commandName))
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
        var compactRequested = false;
        var fieldsRequested = false;
        var resultsOnlyRequested = false;
        var countFlagRequested = false;
        var usesSingleDocumentJsonMode = false;
        var hasExplicitPrettyJsonOutput = HasExplicitPrettyJsonOutputSelection(args);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
                break;
            var tokenRole = GetQueryCommandTokenRole(args, i);

            if (tokenRole != QueryCommandTokenRole.CommandOptionValue && arg == "--count")
                countFlagRequested = true;

            if (tokenRole != QueryCommandTokenRole.CommandOptionValue
                && (arg == "--count"
                    || arg == "--summary-only"
                    || arg == JsonEnvelopeWrapper.EnvelopeFlag
                    || (string.Equals(commandName, "search", StringComparison.Ordinal)
                        && (arg is "--count-by" or "--unique" or "--group-by" or "--named-query" or "--list-recipes" or "--recipe"
                            || arg.StartsWith("--count-by=", StringComparison.Ordinal)
                            || arg.StartsWith("--unique=", StringComparison.Ordinal)
                            || arg.StartsWith("--group-by=", StringComparison.Ordinal)
                            || arg.StartsWith("--named-query=", StringComparison.Ordinal)
                            || arg.StartsWith("--recipe=", StringComparison.Ordinal)))))
            {
                usesSingleDocumentJsonMode = true;
            }

            if (tokenRole != QueryCommandTokenRole.CommandOptionValue && arg == "--results-only")
                resultsOnlyRequested = true;

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
                if (tokenRole != QueryCommandTokenRole.CommandOptionValue
                    && (hasExplicitPrettyJsonOutput || !ShouldPreserveQueryCommandToken(args, i)))
                {
                    prettyRequested = true;
                }
                continue;
            }
            if (tokenRole != QueryCommandTokenRole.CommandOptionValue && arg == "--compact")
            {
                compactRequested = true;
                continue;
            }
            if (tokenRole != QueryCommandTokenRole.CommandOptionValue
                && (arg == "--fields" || arg.StartsWith("--fields=", StringComparison.Ordinal)))
            {
                fieldsRequested = true;
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

        var countModeRequested = countFlagRequested
            || string.Equals(outputFormat, "count", StringComparison.Ordinal);
        if (countModeRequested)
        {
            usesSingleDocumentJsonMode = true;
            if (resultsOnlyRequested)
            {
                jsonStreamMode = "ndjson";
                jsonStreamOption = "--results-only";
            }
        }

        if (ShouldDeferOutputCombinationValidation(args, commandIndex, commandName, outputFormat, jsonStreamMode))
            return true;

        // Audit owns a structured JSON usage-error contract. Once count aliases are
        // normalized, let its command runner report count-mode conflicts so --json
        // callers keep receiving a machine-readable error on stdout.
        if (countModeRequested && string.Equals(commandName, "audit", StringComparison.Ordinal))
            return true;

        if (countFlagRequested
            && outputFormat is not null and not "text" and not "json" and not "count")
        {
            error = $"--count cannot be combined with --format {outputFormat} because count mode supports only text, json, or count output.";
            hint = $"remove --count to keep --format {outputFormat}, or use --format text, json, or count for count output.";
            usage = ConsoleUi.GetUsageLine(commandName) ?? $"cdidx {commandName} --help";
            return false;
        }

        if (countModeRequested && compactRequested)
        {
            error = "Bounded response controls cannot be combined with --count.";
            hint = "remove --compact to keep count output, or remove --count/--format count to keep compact output.";
            usage = ConsoleUi.GetUsageLine(commandName) ?? $"cdidx {commandName} --help";
            return false;
        }

        var commandUsesImplicitNdjson = string.Equals(commandName, "search", StringComparison.Ordinal)
            || string.Equals(commandName, "files", StringComparison.Ordinal)
            || string.Equals(commandName, "symbols", StringComparison.Ordinal);
        if (jsonStreamMode == null
            && (jsonRequested || resultsOnlyRequested || string.Equals(outputFormat, "json", StringComparison.Ordinal))
            && commandUsesImplicitNdjson
            && !usesSingleDocumentJsonMode)
        {
            jsonStreamMode = "ndjson";
            jsonStreamOption = resultsOnlyRequested
                ? "--results-only"
                : jsonRequested
                    ? "--json"
                    : "--format json";
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

        if (fieldsRequested
            && outputFormat != null
            && CliOutputFormatCapabilities.TryGet(outputFormat, out var fieldsFormatCapability)
            && fieldsFormatCapability.Name is not "json" and not "compact")
        {
            error = $"--fields cannot be combined with projection-incompatible --format {outputFormat}.";
            hint = $"remove --fields to keep --format {outputFormat}, or use --format json or compact for projected output.";
            usage = ConsoleUi.GetUsageLine(commandName) ?? $"cdidx {commandName} --help";
            return false;
        }

        var schemaOutputFormat = countModeRequested ? "count" : outputFormat;
        if (jsonStreamMode != null
            && schemaOutputFormat != null
            && CliOutputFormatCapabilities.TryGet(schemaOutputFormat, out var streamCapability)
            && !streamCapability.SupportsJsonStreamMode)
        {
            var streamOption = jsonStreamOption ?? $"--json={jsonStreamMode}";
            error = $"{streamOption} cannot be combined with --format {schemaOutputFormat} because that format defines its own output schema.";
            hint = $"remove {streamOption} to keep --format {schemaOutputFormat}, or use --format json.";
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

    private static bool HasExplicitPrettyJsonOutputSelection(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
                break;
            if (GetQueryCommandTokenRole(args, i) == QueryCommandTokenRole.CommandOptionValue)
                continue;

            if (arg == "--json"
                || arg.StartsWith("--json=", StringComparison.Ordinal)
                || arg == "--results-only"
                || arg == JsonEnvelopeWrapper.EnvelopeFlag
                || (arg.StartsWith("--format=", StringComparison.Ordinal)
                    && CliOutputFormatCapabilities.TryGet(arg["--format=".Length..], out var inlineCapability)
                    && inlineCapability.SupportsPretty))
            {
                return true;
            }

            if (arg == "--format"
                && i + 1 < args.Length
                && CliOutputFormatCapabilities.TryGet(args[i + 1], out var separatedCapability)
                && separatedCapability.SupportsPretty)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldDeferOutputCombinationValidation(
        string[] args,
        int commandIndex,
        string commandName,
        string? outputFormat,
        string? jsonStreamMode)
    {
        if (jsonStreamMode is not null and not "ndjson" and not "array")
            return true;

        if (outputFormat != null && !CommandAcceptsOutputFormat(commandName, outputFormat))
            return true;

        var acceptedFlags = CliFlagSchema.GetAcceptedFlagNamesForCommand(commandName);
        for (var i = commandIndex + 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
                break;
            if (!arg.StartsWith("-", StringComparison.Ordinal)
                || GetQueryCommandTokenRole(args, i) == QueryCommandTokenRole.CommandOptionValue)
            {
                continue;
            }

            var optionName = TryGetInlineOptionName(arg, out var inlineName)
                ? inlineName
                : arg;
            if (!acceptedFlags.Contains(optionName) && !NonLogGlobalOptionNames.Contains(optionName))
                return true;
        }

        return false;
    }

    private static bool CommandAcceptsOutputFormat(string commandName, string outputFormat)
        => CliFlagSchema.TryNormalizeOptionValue(
            commandName,
            "--format",
            outputFormat,
            out _);

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
