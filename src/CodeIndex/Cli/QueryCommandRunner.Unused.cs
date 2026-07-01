using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunUnused(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var byBucket = cmdArgs.Any(arg => arg == "--by-bucket");
        var previewOptionError = ValidatePreviewOptions("unused", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("unused", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("unused")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "unused"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "unused", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteInvalidUnusedFilterError(options))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteUnexpectedPositionals("unused", options))
            return CommandExitCodes.UsageError;
        if (options.SearchCursor.HasValue)
        {
            WriteUsageError(
                "--cursor for unused must use the `unused:<offset>` cursor returned by a previous unused response.",
                GetUsageLineOrThrow("unused"),
                "Use the `next_cursor` value from `cdidx unused --json`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutlineCursorOffset.HasValue)
        {
            WriteUsageError(
                "--cursor for unused must use the `unused:<offset>` cursor returned by a previous unused response.",
                GetUsageLineOrThrow("unused"),
                "`outline:<offset>` cursors are for `cdidx outline <path>`.");
            return CommandExitCodes.UsageError;
        }
        if (options.UnusedCursorOffset.HasValue && options.CountOnly)
        {
            WriteUsageError(
                "--cursor cannot be used with `unused --count`.",
                GetUsageLineOrThrow("unused"),
                "Remove `--count` to page unused results.");
            return CommandExitCodes.UsageError;
        }
        var unusedScope = BuildUnusedAuditScopeFilters(options);

        return WithDb(options, jsonOptions, reader =>
        {
            // Warn if user specified an unsupported language / 未対応言語の場合は警告
            if (options.Lang != null && !ReferenceExtractor.SupportsLanguage(options.Lang) && !options.Json)
                CommandErrorWriter.WriteStderr($"Warning: '{options.Lang}' does not support reference extraction. Unused results are unavailable for this language.");

            bool? graphSupported = options.Lang != null ? ReferenceExtractor.SupportsLanguage(options.Lang) : null;
            var graphSupportReason = ReferenceExtractor.BuildGraphSupportReason(options.Lang, graphSupported);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, unusedScope.PathPatterns, unusedScope.ExcludePaths, unusedScope.ExcludeTests);
            var zeroResultSqlGraphSignal = NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, unusedScope.PathPatterns, unusedScope.ExcludePaths, unusedScope.ExcludeTests));
            var applyDefaultSuppressions = ShouldApplyUnusedDefaultSuppressions(options, unusedScope);

            UnusedCountResult CountUnusedSymbolsDetailedForCurrentQuery(Func<UnusedSymbolResult, bool>? resultFilter = null)
            {
                if (resultFilter == null)
                {
                    return reader.CountUnusedSymbolsDetailed(
                        options.Kind,
                        options.Lang,
                        unusedScope.PathPatterns,
                        unusedScope.ExcludePaths,
                        unusedScope.ExcludeTests,
                        visibilityFilters: unusedScope.VisibilityFilters,
                        excludeVisibilityFilters: unusedScope.ExcludeVisibilityFilters,
                        bucketFilter: options.UnusedBucket,
                        minConfidence: options.MinUnusedConfidence);
                }

                return reader.CountUnusedSymbolsDetailedFiltered(
                    options.Kind,
                    options.Lang,
                    unusedScope.PathPatterns,
                    unusedScope.ExcludePaths,
                    unusedScope.ExcludeTests,
                    visibilityFilters: unusedScope.VisibilityFilters,
                    excludeVisibilityFilters: unusedScope.ExcludeVisibilityFilters,
                    bucketFilter: options.UnusedBucket,
                    minConfidence: options.MinUnusedConfidence,
                    resultFilter: resultFilter);
            }

            if (options.CountOnly)
            {
                var countSummary = CountUnusedSymbolsDetailedForCurrentQuery();
                UnusedDefaultCountSuppressionResult? countSuppression = null;
                if (applyDefaultSuppressions)
                {
                    var suppressedCountSummary = CountUnusedSymbolsDetailedForCurrentQuery(IsDefaultSuppressedUnusedResult);
                    if (suppressedCountSummary.Count > 0)
                        countSummary = CountUnusedSymbolsDetailedForCurrentQuery(result => !IsDefaultSuppressedUnusedResult(result));
                    countSuppression = new UnusedDefaultCountSuppressionResult(countSummary, suppressedCountSummary, Applied: true);
                }

                if (options.Json)
                {
                    var effectiveSqlGraphSignal = countSummary.Count == 0
                        ? zeroResultSqlGraphSignal
                        : NarrowSqlGraphContractSignal(
                            baseSqlGraphSignal,
                            countSummary.IncludesSql || DbReader.IsSqlLanguage(options.Lang));
                    var payload = new JsonObject
                    {
                        ["count"] = countSummary.Count,
                        ["files"] = countSummary.FileCount,
                        ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(countSummary.BucketCounts), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
                        ["returned_contract_domain_counts"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(countSummary.ContractDomainCounts), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
                        ["summary"] = BuildUnusedCountSummaryJson(countSummary, jsonOptions, countSuppression),
                        ["bucket_taxonomy"] = BuildUnusedBucketTaxonomyJson(),
                        ["graph_supported"] = graphSupported,
                        ["graph_support_reason"] = graphSupportReason,
                        ["graph_table_available"] = reader._hasReferencesTable,
                        ["degraded"] = !reader._hasReferencesTable
                    };
                    if (countSuppression is { Applied: true })
                        payload["default_suppression"] = BuildUnusedDefaultCountSuppressionJson(countSuppression, jsonOptions);
                    AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                    var queryContext = BuildUnusedQueryContextJson(options, unusedScope, jsonOptions);
                    if (countSuppression is { Applied: true })
                        queryContext["default_suppression"] = true;
                    if (options.All)
                        queryContext["all"] = true;
                    payload["query_context"] = queryContext;
                    if (options.Compact)
                    {
                        payload["compact"] = true;
                        payload["omitted_sections"] = new JsonArray();
                    }
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    var effectiveSqlGraphSignal = countSummary.Count == 0
                        ? zeroResultSqlGraphSignal
                        : NarrowSqlGraphContractSignal(
                            baseSqlGraphSignal,
                            countSummary.IncludesSql || DbReader.IsSqlLanguage(options.Lang));
                    Console.WriteLine($"{countSummary.Count}");
                    if (countSuppression is { Applied: true })
                        WriteUnusedDefaultCountSuppressionSummary(countSuppression);
                    WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "unused", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return CommandExitCodes.Success;
            }

            var pageOffset = options.UnusedCursorOffset ?? 0;
            if (!IsUnusedCursorOffsetWithinFetchCap(options.Limit, pageOffset))
            {
                WriteUsageError(
                    $"unused --cursor offset must be less than or equal to {MaxUnusedPaginationOffset.ToString(CultureInfo.InvariantCulture)}, got {pageOffset.ToString(CultureInfo.InvariantCulture)}.",
                    GetUsageLineOrThrow("unused"),
                    "Restart unused pagination without --cursor, or narrow the query filters before paginating again.");
                return CommandExitCodes.UsageError;
            }

            var fetchLimit = applyDefaultSuppressions
                ? GetUnusedDefaultSuppressionFetchLimit(options.Limit, pageOffset)
                : GetUnusedFetchLimit(options.Limit, pageOffset);
            var fetchedResults = FetchUnusedResults(fetchLimit);
            var suppression = BuildUnusedDefaultSuppressionResult(fetchedResults, applyDefaultSuppressions);
            while (suppression.Applied
                && suppression.VisibleResults.Count <= pageOffset + options.Limit
                && fetchedResults.Count >= fetchLimit
                && fetchLimit < MaxUnusedPaginationFetchLimit)
            {
                var nextFetchLimit = Math.Min(MaxUnusedPaginationFetchLimit, fetchLimit * 2);
                if (nextFetchLimit == fetchLimit)
                    break;

                fetchLimit = nextFetchLimit;
                fetchedResults = FetchUnusedResults(fetchLimit);
                suppression = BuildUnusedDefaultSuppressionResult(fetchedResults, applyDefaultSuppressions);
            }
            if (suppression.Applied)
                suppression = suppression with { SuppressedCountResult = CountUnusedSymbolsDetailedForCurrentQuery(IsDefaultSuppressedUnusedResult) };

            var pageableResults = suppression.VisibleResults;
            var results = pageableResults
                .Skip(pageOffset)
                .Take(options.Limit)
                .ToList();
            var nextOffset = pageOffset + options.Limit;
            var nextCursor = pageableResults.Count > nextOffset
                && IsUnusedCursorOffsetWithinFetchCap(options.Limit, nextOffset)
                ? FormatUnusedCursor(nextOffset)
                : null;
            var sqlGraphSignal = results.Count == 0
                ? zeroResultSqlGraphSignal
                : NarrowSqlGraphContractSignalByLanguages(
                    baseSqlGraphSignal,
                    results.Select(result => result.Lang),
                    options.Lang);
            if (results.Count == 0)
            {
                if (options.Json)
                {
                    Console.WriteLine(BuildUnusedJsonPayload(
                        Array.Empty<UnusedSymbolResult>(),
                        graphSupported,
                        graphSupportReason,
                        sqlGraphSignal,
                        reader._hasReferencesTable,
                        jsonOptions,
                        options,
                        unusedScope,
                        nextCursor: nextCursor,
                        suppression: suppression));
                }
                else if (suppression.Applied && GetUnusedSuppressedCount(suppression) > 0)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No unsuppressed unused symbols found", options));
                    WriteUnusedDefaultSuppressionSummary(suppression);
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "symbols", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                else
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No unused symbols found", options));
                    WriteZeroResultHints(options, reader);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "symbols", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return UnusedZeroResultExitCode(options, suppression);
            }

            if (options.Json)
            {
                Console.WriteLine(BuildUnusedJsonPayload(results, graphSupported, graphSupportReason, sqlGraphSignal, reader._hasReferencesTable, jsonOptions, options, unusedScope, byBucket: byBucket, nextCursor: nextCursor, suppression: suppression));
            }
            else
            {
                var bucketCounts = BuildUnusedBucketCounts(results);
                foreach (var bucket in OrderedUnusedBuckets)
                {
                    var bucketResults = results.Where(s => s.UnusedBucket == bucket).ToList();
                    if (bucketResults.Count == 0)
                        continue;

                    Console.WriteLine($"{GetUnusedBucketHeading(bucket)} ({bucketResults.Count})");
                    foreach (var s in bucketResults)
                    {
                        var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                        var container = s.ContainerName != null ? $" in {s.ContainerName}" : "";
                        Console.WriteLine($"{ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}{container}");
                        var domain = s.UnusedContractDomain != null ? $" domain={s.UnusedContractDomain}" : "";
                        Console.WriteLine($"             confidence={s.UnusedConfidence}{domain} reason={s.UnusedReason}");
                    }
                    Console.WriteLine();
                }
                var summaryBuckets = OrderedUnusedBuckets
                    .Where(bucketCounts.ContainsKey)
                    .Select(bucket => $"{GetUnusedBucketHeading(bucket)}: {bucketCounts[bucket]}");
                CommandErrorWriter.WriteStderr($"({results.Count} returned potentially unused symbols; returned buckets: {string.Join(", ", summaryBuckets)})");
                if (nextCursor != null)
                    CommandErrorWriter.WriteStderr($"next_cursor={nextCursor}");
                WriteUnusedDefaultSuppressionSummary(suppression);
                WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
            }
            return CommandExitCodes.Success;

            List<UnusedSymbolResult> FetchUnusedResults(int limit)
                => reader.GetUnusedSymbols(
                    limit,
                    options.Kind,
                    options.Lang,
                    unusedScope.PathPatterns,
                    unusedScope.ExcludePaths,
                    unusedScope.ExcludeTests,
                    visibilityFilters: unusedScope.VisibilityFilters,
                    excludeVisibilityFilters: unusedScope.ExcludeVisibilityFilters,
                    bucketFilter: options.UnusedBucket,
                    minConfidence: options.MinUnusedConfidence);
        });
    }
}
