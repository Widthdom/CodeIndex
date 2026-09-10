using System.Text.Json;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    // Inspect tokens without parsing values, resolving a DB, or emitting diagnostics. A
    // separated --query owns its next token, including JSON-looking literals and --.
    private static bool RequestsEarlyUsageJson(string[] args)
    {
        var explicitJson = false;
        var formatJson = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
                break;
            if (arg == "--json" || arg.StartsWith("--json=", StringComparison.Ordinal)
                || arg == "--json-envelope" || arg == "--compact")
            {
                explicitJson = true;
                continue;
            }

            var separator = arg.IndexOf('=');
            var name = separator > 0 ? arg[..separator] : arg;
            var inline = separator > 0;
            if (name == "--format")
            {
                var value = inline ? arg[(separator + 1)..]
                    : i + 1 < args.Length ? args[i + 1] : null;
                if (value != null && TryParseOutputFormat(value, out var format))
                    formatJson = format is OutputFormatJson or OutputFormatCompact or OutputFormatCount;
            }

            if (!inline && ValueTakingOptions.Contains(name) && i + 1 < args.Length)
            {
                var next = args[i + 1];
                // Other readers reject double-dash option boundaries as missing values.
                // Inline values always remain values, even when they look like selectors.
                if (name == "--query" || !next.StartsWith("--", StringComparison.Ordinal))
                    i++;
            }
        }
        return explicitJson || formatJson;
    }

    private static int WriteEarlyUsageJson(
        string command,
        JsonSerializerOptions jsonOptions,
        string error,
        string? hint = null,
        string? errorCode = null)
        => CommandErrorWriter.WriteJsonOrHuman(
            true,
            jsonOptions,
            ConsoleUi.FormatBoundedValue(StripErrorPrefix(error), 1536),
            CommandExitCodes.UsageError,
            ConsoleUi.FormatBoundedValue(hint ?? CommandErrorWriter.BuildUsageHint(command), 1024),
            errorCode: errorCode ?? ExtractErrorCode(error) ?? CommandErrorCodes.UsageError,
            category: "usage",
            command: command,
            omitNullUsage: true);

    private static bool UsesEarlySearchJson(QueryCommandOptions options)
        => options.InvocationContext == QueryCommandInvocationContext.Search
           && options.EarlySearchValidation
           && options.InvocationMachineErrorOutputRequested
           && options.InvocationJsonOptions != null;
}
