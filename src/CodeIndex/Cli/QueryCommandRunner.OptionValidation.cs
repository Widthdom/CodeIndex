using System.Globalization;
using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static bool HasOption(string[] args, string optionName)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, optionName, StringComparison.Ordinal))
                return true;
            if (arg.StartsWith(optionName + "=", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Preview option validation now lives in the command-specific unsupported-option allowlists.
    // Keep this shim so the existing call sites stay simple while the actual fail-closed logic
    // runs through ParseArgs() + TryWriteUnsupportedOptionError().
    // preview 系オプションの検証はコマンド別 allowlist に寄せたため、この shim は常に null を返す。
    private static string? ValidatePreviewOptions(string commandName, string[] args, bool allowMaxLineWidth, bool allowFocusOptions) => null;

    private static bool TryWriteParseError(QueryCommandOptions options, string commandName)
        => TryWriteParseError(options, commandName, jsonOptions: null);

    private static bool TryWriteParseError(QueryCommandOptions options, string commandName, JsonSerializerOptions? jsonOptions)
        => TryWriteParseError(
            options,
            new QueryCommandInvocationContext(
                commandName,
                commandName,
                commandName,
                RecipeNameIsPositional: false,
                StructuredMachineUsageErrors: false),
            jsonOptions);

    private static bool TryWriteParseError(
        QueryCommandOptions options,
        QueryCommandInvocationContext invocationContext,
        JsonSerializerOptions? jsonOptions)
    {
        var dbPathError = BuildExplicitDbPathParseError(options);
        var inspectCursorScopeError = BuildInspectCursorScopeParseError(options, invocationContext.CommandName);
        if (options.ParseError == null && dbPathError == null && inspectCursorScopeError == null)
            return false;

        var primaryError = options.ParseError ?? dbPathError ?? inspectCursorScopeError!;
        var primaryHint = primaryError == dbPathError && options.ParseError == null
            ? "create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command."
            : primaryError == inspectCursorScopeError && options.ParseError == null && dbPathError == null
                ? "Pass this cursor back to the unchanged `cdidx inspect` query that returned it."
                : "fix the invalid or missing option value, then rerun with the command shape below.";
        var machineErrorOutput = options.Json
            && jsonOptions != null
            && (!invocationContext.StructuredMachineUsageErrors
                || options.InvocationMachineErrorOutputRequested);
        WriteParseError(primaryError, primaryHint, invocationContext, options, jsonOptions);
        if (options.ParseError != null
            && dbPathError != null
            && !machineErrorOutput)
        {
            WriteParseError(
                dbPathError,
                "create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command.",
                invocationContext,
                options,
                jsonOptions);
        }
        return true;
    }

    private static string? BuildInspectCursorScopeParseError(QueryCommandOptions options, string commandName)
    {
        if (string.Equals(commandName, "inspect", StringComparison.Ordinal)
            || options.CursorValue == null
            || !InspectGraphCursorCodec.TryParse(options.CursorValue, out _))
        {
            return null;
        }

        return "Error: inspect graph pagination cursors can only be used with the inspect command.";
    }

    private static bool TryWriteNonPositiveCoordinateRangeError(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool includeHumanOutput,
        params string[] coordinateOptionNames)
    {
        if ((!options.Json && !includeHumanOutput) || options.ParseError == null)
            return false;

        foreach (var optionName in coordinateOptionNames)
        {
            var errorPrefix = $"Error: {optionName} requires ";
            if (!options.ParseError.StartsWith(errorPrefix, StringComparison.Ordinal))
                continue;

            const string valueMarker = ", got '";
            var valueStart = options.ParseError.IndexOf(valueMarker, errorPrefix.Length, StringComparison.Ordinal);
            if (valueStart < 0)
                continue;
            valueStart += valueMarker.Length;
            var valueEnd = options.ParseError.IndexOf("'. Hint:", valueStart, StringComparison.Ordinal);
            if (valueEnd < 0)
                continue;

            var rawValue = options.ParseError[valueStart..valueEnd];
            if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value > 0)
                continue;

            CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                $"requested line {rawValue} is outside the valid range beginning at 1.",
                CommandExitCodes.InvalidArgument,
                "Use a line number of 1 or greater.",
                includeHumanOutput ? GetUsageLineOrThrow("excerpt") : null,
                errorCode: CommandErrorCodes.LineOutOfRange,
                category: "range",
                command: includeHumanOutput ? "excerpt" : null);
            return true;
        }

        return false;
    }

    private static void WriteParseError(
        string error,
        string hint,
        QueryCommandInvocationContext invocationContext,
        QueryCommandOptions options,
        JsonSerializerOptions? jsonOptions)
    {
        if (options.Json
            && jsonOptions != null
            && (!invocationContext.StructuredMachineUsageErrors
                || options.InvocationMachineErrorOutputRequested))
        {
            if (invocationContext.StructuredMachineUsageErrors)
            {
                WriteInvocationUsageError(
                    StripErrorPrefix(error),
                    options,
                    hint,
                    ExtractErrorCode(error));
                return;
            }

            CommandErrorWriter.WriteJsonOrHuman(
                true,
                jsonOptions,
                StripErrorPrefix(error),
                CommandExitCodes.UsageError,
                hint,
                invocationContext.UsageLine,
                ExtractErrorCode(error),
                category: "usage",
                command: invocationContext.CommandName);
            return;
        }

        CommandErrorWriter.Write(
            StripErrorPrefix(error),
            hint,
            invocationContext.UsageLine,
            ExtractErrorCode(error)
                ?? (string.Equals(invocationContext.CommandName, "outline", StringComparison.Ordinal)
                    ? CommandErrorCodes.UsageError
                    : null));
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

    private static bool TryWriteInvalidOutlineKindFilterError(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        if (options.Kind == null)
            return false;

        var kinds = BuildOutlineKindFilters(options.Kind);
        if (kinds.Count == 0)
        {
            CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                $"invalid --kind value `{ConsoleUi.FormatBoundedValue(options.Kind)}`.",
                CommandExitCodes.InvalidArgument,
                $"use one or more of: {string.Join(", ", KnownSymbolKindFilters)}.",
                GetUsageLineOrThrow("outline"),
                CommandErrorCodes.UsageError,
                command: "outline");
            return true;
        }

        foreach (var kind in kinds)
        {
            if (KnownSymbolKindFilters.Contains(kind))
                continue;

            CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                $"invalid --kind value `{ConsoleUi.FormatBoundedValue(kind)}`.",
                CommandExitCodes.InvalidArgument,
                $"use one or more of: {string.Join(", ", KnownSymbolKindFilters)}.",
                GetUsageLineOrThrow("outline"),
                CommandErrorCodes.UsageError,
                command: "outline");
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

    private static bool TryWriteUnsupportedOptionError(
        string commandName,
        string[] cmdArgs,
        IEnumerable<string> supportedOptions,
        string? queryLiteral = null,
        JsonSerializerOptions? jsonOptions = null)
        => TryWriteUnsupportedOptionError(
            new QueryCommandInvocationContext(
                commandName,
                commandName,
                commandName,
                RecipeNameIsPositional: false,
                StructuredMachineUsageErrors: false),
            cmdArgs,
            supportedOptions,
            queryLiteral: queryLiteral,
            jsonOptions: jsonOptions);

    private static bool TryWriteUnsupportedOptionError(
        QueryCommandInvocationContext invocationContext,
        string[] cmdArgs,
        IEnumerable<string> supportedOptions,
        QueryCommandOptions? options = null,
        string? queryLiteral = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        var commandName = invocationContext.CommandName;

        void WriteOptionError(string message, string hint, string? errorCode = null)
        {
            if (jsonOptions != null
                && ProgramRunner.ContainsJsonOutputFlag(cmdArgs))
            {
                if (invocationContext.StructuredMachineUsageErrors && options != null)
                {
                    WriteInvocationUsageError(
                        message,
                        options,
                        hint,
                        errorCode);
                    return;
                }

                CommandErrorWriter.WriteJsonOrHuman(
                    true,
                    jsonOptions,
                    message,
                    CommandExitCodes.UsageError,
                    hint,
                    invocationContext.UsageLine,
                    errorCode ?? CommandErrorCodes.UsageError,
                    command: commandName);
                return;
            }

            CommandErrorWriter.Write(
                message,
                hint,
                invocationContext.UsageLine,
                errorCode
                    ?? (string.Equals(commandName, "outline", StringComparison.Ordinal)
                        ? CommandErrorCodes.UsageError
                        : null));
        }

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
                    WriteOptionError(
                        "--json=<format> is not supported by outline because outline emits one JSON object.",
                        "use plain `--json`; add `--limit <n>` to cap symbols and read the paging metadata (`returned_symbol_count`, `total_symbol_count`, `next_cursor`).");
                    return true;
                }
                if (commandName == "validate" && string.Equals(inlineValue, JsonOutputFormatArray, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                WriteOptionError(
                    commandName == "validate"
                        ? "--json=<format> for validate only supports 'array'."
                        : "--json=<format> is only supported by 'search', 'files', 'symbols', and validate's array output.",
                    commandName == "validate"
                        ? "use plain `--json` or `--json=array`."
                        : "use plain `--json` here, rerun search/files/symbols with `--json=array`, or rerun validate with `--json=array`.");
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
                WriteOptionError(
                    "--group-by-name is only supported by 'hotspots'.",
                    "remove `--group-by-name` here, or rerun with `cdidx hotspots --group-by-name ...`.");
                return true;
            }

            if (normalizedArg == "--group-by")
            {
                WriteOptionError(
                    "--group-by is only supported by 'hotspots'.",
                    "remove `--group-by` here, or rerun with `cdidx hotspots --group-by <symbol|file|statement> ...`.");
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
            WriteOptionError(
                $"{displayArg} is not supported for {commandName}.",
                hint,
                CommandErrorCodes.UsageError);
            return true;
        }

        return false;
    }
}
