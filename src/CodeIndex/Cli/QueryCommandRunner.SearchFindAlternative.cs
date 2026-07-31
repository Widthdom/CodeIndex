using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const int SearchFindAlternativeDiagnosticItemLimit = 32;

    private static readonly HashSet<string> SearchFindAlternativeMappableOptions =
        new(StringComparer.Ordinal)
        {
            "--",
            "--all",
            "--allow-partial",
            "--allow-unknown-lang",
            "--count",
            "--data-dir",
            "--db",
            "--exact",
            "--exclude-path",
            "--exclude-tests",
            "--format",
            "--immutable",
            "--include-generated",
            "--json",
            "--lang",
            "--limit",
            "--max-json-bytes",
            "--max-line-width",
            "--max-results",
            "--no-progress",
            "--path",
            "--profile",
            "--query",
            "--quiet",
            "--read-only",
            "--regex",
            "--silent",
            "--slow-query-ms",
            "--snippet-lines",
            "--strict-not-found",
            "--top",
            "--verbose",
            "-q",
        };

    private static readonly HashSet<string> SearchFindAlternativeOutputFormats =
        new(StringComparer.Ordinal)
        {
            "compact",
            "count",
            "csv",
            "json",
            "lsp",
            "qf",
            "sarif",
            "text",
            "tsv",
        };

    private static bool TryWriteSearchFindAlternativeError(
        string[] cmdArgs,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        if (!options.Regex && !options.All)
            return false;

        var optionNames = CollectSearchFindAlternativeOptionNames(cmdArgs);
        var acceptedSearchOptions = CliFlagSchema
            .GetAcceptedFlagNamesForCommand("search")
            .ToHashSet(StringComparer.Ordinal);
        if (optionNames.Any(name =>
                name is not "--regex" and not "--all"
                && !acceptedSearchOptions.Contains(name)))
        {
            // Keep the existing typo/unknown-option recovery for malformed invocations.
            return false;
        }

        var nonEquivalentOptions = optionNames
            .Where(name => !SearchFindAlternativeMappableOptions.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(SearchFindAlternativeDiagnosticItemLimit)
            .ToList();
        var blockers = new List<string>();

        if (options.ParseError != null)
            blockers.Add("one or more search option values are invalid and cannot be normalized safely");
        if (string.IsNullOrWhiteSpace(options.Query))
            blockers.Add("search did not receive one non-empty query to replay");
        if (options.ExtraNames.Count > 0)
            blockers.Add("extra positional query text cannot be represented as one find query");
        if (options.PathPatterns.Count > 0 && options.All)
            blockers.Add("find accepts either --path filters or --all, not both");
        if (!SearchFindAlternativeOutputFormats.Contains(options.OutputFormat))
            nonEquivalentOptions.Add($"--format {options.OutputFormat}");
        if (options.JsonOutputFormatExplicit
            && !string.Equals(options.JsonOutputFormat, JsonOutputFormatNdjson, StringComparison.OrdinalIgnoreCase))
        {
            nonEquivalentOptions.Add($"--json={options.JsonOutputFormat}");
        }

        var usesFindAll = options.PathPatterns.Count == 0;
        if (usesFindAll
            && !options.CountOnly
            && options.OutputFormat is not OutputFormatText and not OutputFormatJson)
        {
            blockers.Add($"find --all cannot preserve row output format {options.OutputFormat}");
        }

        nonEquivalentOptions = nonEquivalentOptions
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(SearchFindAlternativeDiagnosticItemLimit)
            .ToList();

        List<string>? argv = null;
        if (blockers.Count == 0 && nonEquivalentOptions.Count == 0)
        {
            argv = BuildSearchFindAlternativeArgv(cmdArgs, options, usesFindAll);
            if (argv.Any(argument => argument.Any(char.IsControl)))
            {
                blockers.Add("the query or an option value contains control characters that are unsafe in a one-line replay suggestion");
                argv = null;
            }
        }

        var reason = BuildSearchFindAlternativeReason(argv, nonEquivalentOptions, blockers);
        var additionalProperties = BuildSearchFindAlternativeJson(
            argv,
            reason,
            nonEquivalentOptions,
            blockers);
        var triggeringOptions = options.Regex && options.All
            ? "--regex and --all are"
            : options.Regex
                ? "--regex is"
                : "--all is";
        var hint = argv != null
            ? $"Run this equivalent find scan (displayed only; not executed): {RenderSearchFindAlternativeForCurrentShell(argv)}"
            : $"Use `find` for literal or regular-expression file scans, but no exact command was generated: {reason}";

        CommandErrorWriter.WriteJsonOrHuman(
            ProgramRunner.ContainsJsonOutputFlag(cmdArgs),
            jsonOptions,
            $"{triggeringOptions} not supported for search.",
            CommandExitCodes.UsageError,
            hint,
            GetUsageLineOrThrow("search"),
            CommandErrorCodes.UsageError,
            category: "usage",
            command: "search",
            additionalJsonProperties: additionalProperties);
        return true;
    }

    private static List<string> CollectSearchFindAlternativeOptionNames(string[] cmdArgs)
    {
        var names = new List<string>();
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (arg == "--")
            {
                names.Add(arg);
                if (i + 1 < cmdArgs.Length)
                    i++;
                continue;
            }

            if (!arg.StartsWith("-", StringComparison.Ordinal))
                continue;

            var hasInlineValue = TrySplitInlineOptionValue(arg, out var inlineOptionName);
            var normalizedName = inlineOptionName ?? arg;
            names.Add(normalizedName);
            if (!hasInlineValue
                && ValueTakingOptions.Contains(normalizedName)
                && i + 1 < cmdArgs.Length)
            {
                i++;
            }
        }

        return names;
    }

    private static List<string> BuildSearchFindAlternativeArgv(
        string[] cmdArgs,
        QueryCommandOptions options,
        bool usesFindAll)
    {
        var argv = new List<string>
        {
            "cdidx",
            "find",
            "--query",
            options.Query!,
        };

        if (usesFindAll)
        {
            argv.Add("--all");
        }
        else
        {
            foreach (var path in options.PathPatterns)
                AddSearchFindAlternativeOption(argv, "--path", path);
        }

        if (options.Regex)
            argv.Add("--regex");
        if (options.Exact)
            argv.Add("--exact");
        if (options.Lang != null)
            AddSearchFindAlternativeOption(argv, "--lang", options.Lang);
        if (options.AllowUnknownLang)
            argv.Add("--allow-unknown-lang");
        foreach (var excludePath in options.ExcludePaths)
            AddSearchFindAlternativeOption(argv, "--exclude-path", excludePath);
        if (options.ExcludeTests)
            argv.Add("--exclude-tests");
        if (options.IncludeGenerated)
            argv.Add("--include-generated");
        if (options.LimitExplicit)
            AddSearchFindAlternativeOption(argv, "--limit", options.Limit);
        if (options.SnippetLinesExplicit)
            AddSearchFindAlternativeOption(argv, "--snippet-lines", options.SnippetLines);
        if (HasOption(cmdArgs, "--max-line-width"))
            AddSearchFindAlternativeOption(argv, "--max-line-width", options.MaxLineWidth);
        if (options.CountOnly)
            argv.Add("--count");
        if (options.StrictNotFound)
            argv.Add("--strict-not-found");
        if (options.AllowPartial)
            argv.Add("--allow-partial");

        if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount)
            AddSearchFindAlternativeOption(argv, "--format", options.OutputFormat);
        if (options.OutputFormat == OutputFormatJson
            || ProgramRunner.ContainsJsonOutputFlag(cmdArgs))
            argv.Add("--json");
        if (options.MaxJsonBytes.HasValue)
            AddSearchFindAlternativeOption(argv, "--max-json-bytes", options.MaxJsonBytes.Value);

        if (HasOption(cmdArgs, "--data-dir") && options.DataDir != null)
            AddSearchFindAlternativeOption(argv, "--data-dir", options.DataDir);
        if (options.DbPathExplicit)
            AddSearchFindAlternativeOption(argv, "--db", options.DbPath);
        if (options.ReadOnly)
            argv.Add("--read-only");
        if (options.Profile)
            argv.Add("--profile");
        if (options.Verbose)
            argv.Add("--verbose");
        if (options.SlowQueryMs.HasValue)
            AddSearchFindAlternativeOption(argv, "--slow-query-ms", options.SlowQueryMs.Value);
        if (HasOption(cmdArgs, "--quiet") || HasOption(cmdArgs, "-q") || HasOption(cmdArgs, "--silent"))
            argv.Add("--quiet");
        if (HasOption(cmdArgs, "--no-progress"))
            argv.Add("--no-progress");

        return argv;
    }

    private static void AddSearchFindAlternativeOption(List<string> argv, string name, string value)
    {
        argv.Add(name);
        argv.Add(value);
    }

    private static void AddSearchFindAlternativeOption(List<string> argv, string name, int value)
        => AddSearchFindAlternativeOption(argv, name, value.ToString(CultureInfo.InvariantCulture));

    private static string BuildSearchFindAlternativeReason(
        IReadOnlyList<string>? argv,
        IReadOnlyList<string> nonEquivalentOptions,
        IReadOnlyList<string> blockers)
    {
        if (argv != null)
        {
            return "find performs literal and regular-expression file scans; the query, scope, and every representable option were preserved in argv. The alternative is displayed only and was not executed.";
        }

        var parts = new List<string>();
        if (nonEquivalentOptions.Count > 0)
            parts.Add($"these search options have no safe find mapping: {string.Join(", ", nonEquivalentOptions)}");
        parts.AddRange(blockers);
        return parts.Count == 0
            ? "the invocation cannot be represented as one equivalent find command"
            : string.Join("; ", parts);
    }

    private static JsonObject BuildSearchFindAlternativeJson(
        IReadOnlyList<string>? argv,
        string reason,
        IReadOnlyList<string> nonEquivalentOptions,
        IReadOnlyList<string> blockers)
    {
        JsonObject? alternativeCommand = null;
        if (argv != null)
        {
            alternativeCommand = new JsonObject
            {
                ["argv"] = ToSearchFindAlternativeJsonArray(argv),
                ["posix_sh"] = ExcerptRecoveryCommandFormatter.RenderDisplayCommand(argv, RecoveryCommandShell.PosixSh),
                ["powershell"] = ExcerptRecoveryCommandFormatter.RenderDisplayCommand(argv, RecoveryCommandShell.PowerShell),
                ["display_only"] = true,
                ["executed"] = false,
            };
        }

        return new JsonObject
        {
            ["alternative_command"] = alternativeCommand,
            ["alternative_reason"] = reason,
            ["non_equivalent_options"] = ToSearchFindAlternativeJsonArray(nonEquivalentOptions),
            ["alternative_blockers"] = ToSearchFindAlternativeJsonArray(blockers),
            ["automatic_execution"] = false,
        };
    }

    private static JsonArray ToSearchFindAlternativeJsonArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    private static string RenderSearchFindAlternativeForCurrentShell(IReadOnlyList<string> argv)
        => ExcerptRecoveryCommandFormatter.RenderDisplayCommand(
            argv,
            OperatingSystem.IsWindows()
                ? RecoveryCommandShell.PowerShell
                : RecoveryCommandShell.PosixSh);
}
