using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunSymbols(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("symbols", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("symbols", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("symbols"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteUnsupportedOutputFormat("symbols", options, SymbolOutputFormats, "Use `--format json` for symbol rows, `--format compact` for bounded compact rows, `--format count` for symbol totals, or `--format lsp|qf|sarif` for editor/diagnostic locations."))
            return CommandExitCodes.UsageError;
        if (TryWriteDiscoveryOutputControlUsageError("symbols", options))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "symbols", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteParseError(options, "symbols"))
            return CommandExitCodes.UsageError;
        if (TryWriteBlankQueryError(options, "symbols"))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "symbols", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        var exactBareVerbatimOnly = exact && string.Equals(options.Lang, "csharp", StringComparison.OrdinalIgnoreCase) && (
            (options.Query is not null && IsBareVerbatimQueryToken(options.Query) && options.ExtraNames.Count == 0) ||
            (options.Query is null && options.ExtraNames.Count > 0 && options.ExtraNames.All(IsBareVerbatimQueryToken)));
        var (symbolQueries, hadExplicitInput) = BuildSymbolQueryList(options);
        if (hadExplicitInput && symbolQueries == null)
        {
            if (exactBareVerbatimOnly && options.CountOnly)
            {
                var countQuery = options.Query ?? string.Join(" ", options.ExtraNames);
                if (options.Json)
                {
                    var json = JsonSerializer.Serialize(
                        new QueryCountFilesJsonResult(0, 0, countQuery),
                        CliJsonSerializerContextFactory.Create(jsonOptions).QueryCountFilesJsonResult);
                    return WriteJsonObjectWithOptionalByteLimit(
                        json,
                        options,
                        "symbols count",
                        "Narrow the query or increase --max-json-bytes.",
                        "symbols");
                }

                Console.WriteLine("0");
                return CommandExitCodes.Success;
            }
            // Fail closed: an explicit name/query was provided but normalized to empty or a bare
            // verbatim prefix (e.g. `|`, `@`, `--name ""`). Returning null here would broaden into
            // an unfiltered symbol dump. /
            // 明示入力が正規化で空、または verbatim 接頭辞単独（`|`、`@`、`--name ""` など）になった場合は必ず拒否する。
            CommandErrorWriter.WriteStderr("Error: symbol name list is empty after normalization. Check for empty --name values, bare verbatim prefixes like `@`, or bare `|` separators. / シンボル名リストが正規化の結果空です。--name の空値、`@` のような verbatim 接頭辞単独、単独の `|` を確認してください。");
            return CommandExitCodes.UsageError;
        }
        if (symbolQueries != null && symbolQueries.Count > MaxSymbolQueryNames)
        {
            CommandErrorWriter.WriteStderr($"Error: too many symbol names ({symbolQueries.Count}); maximum is {MaxSymbolQueryNames}. Split the request into smaller batches. / シンボル名が多すぎます（{symbolQueries.Count}件、上限は {MaxSymbolQueryNames} 件）。分割してください。");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountSearchSymbolsTotal(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var hasExactPredicateForCount = exact && symbolQueries is { Count: > 0 };
                var exactSignalForCount = reader.GetSymbolsExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
                var multiNameExactHintForCount = symbolQueries != null && symbolQueries.Count > 1;
                var exactZeroHintForCount = multiNameExactHintForCount
                    ? BuildExactZeroHint(
                        exact,
                        () => reader.AnySearchSymbols(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        r => r.Name)
                    : BuildExactZeroHint(
                        exact && symbolQueries != null && symbolQueries.Count > 0,
                        () => reader.CountSearchSymbols(symbolQueries, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                        () => reader.CountSearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        r => r.Name);
                WriteExactSymbolWarningIfNeeded(hasExactPredicateForCount, options.Json, exactSignalForCount, reader, options);
                if (counts.Count == 0)
                {
                    if (options.Json)
                    {
                        var payload = BuildCountJsonPayload(reader, jsonOptions, count: 0, files: 0, query: options.Query, exactZeroHint: exactZeroHintForCount, exactSignal: hasExactPredicateForCount ? exactSignalForCount : null, queryOptions: options);
                        return WriteJsonPayloadWithOptionalByteLimit(
                            payload,
                            options,
                            jsonOptions,
                            "symbols",
                            "symbols count",
                            "Narrow the query or increase --max-json-bytes.");
                    }

                    Console.WriteLine("0");
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var payload = BuildCountJsonPayload(reader, jsonOptions, counts.Count, counts.FileCount, query: options.Query, exactSignal: hasExactPredicateForCount ? exactSignalForCount : null, queryOptions: options);
                    return WriteJsonPayloadWithOptionalByteLimit(
                        payload,
                        options,
                        jsonOptions,
                        "symbols",
                        "symbols count",
                        "Narrow the query or increase --max-json-bytes.");
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                }
                return CommandExitCodes.Success;
            }

            var results = reader.SearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters, sortMode: options.SymbolSortMode);
            var hasExactPredicate = exact && symbolQueries is { Count: > 0 };
            var exactSignal = reader.GetSymbolsExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            var multiNameExactHint = symbolQueries != null && symbolQueries.Count > 1;
            var exactZeroHint = multiNameExactHint
                ? BuildExactZeroHint(
                    exact,
                    () => reader.AnySearchSymbols(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    r => r.Name)
                : BuildExactZeroHint(
                    exact && symbolQueries != null && symbolQueries.Count > 0,
                    () => reader.CountSearchSymbols(symbolQueries, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                    () => reader.CountSearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    r => r.Name);
            WriteExactSymbolWarningIfNeeded(hasExactPredicate, options.Json, exactSignal, reader, options);
            if (results.Count == 0)
            {
                if (ShouldWriteBoundedDiscoveryJsonPayload(options))
                {
                    var payloadExitCode = WriteBoundedDiscoveryJsonPayload(
                        reader,
                        options,
                        jsonOptions,
                        "symbols",
                        "symbols",
                        results,
                        totalCount: 0,
                        fileCount: 0,
                        rowFactory: result => ToSymbolDiscoveryJsonNode(result, jsonOptions, options.OutputFormat == OutputFormatCompact),
                        exactSignal: hasExactPredicate ? exactSignal : null);
                    return payloadExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : payloadExitCode;
                }
                if (options.OutputFormat == OutputFormatJson && options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    Console.WriteLine(JsonSerializer.Serialize(results, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult));
                    return ZeroResultExitCode(options);
                }
                if (TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No symbols found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteSymbolExtractionCapabilityHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader);
                }
                return ZeroResultExitCode(options);
            }

            if (ShouldWriteBoundedDiscoveryJsonPayload(options))
            {
                var counts = reader.CountSearchSymbolsTotal(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                return WriteBoundedDiscoveryJsonPayload(
                    reader,
                    options,
                    jsonOptions,
                    "symbols",
                    "symbols",
                    results,
                    counts.Count,
                    counts.FileCount,
                    result => ToSymbolDiscoveryJsonNode(result, jsonOptions, options.OutputFormat == OutputFormatCompact),
                    hasExactPredicate ? exactSignal : null);
            }

            if (options.OutputFormat == OutputFormatLsp)
            {
                WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                return CommandExitCodes.Success;
            }
            if (options.OutputFormat == OutputFormatQf)
            {
                WriteQuickfix(results.Select(ToSymbolQuickfixItem));
                return CommandExitCodes.Success;
            }
            if (options.OutputFormat == OutputFormatSarif)
            {
                WriteSarif(results.Select(ToSymbolSarifItem), jsonOptions);
                return CommandExitCodes.Success;
            }
            if (options.Json)
            {
                if (options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    Console.WriteLine(JsonSerializer.Serialize(results, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult));
                }
                else
                {
                    foreach (var r in results)
                    {
                        if (hasExactPredicate)
                            WriteJsonResultWithExactSignal(r, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolResult, exactSignal, jsonOptions);
                        else
                            Console.WriteLine(JsonSerializer.Serialize(r, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolResult));
                    }
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var lineRange = r.EndLine > r.StartLine
                        ? $"{r.StartLine}-{r.EndLine}"
                        : r.StartLine.ToString();
                    Console.WriteLine($"{ConsoleUi.ColorizeKind(r.Kind, 10)} {r.Name,-40} {r.Path}:{lineRange}{FormatSymbolRankSuffix(r)}");
                }
                var symFileCount = results.Select(r => r.Path).Distinct().Count();
                var sortSummary = options.SymbolSortMode == SymbolSortMode.Name ? string.Empty : $"; sort={options.SymbolSortMode.ToString().ToLowerInvariant()}";
                CommandErrorWriter.WriteStderr($"({results.Count} symbols in {symFileCount} files{sortSummary})");
            }
            return CommandExitCodes.Success;
        });
    }

    private static string FormatSymbolRankSuffix(SymbolResult result)
    {
        if (result.SortMode == null)
            return string.Empty;

        var parts = new List<string>();
        if (result.ReferenceCount.HasValue)
            parts.Add($"refs={result.ReferenceCount.Value}");
        if (result.HotspotScore.HasValue)
            parts.Add($"hotspot={result.HotspotScore.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (result.RankingReferenceScore.HasValue)
            parts.Add($"rank_refs={result.RankingReferenceScore.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (result.RankingHotspotScore.HasValue)
            parts.Add($"rank_hotspot={result.RankingHotspotScore.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (result.GenericNamePenalty is < 1.0)
            parts.Add($"name_penalty={result.GenericNamePenalty.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (result.StructuralRankPenalty is < 1.0)
            parts.Add($"struct_penalty={result.StructuralRankPenalty.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (result.DefinitionSites is > 1)
            parts.Add($"defs={result.DefinitionSites.Value}");
        if (result.SizeLines.HasValue)
            parts.Add($"size={result.SizeLines.Value}");
        if (result.ComplexityScore.HasValue)
            parts.Add($"complexity={result.ComplexityScore.Value.ToString("0.###", CultureInfo.InvariantCulture)}");

        return parts.Count == 0 ? string.Empty : $" [{string.Join(", ", parts)}]";
    }

    public static int RunFiles(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("files", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("files", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("files"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteUnsupportedOutputFormat("files", options, FilesOutputFormats, "Use `--format json` for file rows, `--format compact` for bounded compact rows, or `--format count` for file totals."))
            return CommandExitCodes.UsageError;
        if (TryWriteDiscoveryOutputControlUsageError("files", options))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "files"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedExtraPositionals("files", options))
            return CommandExitCodes.UsageError;
        var filesScope = BuildFilesScopeFilters(options);

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountListFiles(options.Query, options.Lang, filesScope.PathPatterns, filesScope.ExcludePaths, filesScope.ExcludeTests, options.Since);
                if (options.Json)
                {
                    var payload = BuildCountJsonPayload(
                        reader,
                        jsonOptions,
                        counts.Count,
                        counts.FileCount,
                        query: options.Query,
                        queryOptions: options);
                    if (options.RawBytes)
                        AddFileCountBytesJsonFields(payload, counts);
                    return WriteJsonPayloadWithOptionalByteLimit(
                        payload,
                        options,
                        jsonOptions,
                        "files",
                        "files count",
                        "Narrow the query or increase --max-json-bytes.");
                }
                else
                    Console.WriteLine(options.RawBytes ? FormatFileCountBytesSummary(counts) : $"{counts.Count}");
                return CommandExitCodes.Success;
            }

            var results = reader.ListFiles(options.Query, options.Limit, options.Lang, filesScope.PathPatterns, filesScope.ExcludePaths, filesScope.ExcludeTests, options.Since, orderBySize: options.RawBytes);
            if (results.Count == 0)
            {
                if (options.Json)
                {
                    if (ShouldWriteBoundedDiscoveryJsonPayload(options))
                    {
                        var payloadExitCode = WriteBoundedDiscoveryJsonPayload(
                            reader,
                            options,
                            jsonOptions,
                            "files",
                            "files",
                            results,
                            totalCount: 0,
                            fileCount: 0,
                            rowFactory: result => ToFileDiscoveryJsonNode(result, jsonOptions, options.OutputFormat == OutputFormatCompact));
                        return payloadExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : payloadExitCode;
                    }
                    Console.WriteLine(options.JsonOutputFormat == JsonOutputFormatArray
                        ? "[]"
                        : BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "files", queryOptions: options).ToJsonString(jsonOptions));
                }
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No files found", options));
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader);
                }
                return ZeroResultExitCode(options);
            }

            if (ShouldWriteBoundedDiscoveryJsonPayload(options))
            {
                var counts = reader.CountListFiles(options.Query, options.Lang, filesScope.PathPatterns, filesScope.ExcludePaths, filesScope.ExcludeTests, options.Since);
                return WriteBoundedDiscoveryJsonPayload(
                    reader,
                    options,
                    jsonOptions,
                    "files",
                    "files",
                    results,
                    counts.Count,
                    counts.FileCount,
                    result => ToFileDiscoveryJsonNode(result, jsonOptions, options.OutputFormat == OutputFormatCompact));
            }

            if (options.Json)
            {
                var context = CliJsonSerializerContextFactory.Create(jsonOptions);
                if (options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    Console.WriteLine(JsonSerializer.Serialize(results, context.ListFileResult));
                }
                else
                {
                    foreach (var r in results)
                        Console.WriteLine(JsonSerializer.Serialize(r, context.FileResult));
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var size = options.RawBytes ? $"{r.Size.ToString(CultureInfo.InvariantCulture)} bytes" : ConsoleUi.FormatBytes(r.Size);
                    Console.WriteLine($"{r.Lang ?? "?",-12} {r.Lines,6} lines  {size,12}  {r.Path}");
                }
                CommandErrorWriter.WriteStderr($"({results.Count} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    /// <summary>
    /// Build the OR-joined name list for `symbols`: first positional + extra positionals + --name values.
    /// Pipe characters are treated as literal name characters so operator symbols like `operator |` remain searchable.
    /// Multi-name queries must use repeated positional args or `--name` flags.
    /// `symbols` コマンド用の名前リストを組み立て（最初の positional + 追加 positional + --name）。
    /// `|` は名前文字として扱うので `operator |` などの演算子シンボルも検索可能。複数名指定は繰り返し positional か `--name` で行う。
    /// </summary>
    internal static (List<string>? Queries, bool HadExplicitInput) BuildSymbolQueryList(QueryCommandOptions options)
    {
        var raw = new List<string>();
        if (options.Query != null)
            raw.Add(options.Query);
        raw.AddRange(options.ExtraNames);
        var hadExplicitInput = raw.Count > 0;
        if (!hadExplicitInput)
            return (null, false);
        var deduped = raw.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        if (deduped.Any(IsBareVerbatimQueryToken))
            return (null, hadExplicitInput);
        return (deduped.Count == 0 ? null : deduped, hadExplicitInput);
    }

    private sealed record FileListScopeFilters(
        IReadOnlyList<string> PathPatterns,
        IReadOnlyList<string> ExcludePaths,
        bool ExcludeTests);

    private static FileListScopeFilters BuildFilesScopeFilters(QueryCommandOptions options)
    {
        if (!options.ExcludeTests || options.PathPatterns.Count > 0)
        {
            return new(
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests);
        }

        var pathPatterns = new List<string>(options.PathPatterns);
        AddDistinct(pathPatterns, SearchAuditRecipes.DefaultSourcePathPatterns);
        var excludePaths = new List<string>(options.ExcludePaths);
        AddDistinct(excludePaths, SearchAuditRecipes.DefaultSourceExcludePaths);
        return new(pathPatterns, excludePaths, ExcludeTests: true);
    }

    private static void AddFileCountBytesJsonFields(JsonObject payload, QueryCountResult counts)
    {
        payload["total_bytes"] = counts.TotalBytes ?? 0;
        payload["average_bytes"] = counts.AverageBytes ?? 0;
        payload["max_bytes"] = counts.MaxBytes ?? 0;
        payload["max_bytes_path"] = counts.MaxBytesPath;
        payload["bytes_authoritative"] = counts.BytesAuthoritative ?? true;
    }

    private static string FormatFileCountBytesSummary(QueryCountResult counts)
    {
        var totalBytes = counts.TotalBytes ?? 0;
        var averageBytes = counts.AverageBytes ?? 0;
        var maxBytes = counts.MaxBytes ?? 0;
        var maxPath = string.IsNullOrEmpty(counts.MaxBytesPath)
            ? "none"
            : counts.MaxBytesPath;
        var authority = (counts.BytesAuthoritative ?? true) ? "true" : "false";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{counts.Count} files, {totalBytes} bytes total, average {averageBytes:0.##} bytes, max {maxBytes} bytes ({maxPath}), bytes_authoritative: {authority}");
    }

    private static bool ShouldWriteBoundedDiscoveryJsonPayload(QueryCommandOptions options)
        => options.OutputFormat == OutputFormatCompact || options.SummaryOnly || options.MaxJsonBytes.HasValue;

    private static bool TryWriteDiscoveryOutputControlUsageError(string commandName, QueryCommandOptions options)
    {
        if (!options.SummaryOnly && !options.MaxJsonBytes.HasValue)
            return false;
        if (options.Json && options.OutputFormat is OutputFormatJson or OutputFormatCompact or OutputFormatCount)
            return false;

        var control = options.SummaryOnly ? "--summary-only" : "--max-json-bytes";
        WriteUsageError(
            $"{control} is only supported with {commandName} JSON, compact, or count output.",
            GetUsageLineOrThrow(commandName),
            $"Use `cdidx {commandName} --json {control}` or `cdidx {commandName} --format compact {control}`.");
        return true;
    }

    private static int WriteBoundedDiscoveryJsonPayload<T>(
        DbReader reader,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string commandName,
        string resultsKey,
        IReadOnlyList<T> results,
        int totalCount,
        int fileCount,
        Func<T, JsonNode?> rowFactory,
        ExactQuerySignal? exactSignal = null)
    {
        var jsonNodeOptions = EnsureJsonNodeSerializerOptions(jsonOptions);
        var requestedRows = options.SummaryOnly ? 0 : results.Count;
        var json = BuildBoundedDiscoveryJson(requestedRows);
        if (!options.MaxJsonBytes.HasValue)
        {
            Console.WriteLine(json);
            return CommandExitCodes.Success;
        }

        if (JsonFitsByteLimit(json, options.MaxJsonBytes.Value))
        {
            Console.WriteLine(json);
            return CommandExitCodes.Success;
        }

        string? bestJson = null;
        var low = 0;
        var high = requestedRows;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var candidate = BuildBoundedDiscoveryJson(mid);
            if (JsonFitsByteLimit(candidate, options.MaxJsonBytes.Value))
            {
                bestJson = candidate;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (bestJson != null)
        {
            Console.WriteLine(bestJson);
            return CommandExitCodes.Success;
        }

        return WriteJsonObjectWithOptionalByteLimit(
            BuildBoundedDiscoveryJson(0),
            options,
            $"{commandName} compact",
            "Use --summary-only, reduce --limit, or increase --max-json-bytes.",
            commandName);

        string BuildBoundedDiscoveryJson(int emittedRows)
        {
            var payload = BuildBoundedDiscoveryPayload(
                reader,
                options,
                jsonOptions,
                resultsKey,
                results,
                totalCount,
                fileCount,
                Math.Clamp(emittedRows, 0, results.Count),
                rowFactory,
                exactSignal);
            return payload.ToJsonString(jsonNodeOptions);
        }
    }

    private static JsonObject BuildBoundedDiscoveryPayload<T>(
        DbReader reader,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string resultsKey,
        IReadOnlyList<T> results,
        int totalCount,
        int fileCount,
        int emittedRows,
        Func<T, JsonNode?> rowFactory,
        ExactQuerySignal? exactSignal)
    {
        var omittedCount = Math.Max(0, totalCount - emittedRows);
        var rowLimitReached = totalCount > results.Count;
        var byteLimitReached = !options.SummaryOnly && options.MaxJsonBytes.HasValue && emittedRows < results.Count;
        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["count"] = totalCount,
            ["file_count"] = fileCount,
            ["emitted_count"] = emittedRows,
            ["omitted_count"] = omittedCount,
            ["truncated"] = omittedCount > 0,
        };

        if (options.OutputFormat == OutputFormatCompact)
            payload["format"] = OutputFormatCompact;
        if (options.SummaryOnly)
            payload["summary_only"] = true;
        if (options.MaxJsonBytes.HasValue)
            payload["max_json_bytes"] = options.MaxJsonBytes.Value;
        if (rowLimitReached)
            payload["row_limit_reached"] = true;
        if (byteLimitReached)
            payload["byte_limit_reached"] = true;

        var omittedBy = new JsonArray();
        if (options.SummaryOnly && totalCount > 0)
            omittedBy.Add("summary_only");
        if (rowLimitReached)
            omittedBy.Add("limit");
        if (byteLimitReached)
            omittedBy.Add("max_json_bytes");
        if (omittedBy.Count > 0)
            payload["omitted_by"] = omittedBy;

        if (!options.SummaryOnly)
            payload[resultsKey] = BuildDiscoveryRows(results, emittedRows, rowFactory);
        if (exactSignal.HasValue)
            AddExactJsonFields(payload, exactSignal.Value);
        payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
        AddFreshnessHint(payload, reader);
        return payload;
    }

    private static JsonArray BuildDiscoveryRows<T>(IReadOnlyList<T> results, int emittedRows, Func<T, JsonNode?> rowFactory)
    {
        var rows = new JsonArray();
        for (var i = 0; i < emittedRows && i < results.Count; i++)
            rows.Add(rowFactory(results[i]));
        return rows;
    }

    private static bool JsonFitsByteLimit(string json, int maxJsonBytes)
        => Encoding.UTF8.GetByteCount(json) + Environment.NewLine.Length <= maxJsonBytes;

    private static JsonNode? ToFileDiscoveryJsonNode(FileResult result, JsonSerializerOptions jsonOptions, bool compact)
    {
        if (!compact)
            return JsonSerializer.SerializeToNode(result, CliJsonSerializerContextFactory.Create(jsonOptions).FileResult);

        var row = new JsonObject
        {
            ["path"] = result.Path,
            ["lines"] = result.Lines,
            ["size"] = result.Size,
            ["symbol_count"] = result.SymbolCount,
            ["reference_count"] = result.ReferenceCount,
        };
        if (result.Lang != null)
            row["lang"] = result.Lang;
        return row;
    }

    private static JsonNode? ToSymbolDiscoveryJsonNode(SymbolResult result, JsonSerializerOptions jsonOptions, bool compact)
    {
        if (!compact)
            return JsonSerializer.SerializeToNode(result, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolResult);

        var row = new JsonObject
        {
            ["path"] = result.Path,
            ["line"] = result.Line,
            ["start_line"] = result.StartLine,
            ["end_line"] = result.EndLine,
            ["kind"] = result.Kind,
            ["name"] = result.Name,
        };
        if (result.Lang != null)
            row["lang"] = result.Lang;
        if (result.ContainerName != null)
            row["container_name"] = result.ContainerName;
        if (result.Visibility != null)
            row["visibility"] = result.Visibility;
        if (result.SortMode != null)
            row["sort_mode"] = result.SortMode;
        if (result.ReferenceCount.HasValue)
            row["reference_count"] = result.ReferenceCount.Value;
        if (result.HotspotScore.HasValue)
            row["hotspot_score"] = result.HotspotScore.Value;
        if (result.RankingReferenceScore.HasValue)
            row["ranking_reference_score"] = result.RankingReferenceScore.Value;
        if (result.RankingHotspotScore.HasValue)
            row["ranking_hotspot_score"] = result.RankingHotspotScore.Value;
        if (result.GenericNamePenalty.HasValue)
            row["generic_name_penalty"] = result.GenericNamePenalty.Value;
        if (result.StructuralRankPenalty.HasValue)
            row["structural_rank_penalty"] = result.StructuralRankPenalty.Value;
        if (result.DefinitionSites.HasValue)
            row["definition_sites"] = result.DefinitionSites.Value;
        if (result.SizeLines.HasValue)
            row["size_lines"] = result.SizeLines.Value;
        if (result.ComplexityScore.HasValue)
            row["complexity_score"] = result.ComplexityScore.Value;
        return row;
    }
}
