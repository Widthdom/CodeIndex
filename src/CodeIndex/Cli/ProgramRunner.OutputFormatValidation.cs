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
        if (args.Length < 2)
            return true;

        var commandName = args[0];
        string? outputFormat = null;
        string? jsonStreamMode = null;
        var jsonRequested = false;
        var prettyRequested = false;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
                break;

            if (arg == "--json")
            {
                jsonRequested = true;
                continue;
            }
            if (arg.StartsWith("--json=", StringComparison.Ordinal))
            {
                jsonRequested = true;
                jsonStreamMode = arg["--json=".Length..].ToLowerInvariant();
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
                outputFormat = arg["--format=".Length..].ToLowerInvariant();
                continue;
            }
            if (arg == "--format"
                && i + 1 < args.Length
                && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                outputFormat = args[++i].ToLowerInvariant();
            }
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
            error = $"--json={jsonStreamMode} cannot be combined with --format {outputFormat} because that format defines its own output schema.";
            hint = $"remove --json={jsonStreamMode} to keep --format {outputFormat}, or use --format json.";
            usage = ConsoleUi.GetUsageLine(commandName) ?? $"cdidx {commandName} --help";
            return false;
        }

        if (prettyRequested && string.Equals(jsonStreamMode, "ndjson", StringComparison.Ordinal))
        {
            error = "--pretty cannot be combined with --json=ndjson because NDJSON requires one JSON value per line.";
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
}
