namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static bool TryWriteUnexpectedExtraPositionals(string commandName, QueryCommandOptions options)
    {
        if (options.ExtraNames.Count == 0)
            return false;

        CommandErrorWriter.Write(
            $"unexpected extra positional {ConsoleUi.Counted(options.ExtraNames.Count, "argument")} for {commandName}: {string.Join(", ", options.ExtraNames.Select(name => $"`{name}`"))}.",
            BuildUnexpectedExtraPositionalsHint(commandName, options),
            GetUsageLineOrThrow(commandName));
        return true;
    }

    private static string BuildUnexpectedExtraPositionalsHint(string commandName, QueryCommandOptions options)
    {
        if (string.Equals(commandName, "search", StringComparison.Ordinal)
            && options.PathPatterns.Count > 0
            && options.ExtraNames.Any(IsPathLikeArgument))
        {
            return "quote --path globs so the shell passes one literal pattern, e.g. `--path 'src/CodeIndex/**'`; remove the expanded path arguments and rerun.";
        }

        return "quote multi-word queries as a single argument, or remove the extra positional values.";
    }

    private static bool IsPathLikeArgument(string value) =>
        value.Contains('/') || value.Contains('\\');

    private static bool TryWriteUnexpectedPositionals(string commandName, QueryCommandOptions options)
    {
        var unexpected = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Query))
            unexpected.Add($"`{options.Query}`");
        unexpected.AddRange(options.ExtraNames.Select(name => $"`{name}`"));
        if (unexpected.Count == 0)
            return false;

        CommandErrorWriter.Write(
            $"{commandName} does not accept positional arguments: {string.Join(", ", unexpected)}.",
            "remove the extra positional arguments and use the documented flags only.",
            GetUsageLineOrThrow(commandName));
        return true;
    }

    private static string BuildMissingSearchQueryHint(string[] cmdArgs)
    {
        var candidate = FindOptionLookingSearchLiteralCandidate(cmdArgs);
        if (candidate != null)
        {
            var display = ConsoleUi.FormatBoundedValue(candidate);
            return $"Add the text you want to search for after the command. If you meant to search for `{display}`, pass it as `--query \"{display}\"` or after `--`, for example: `cdidx search -- \"{display}\"`.";
        }

        return "Add the text you want to search for after the command, for example: `cdidx search authenticate`. If the query itself starts with `--`, pass it as `--query \"--profile\"` or after `--`, for example: `cdidx search -- \"--profile\"`.";
    }

    private static string? FindOptionLookingSearchLiteralCandidate(string[] cmdArgs)
    {
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (arg == "--")
                return i + 1 < cmdArgs.Length && cmdArgs[i + 1].StartsWith("-", StringComparison.Ordinal)
                    ? cmdArgs[i + 1]
                    : null;

            var inlineValue = TrySplitInlineOptionValue(arg, out var inlineOptionName)
                ? arg[(inlineOptionName!.Length + 1)..]
                : null;
            var normalizedArg = inlineOptionName ?? arg;
            if (ValueTakingOptions.Contains(normalizedArg))
            {
                if (inlineValue == null)
                    i++;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;
            if (SearchMissingQueryControlFlags.Contains(normalizedArg))
                continue;

            return arg;
        }

        return null;
    }

    private static readonly HashSet<string> SearchMissingQueryControlFlags =
    [
        "--exact",
        "--exact-name",
        "--exact-substring",
        "--prefix",
        "--fts",
        "--json",
        "--pretty",
        "--count",
        "--no-dedup",
        "--no-visibility-rank",
        "--exclude-tests",
        "--strict-not-found",
        "--verbose",
        "--quiet",
        "--silent",
    ];

    private static string GetUsageLineOrThrow(string commandName) =>
        ConsoleUi.GetUsageLine(commandName)
        ?? throw new InvalidOperationException($"Missing usage line for command '{commandName}'.");

    private static void WriteUsageError(string message, string usage, string hint)
        => CommandErrorWriter.Write(message, hint, usage);

    private static bool TryWriteUnsupportedOutputFormat(string commandName, QueryCommandOptions options, IReadOnlySet<string> supportedFormats, string hint)
    {
        if (supportedFormats.Contains(options.OutputFormat))
            return false;

        WriteUsageError(
            $"--format {options.OutputFormat} is not supported by {commandName}.",
            GetUsageLineOrThrow(commandName),
            hint);
        return true;
    }

    private static bool TryWriteBlankQueryError(QueryCommandOptions options, string commandName)
    {
        if (options.Query is null)
            return false;
        if (!string.IsNullOrWhiteSpace(options.Query))
            return false;
        WriteUsageError(
            $"{commandName} query cannot be empty or whitespace-only",
            GetUsageLineOrThrow(commandName),
            $"Pass a non-empty value after `{commandName}`; empty or whitespace-only arguments (e.g. `\"\"` or `\"   \"`) are rejected.");
        return true;
    }

    private static bool TryWriteSnippetLinesZeroUnsupportedError(QueryCommandOptions options, string commandName)
    {
        if (options.SnippetLines != 0)
            return false;

        WriteUsageError(
            "--snippet-lines 0 is only supported with `cdidx search --format issue-drafts`.",
            GetUsageLineOrThrow(commandName),
            "Pass a positive snippet line count for this command, for example `--snippet-lines 1`.");
        return true;
    }

    private static void WriteValidationError(string message, string hint)
        => CommandErrorWriter.Write(message, hint);

    private static string StripErrorPrefix(string message)
    {
        const string prefix = "Error: ";
        if (message.StartsWith(prefix, StringComparison.Ordinal))
            return message[prefix.Length..];

        var codedPrefixEnd = message.IndexOf("]: ", StringComparison.Ordinal);
        if (message.StartsWith("Error [", StringComparison.Ordinal) && codedPrefixEnd >= 0)
            return message[(codedPrefixEnd + 3)..];

        return message;
    }

    private static string? ExtractErrorCode(string message)
    {
        const string prefix = "Error [";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var end = message.IndexOf("]: ", StringComparison.Ordinal);
        return end > prefix.Length ? message[prefix.Length..end] : null;
    }
}
