using System.Globalization;
using System.Text.Json;
using CodeIndex.Database;

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
}
