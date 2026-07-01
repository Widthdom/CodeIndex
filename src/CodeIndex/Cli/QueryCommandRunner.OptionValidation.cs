using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static bool TryWriteParseError(QueryCommandOptions options, string commandName)
        => TryWriteParseError(options, commandName, jsonOptions: null);

    private static bool TryWriteParseError(QueryCommandOptions options, string commandName, JsonSerializerOptions? jsonOptions)
    {
        var dbPathError = BuildExplicitDbPathParseError(options);
        if (options.ParseError == null && dbPathError == null)
            return false;

        var primaryError = options.ParseError ?? dbPathError!;
        var primaryHint = primaryError == dbPathError && options.ParseError == null
            ? "create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command."
            : "fix the invalid or missing option value, then rerun with the command shape below.";
        WriteParseError(primaryError, primaryHint, commandName, options, jsonOptions);
        if (options.ParseError != null && dbPathError != null)
            WriteParseError(
                dbPathError,
                "create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command.",
                commandName,
                options,
                jsonOptions);
        return true;
    }

    private static void WriteParseError(
        string error,
        string hint,
        string commandName,
        QueryCommandOptions options,
        JsonSerializerOptions? jsonOptions)
    {
        if (options.Json && jsonOptions != null)
        {
            CommandErrorWriter.WriteJsonOrHuman(
                true,
                jsonOptions,
                StripErrorPrefix(error),
                CommandExitCodes.UsageError,
                hint,
                GetUsageLineOrThrow(commandName),
                ExtractErrorCode(error),
                category: "usage");
            return;
        }

        CommandErrorWriter.Write(
            StripErrorPrefix(error),
            hint,
            GetUsageLineOrThrow(commandName),
            ExtractErrorCode(error));
    }

    private static string? BuildExplicitDbPathParseError(QueryCommandOptions options)
    {
        if (options.StatusConfig)
            return null;
        if (!options.DbPathExplicit)
            return null;
        if (string.IsNullOrWhiteSpace(options.DbPath))
            return BuildMissingOptionValueError("--db");
        if (options.DbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (File.Exists(LongPath.EnsureWindowsPrefix(options.DbPath)))
            return null;

        return $"Error [{CommandErrorCodes.DbNotFound}]: --db '{FormatDbDiagnosticValue(options.DbPath)}' does not point to an existing database file.";
    }

    private static readonly HashSet<string> KnownVisibilityFilters = new(StringComparer.Ordinal)
    {
        "public",
        "protected",
        "internal",
        "private",
    };

    private static void AddVisibilityFilterValues(string optionName, string rawValue, List<string> target, Action<string> addParseError)
    {
        if (!ValidateCsvBounds(optionName, rawValue, MaxVisibilityFilterCsvLength, MaxVisibilityFilterCsvEntries, addParseError))
            return;

        var values = rawValue
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (values.Count == 0)
        {
            addParseError($"Error: {optionName} requires one or more of public, protected, internal, private.");
            return;
        }

        foreach (var value in values)
        {
            if (!KnownVisibilityFilters.Contains(value))
            {
                addParseError($"Error: unsupported {optionName} value '{ConsoleUi.FormatBoundedValue(value)}'. Use one or more of public, protected, internal, private.");
                continue;
            }

            if (!target.Contains(value, StringComparer.Ordinal))
                target.Add(value);
        }
    }

    private static bool TryWriteInvalidKindFilterError(QueryCommandOptions options, string commandName, IReadOnlyCollection<string> acceptedKinds, params IReadOnlyCollection<string>[] alternateAcceptedKinds)
    {
        if (options.Kind != null
            && !acceptedKinds.Contains(options.Kind)
            && !alternateAcceptedKinds.Any(kinds => kinds.Contains(options.Kind)))
        {
            CommandErrorWriter.Write(
                $"invalid --kind value `{ConsoleUi.FormatBoundedValue(options.Kind)}`.",
                $"use one of: {string.Join(", ", acceptedKinds)}.",
                GetUsageLineOrThrow(commandName));
            return true;
        }

        return false;
    }

    private static bool TryWriteInvalidOutlineKindFilterError(QueryCommandOptions options)
    {
        if (options.Kind == null)
            return false;

        var kinds = BuildOutlineKindFilters(options.Kind);
        if (kinds.Count == 0)
        {
            CommandErrorWriter.Write(
                $"invalid --kind value `{ConsoleUi.FormatBoundedValue(options.Kind)}`.",
                $"use one or more of: {string.Join(", ", KnownSymbolKindFilters)}.",
                GetUsageLineOrThrow("outline"));
            return true;
        }

        foreach (var kind in kinds)
        {
            if (KnownSymbolKindFilters.Contains(kind))
                continue;

            CommandErrorWriter.Write(
                $"invalid --kind value `{ConsoleUi.FormatBoundedValue(kind)}`.",
                $"use one or more of: {string.Join(", ", KnownSymbolKindFilters)}.",
                GetUsageLineOrThrow("outline"));
            return true;
        }

        return false;
    }

    internal static bool IsKnownUnusedBucket(string value)
        => OrderedUnusedBuckets.Contains(value, StringComparer.Ordinal);

    internal static bool IsKnownUnusedConfidence(string value)
        => value is "medium" or "low";

    private static bool TryWriteInvalidUnusedFilterError(QueryCommandOptions options)
    {
        if (options.UnusedBucket != null && !IsKnownUnusedBucket(options.UnusedBucket))
        {
            CommandErrorWriter.Write(
                $"invalid --bucket value `{ConsoleUi.FormatBoundedValue(options.UnusedBucket)}`.",
                $"use one of: {string.Join(", ", OrderedUnusedBuckets)}.",
                GetUsageLineOrThrow("unused"));
            return true;
        }

        if (options.MinUnusedConfidence != null && !IsKnownUnusedConfidence(options.MinUnusedConfidence))
        {
            CommandErrorWriter.Write(
                $"invalid --min-confidence value `{ConsoleUi.FormatBoundedValue(options.MinUnusedConfidence)}`.",
                "use one of: medium, low.",
                GetUsageLineOrThrow("unused"));
            return true;
        }

        return false;
    }

    private static bool TryWriteUnsupportedOptionError(string commandName, string[] cmdArgs, IEnumerable<string> supportedOptions, string? queryLiteral = null)
    {
        var supported = supportedOptions.ToHashSet(StringComparer.Ordinal);
        var skippedQueryLiteral = false;
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (!arg.StartsWith("-", StringComparison.Ordinal))
                continue;

            if (queryLiteral != null && !skippedQueryLiteral && arg == queryLiteral)
            {
                skippedQueryLiteral = true;
                continue;
            }

            var inlineValue = TrySplitInlineOptionValue(arg, out var inlineOptionName)
                ? arg[(inlineOptionName!.Length + 1)..]
                : null;
            var normalizedArg = inlineOptionName ?? arg;
            if (arg.StartsWith("--check=", StringComparison.Ordinal) && supported.Contains("--check"))
                normalizedArg = "--check";
            if (normalizedArg == "--json"
                && !string.Equals(arg, "--json", StringComparison.Ordinal)
                && commandName != "search"
                && commandName != "files"
                && commandName != "symbols")
            {
                if (commandName == "outline")
                {
                    CommandErrorWriter.Write(
                        "--json=<format> is not supported by outline because outline emits one JSON object.",
                        "use plain `--json`; add `--limit <n>` to cap symbols and read the paging metadata (`returned_symbol_count`, `total_symbol_count`, `next_cursor`).",
                        GetUsageLineOrThrow(commandName));
                    return true;
                }
                if (commandName == "validate" && string.Equals(inlineValue, JsonOutputFormatArray, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CommandErrorWriter.Write(
                    commandName == "validate"
                        ? "--json=<format> for validate only supports 'array'."
                        : "--json=<format> is only supported by 'search', 'files', 'symbols', and validate's array output.",
                    commandName == "validate"
                        ? "use plain `--json` or `--json=array`."
                        : "use plain `--json` here, rerun search/files/symbols with `--json=array`, or rerun validate with `--json=array`.",
                    GetUsageLineOrThrow(commandName));
                return true;
            }

            if (supported.Contains(normalizedArg))
            {
                if (normalizedArg == "--" && normalizedArg == arg && i + 1 < cmdArgs.Length)
                    i++;
                if (normalizedArg == arg && ValueTakingOptions.Contains(normalizedArg) && i + 1 < cmdArgs.Length)
                    i++;
                continue;
            }

            // `--query` is parsed specially so commands without query literals can emit the
            // dedicated parser message instead of the generic unsupported-option error.
            // `--query` は専用エラー文言を出したいので generic unsupported 判定からは外す。
            if (normalizedArg == "--query")
            {
                if (normalizedArg == arg && ValueTakingOptions.Contains(normalizedArg) && i + 1 < cmdArgs.Length)
                    i++;
                continue;
            }

            if (normalizedArg == "--group-by-name")
            {
                CommandErrorWriter.Write(
                    "--group-by-name is only supported by 'hotspots'.",
                    "remove `--group-by-name` here, or rerun with `cdidx hotspots --group-by-name ...`.",
                    GetUsageLineOrThrow(commandName));
                return true;
            }

            if (normalizedArg == "--group-by")
            {
                CommandErrorWriter.Write(
                    "--group-by is only supported by 'hotspots'.",
                    "remove `--group-by` here, or rerun with `cdidx hotspots --group-by <symbol|file|statement> ...`.",
                    GetUsageLineOrThrow(commandName));
                return true;
            }

            if (normalizedArg == arg && ValueTakingOptions.Contains(normalizedArg) && i + 1 < cmdArgs.Length)
                i++;

            // Suggest the closest accepted flag for this command when the user mistypes
            // a flag name (e.g. `--paht` → `--path`). Built on the same suggester used for
            // subcommand typos so the recovery experience is consistent (#1582).
            // TrySplitInlineOptionValue only splits inline `=value` when the prefix is a
            // known value-taking option, so for an unrecognized `--paht=foo` the normalized
            // arg keeps the `=value`. Strip any trailing `=value` here so the matcher can
            // still find `--path` from `--paht=foo`.
            // ユーザーがフラグ名をミスタイプしたとき (例: `--paht` → `--path`) に
            // そのコマンドで受理される最も近いフラグを提案する。サブコマンドの did-you-mean と
            // 同じ suggester を共用し、回復体験を統一する (#1582)。
            // TrySplitInlineOptionValue は prefix が既知の value-taking option のときだけ
            // inline `=value` を分解するため、`--paht=foo` のように未知のオプションでは
            // `=value` が残る。matcher のために `=` 以降を除去してから候補を探す。
            var nameForSuggestion = normalizedArg;
            var eq = nameForSuggestion.IndexOf('=');
            if (eq > 0)
                nameForSuggestion = nameForSuggestion[..eq];
            var suggestion = ConsoleUi.FindClosestMatch(nameForSuggestion, supported.Where(o => o != "--"));
            var displayArg = ConsoleUi.FormatBoundedValue(arg);
            var hint = suggestion == null
                ? $"remove `{displayArg}` and rerun, or use only the options shown in `{commandName} --help`."
                : $"Did you mean: {suggestion}? Remove `{displayArg}` and rerun, or use `{suggestion}` if that is what you meant.";
            CommandErrorWriter.Write(
                $"{displayArg} is not supported for {commandName}.",
                hint,
                GetUsageLineOrThrow(commandName));
            return true;
        }

        return false;
    }
}
