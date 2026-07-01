using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
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

    internal static readonly string[] OrderedUnusedBuckets =
    [
        "likely_unused_private",
        "maybe_unused_nonpublic",
        "public_or_exported_no_refs",
        "reflection_or_config_suspect",
    ];

    private static readonly string[] UnusedSourceAuditExcludePaths =
    [
        "*.md",
        "docs/**",
        "doc/**",
        "CHANGELOG.md",
        "changelog.d/**",
        "README.md",
        "USER_GUIDE.md",
        "DEVELOPER_GUIDE.md",
        "TESTING_GUIDE.md",
        "AGENT_GUIDE.md",
        ".agent_harness/**",
        ".claude/**",
        ".codex/**",
        ".github/**",
    ];

    private static readonly string[] UnusedSourceAuditVisibilityFilters =
    [
        "private",
        "internal",
    ];

    private static readonly HashSet<string> DefaultSuppressedUnusedContractDomains = new(StringComparer.Ordinal)
    {
        "public_api_surface",
        "cli_contract",
        "json_contract",
        "mcp_contract",
        "lsp_contract",
        "configuration_contract",
        "serialization_or_reflection_contract",
        "generated_code",
        "documentation_surface",
        "test_contract",
        "framework_override",
        "exception_diagnostic",
    };

    private sealed record UnusedAuditScopeFilters(
        IReadOnlyList<string> PathPatterns,
        IReadOnlyList<string> ExcludePaths,
        bool ExcludeTests,
        IReadOnlyList<string> VisibilityFilters,
        IReadOnlyList<string> ExcludeVisibilityFilters,
        bool AppliedSourceDefaults);

    internal sealed record UnusedDefaultSuppressionResult(
        List<UnusedSymbolResult> VisibleResults,
        List<UnusedSymbolResult> SuppressedResults,
        bool Applied,
        UnusedCountResult? SuppressedCountResult = null);

    internal sealed record UnusedDefaultCountSuppressionResult(
        UnusedCountResult VisibleResult,
        UnusedCountResult SuppressedResult,
        bool Applied);

    private static UnusedAuditScopeFilters BuildUnusedAuditScopeFilters(QueryCommandOptions options)
    {
        var shouldApplySourceDefaults =
            string.Equals(options.AuditScope, SearchAuditRecipes.DefaultAuditScope, StringComparison.OrdinalIgnoreCase) &&
            (options.AuditScopeExplicit || (options.ExcludeTests && options.PathPatterns.Count == 0));
        if (!shouldApplySourceDefaults)
        {
            return new(
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                options.VisibilityFilters,
                options.ExcludeVisibilityFilters,
                AppliedSourceDefaults: false);
        }

        var pathPatterns = new List<string>(options.PathPatterns);
        if (pathPatterns.Count == 0)
            AddDistinct(pathPatterns, SearchAuditRecipes.DefaultSourcePathPatterns);
        var excludePaths = new List<string>(options.ExcludePaths);
        AddDistinct(excludePaths, UnusedSourceAuditExcludePaths);
        var visibilityFilters = options.VisibilityFilters.Count > 0
            ? options.VisibilityFilters
            : [.. UnusedSourceAuditVisibilityFilters];
        return new(
            pathPatterns,
            excludePaths,
            ExcludeTests: true,
            visibilityFilters,
            options.ExcludeVisibilityFilters,
            AppliedSourceDefaults: true);
    }

    private static bool ShouldApplyUnusedDefaultSuppressions(QueryCommandOptions options, UnusedAuditScopeFilters unusedScope)
    {
        if (options.All
            || options.UnusedActionable
            || options.Kind != null
            || options.UnusedBucket != null
            || options.MinUnusedConfidence != null
            || options.VisibilityFilters.Count > 0
            || options.ExcludeVisibilityFilters.Count > 0)
        {
            return false;
        }

        return unusedScope.AppliedSourceDefaults || !options.AuditScopeExplicit || string.Equals(options.AuditScope, SearchAuditRecipes.AllAuditScope, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetUnusedDefaultSuppressionFetchLimit(int pageLimit, int pageOffset)
    {
        var visibleTarget = (long)Math.Max(pageLimit, 1) + Math.Max(pageOffset, 0) + 1;
        var overfetch = visibleTarget * UnusedDefaultSuppressionOverfetchMultiplier;
        return (int)Math.Min(MaxUnusedPaginationFetchLimit, Math.Max(visibleTarget, overfetch));
    }

    private static UnusedDefaultSuppressionResult ApplyUnusedDefaultSuppressions(List<UnusedSymbolResult> results)
    {
        var visible = new List<UnusedSymbolResult>(results.Count);
        var suppressed = new List<UnusedSymbolResult>();
        foreach (var result in results)
        {
            if (IsDefaultSuppressedUnusedResult(result))
                suppressed.Add(result);
            else
                visible.Add(result);
        }

        return new UnusedDefaultSuppressionResult(visible, suppressed, Applied: true);
    }

    private static UnusedDefaultSuppressionResult BuildUnusedDefaultSuppressionResult(List<UnusedSymbolResult> results, bool applyDefaultSuppressions)
        => applyDefaultSuppressions
            ? ApplyUnusedDefaultSuppressions(results)
            : new UnusedDefaultSuppressionResult(results, [], Applied: false);

    private static bool IsDefaultSuppressedUnusedResult(UnusedSymbolResult result)
        => string.Equals(result.UnusedConfidence, "low", StringComparison.Ordinal)
           && result.UnusedContractDomain != null
           && DefaultSuppressedUnusedContractDomains.Contains(result.UnusedContractDomain);

    internal static Dictionary<string, int> BuildUnusedBucketCounts(IEnumerable<UnusedSymbolResult> results)
    {
        var grouped = results
            .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var ordered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bucket in OrderedUnusedBuckets)
        {
            if (grouped.TryGetValue(bucket, out var count))
                ordered[bucket] = count;
        }
        return ordered;
    }

    internal static Dictionary<string, int> BuildUnusedConfidenceCounts(IEnumerable<UnusedSymbolResult> results)
        => results
            .GroupBy(result => result.UnusedConfidence, StringComparer.Ordinal)
            .OrderBy(group => GetUnusedConfidenceOrder(group.Key))
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    internal static Dictionary<string, int> BuildUnusedContractDomainCounts(IEnumerable<UnusedSymbolResult> results)
    {
        var grouped = results
            .Where(result => !string.IsNullOrWhiteSpace(result.UnusedContractDomain))
            .GroupBy(result => result.UnusedContractDomain!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var ordered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var domain in DbReader.OrderedUnusedContractDomains)
        {
            if (grouped.TryGetValue(domain, out var count))
                ordered[domain] = count;
        }

        foreach (var pair in grouped.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!ordered.ContainsKey(pair.Key))
                ordered[pair.Key] = pair.Value;
        }

        return ordered;
    }

    internal static JsonObject BuildUnusedSummaryJson(IEnumerable<UnusedSymbolResult> results, JsonSerializerOptions jsonOptions, UnusedDefaultSuppressionResult? suppression = null)
    {
        var resultList = results as List<UnusedSymbolResult> ?? results.ToList();
        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        var summary = new JsonObject
        {
            ["by_bucket"] = JsonSerializer.SerializeToNode(BuildUnusedBucketCounts(resultList), context.DictionaryStringInt32),
            ["by_confidence"] = JsonSerializer.SerializeToNode(BuildUnusedConfidenceCounts(resultList), context.DictionaryStringInt32),
            ["by_contract_domain"] = JsonSerializer.SerializeToNode(BuildUnusedContractDomainCounts(resultList), context.DictionaryStringInt32),
        };
        if (suppression is { Applied: true })
            summary["suppressed"] = BuildUnusedDefaultSuppressionJson(suppression, jsonOptions);
        return summary;
    }

    internal static JsonObject BuildUnusedCountSummaryJson(UnusedCountResult result, JsonSerializerOptions jsonOptions, UnusedDefaultCountSuppressionResult? suppression = null)
    {
        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        var summary = new JsonObject
        {
            ["by_bucket"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(result.BucketCounts), context.DictionaryStringInt32),
            ["by_confidence"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(result.ConfidenceCounts), context.DictionaryStringInt32),
            ["by_contract_domain"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(result.ContractDomainCounts), context.DictionaryStringInt32),
        };
        if (suppression is { Applied: true })
            summary["suppressed"] = BuildUnusedDefaultCountSuppressionJson(suppression, jsonOptions);
        return summary;
    }

    private static int GetUnusedSuppressedCount(UnusedDefaultSuppressionResult suppression)
        => suppression.SuppressedCountResult?.Count ?? suppression.SuppressedResults.Count;

    private static Dictionary<string, int> BuildUnusedSuppressedBucketCounts(UnusedDefaultSuppressionResult suppression)
        => suppression.SuppressedCountResult.HasValue
            ? ToUnusedCountDictionary(suppression.SuppressedCountResult.Value.BucketCounts)
            : BuildUnusedBucketCounts(suppression.SuppressedResults);

    private static Dictionary<string, int> BuildUnusedSuppressedConfidenceCounts(UnusedDefaultSuppressionResult suppression)
        => suppression.SuppressedCountResult.HasValue
            ? ToUnusedCountDictionary(suppression.SuppressedCountResult.Value.ConfidenceCounts)
            : BuildUnusedConfidenceCounts(suppression.SuppressedResults);

    private static Dictionary<string, int> BuildUnusedSuppressedContractDomainCounts(UnusedDefaultSuppressionResult suppression)
        => suppression.SuppressedCountResult.HasValue
            ? ToUnusedCountDictionary(suppression.SuppressedCountResult.Value.ContractDomainCounts)
            : BuildUnusedContractDomainCounts(suppression.SuppressedResults);

    private static JsonObject BuildUnusedDefaultSuppressionJson(UnusedDefaultSuppressionResult suppression, JsonSerializerOptions jsonOptions)
    {
        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        return new JsonObject
        {
            ["applied"] = suppression.Applied,
            ["suppressed_count"] = GetUnusedSuppressedCount(suppression),
            ["suppressed_bucket_counts"] = JsonSerializer.SerializeToNode(BuildUnusedSuppressedBucketCounts(suppression), context.DictionaryStringInt32),
            ["suppressed_confidence_counts"] = JsonSerializer.SerializeToNode(BuildUnusedSuppressedConfidenceCounts(suppression), context.DictionaryStringInt32),
            ["suppressed_contract_domain_counts"] = JsonSerializer.SerializeToNode(BuildUnusedSuppressedContractDomainCounts(suppression), context.DictionaryStringInt32),
            ["include_suppressed_hint"] = "Pass --all to include low-confidence contract-domain candidates.",
        };
    }

    private static JsonObject BuildUnusedDefaultCountSuppressionJson(UnusedDefaultCountSuppressionResult suppression, JsonSerializerOptions jsonOptions)
    {
        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        return new JsonObject
        {
            ["applied"] = suppression.Applied,
            ["suppressed_count"] = suppression.SuppressedResult.Count,
            ["suppressed_bucket_counts"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(suppression.SuppressedResult.BucketCounts), context.DictionaryStringInt32),
            ["suppressed_confidence_counts"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(suppression.SuppressedResult.ConfidenceCounts), context.DictionaryStringInt32),
            ["suppressed_contract_domain_counts"] = JsonSerializer.SerializeToNode(ToUnusedCountDictionary(suppression.SuppressedResult.ContractDomainCounts), context.DictionaryStringInt32),
            ["include_suppressed_hint"] = "Pass --all to include low-confidence contract-domain candidates.",
        };
    }

    private static void WriteUnusedDefaultSuppressionSummary(UnusedDefaultSuppressionResult suppression)
    {
        var suppressedCount = GetUnusedSuppressedCount(suppression);
        if (!suppression.Applied || suppressedCount == 0)
            return;

        WriteUnusedDefaultSuppressionSummary(suppressedCount, BuildUnusedSuppressedContractDomainCounts(suppression));
    }

    private static void WriteUnusedDefaultCountSuppressionSummary(UnusedDefaultCountSuppressionResult suppression)
    {
        if (!suppression.Applied || suppression.SuppressedResult.Count == 0)
            return;

        WriteUnusedDefaultSuppressionSummary(
            suppression.SuppressedResult.Count,
            ToUnusedCountDictionary(suppression.SuppressedResult.ContractDomainCounts));
    }

    private static void WriteUnusedDefaultSuppressionSummary(int suppressedCount, IReadOnlyDictionary<string, int> contractDomainCounts)
    {
        var domainCounts = contractDomainCounts.Select(pair => $"{pair.Key}: {pair.Value}");
        CommandErrorWriter.WriteStderr(
            $"({suppressedCount} low-confidence contract-domain candidates suppressed by default; use --all to include them; suppressed domains: {string.Join(", ", domainCounts)})");
    }

    private static Dictionary<string, int> ToUnusedCountDictionary(IReadOnlyDictionary<string, int> counts)
        => counts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static int GetUnusedFetchLimit(int pageLimit, int pageOffset)
    {
        var requested = (long)Math.Max(pageLimit, 1) + Math.Max(pageOffset, 0) + 1;
        return (int)Math.Min(MaxUnusedPaginationFetchLimit, requested);
    }

    internal static int GetUnusedFetchLimitForTests(int pageLimit, int pageOffset) => GetUnusedFetchLimit(pageLimit, pageOffset);

    private static bool IsUnusedCursorOffsetWithinFetchCap(int pageLimit, int pageOffset)
    {
        if (pageOffset < 0)
            return false;

        var requested = (long)Math.Max(pageLimit, 1) + pageOffset + 1;
        return requested <= MaxUnusedPaginationFetchLimit;
    }

    internal static bool IsUnusedCursorOffsetWithinFetchCapForTests(int pageLimit, int pageOffset) =>
        IsUnusedCursorOffsetWithinFetchCap(pageLimit, pageOffset);

    internal static JsonObject BuildUnusedRepresentativeSymbolsJson(IEnumerable<UnusedSymbolResult> results)
    {
        var grouped = results
            .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Take(3).ToList(), StringComparer.Ordinal);
        var representatives = new JsonObject();
        foreach (var bucket in OrderedUnusedBuckets)
        {
            if (!grouped.TryGetValue(bucket, out var bucketResults) || bucketResults.Count == 0)
                continue;

            var samples = new JsonArray();
            foreach (var result in bucketResults)
            {
                samples.Add(new JsonObject
                {
                    ["name"] = result.Name,
                    ["kind"] = result.Kind,
                    ["path"] = result.Path,
                    ["line"] = result.Line,
                    ["confidence"] = result.UnusedConfidence,
                    ["contract_domain"] = result.UnusedContractDomain,
                });
            }

            representatives[bucket] = samples;
        }

        return representatives;
    }

    internal static JsonObject BuildUnusedBucketTaxonomyJson()
    {
        var taxonomy = new JsonObject();
        foreach (var bucket in OrderedUnusedBuckets)
            taxonomy[bucket] = new JsonObject
            {
                ["confidence"] = GetUnusedBucketConfidence(bucket),
                ["description"] = GetUnusedBucketDescription(bucket),
            };
        return taxonomy;
    }

    private static int GetUnusedConfidenceOrder(string confidence) => confidence switch
    {
        "medium" => 0,
        "low" => 1,
        _ => 2,
    };

    private static string GetUnusedBucketConfidence(string bucket) => bucket switch
    {
        "likely_unused_private" => "medium",
        "maybe_unused_nonpublic" => "low",
        "public_or_exported_no_refs" => "low",
        "reflection_or_config_suspect" => "low",
        _ => "unknown",
    };

    private static string GetUnusedBucketDescription(string bucket) => bucket switch
    {
        "likely_unused_private" => "Private symbols with no indexed references; usually the highest-signal unused candidates.",
        "maybe_unused_nonpublic" => "Internal, protected, or otherwise non-public symbols with no indexed references; review call paths and framework entry points before removal.",
        "public_or_exported_no_refs" => "Public or exported symbols with no indexed references; may still be external API surface.",
        "reflection_or_config_suspect" => "Symbols with no indexed references that look reachable through reflection, serialization, contracts, config, metadata, generated code, documentation headings, test hooks, or binding conventions.",
        _ => "Unknown unused-symbol bucket.",
    };

    private static string BuildUnusedJsonPayload(IEnumerable<UnusedSymbolResult> results, bool? graphSupported, string? graphSupportReason, SqlGraphContractSignal sqlGraphSignal, bool hasReferencesTable, JsonSerializerOptions jsonOptions, QueryCommandOptions? queryOptions = null, UnusedAuditScopeFilters? unusedScope = null, bool byBucket = false, string? nextCursor = null, UnusedDefaultSuppressionResult? suppression = null)
    {
        var resultList = results as List<UnusedSymbolResult> ?? results.ToList();
        var payload = new JsonObject
        {
            ["count"] = resultList.Count,
            ["graph_supported"] = graphSupported,
            ["graph_support_reason"] = graphSupportReason,
            ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(BuildUnusedBucketCounts(resultList), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
            ["returned_contract_domain_counts"] = JsonSerializer.SerializeToNode(BuildUnusedContractDomainCounts(resultList), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
            ["summary"] = BuildUnusedSummaryJson(resultList, jsonOptions, suppression),
            ["bucket_taxonomy"] = BuildUnusedBucketTaxonomyJson(),
        };
        if (suppression is { Applied: true })
            payload["default_suppression"] = BuildUnusedDefaultSuppressionJson(suppression, jsonOptions);
        if (nextCursor != null)
            payload["next_cursor"] = nextCursor;
        if (queryOptions?.Compact == true)
        {
            payload["compact"] = true;
            payload["representative_symbols"] = BuildUnusedRepresentativeSymbolsJson(resultList);
            var omittedSections = new JsonArray(JsonValue.Create("symbols"));
            if (byBucket)
            {
                payload["by_bucket"] = BuildUnusedBucketSummariesJson(resultList);
                omittedSections.Add("by_bucket.symbols");
            }
            payload["omitted_sections"] = omittedSections;
        }
        else
        {
            payload["symbols"] = JsonSerializer.SerializeToNode(resultList, CliJsonSerializerContextFactory.Create(jsonOptions).ListUnusedSymbolResult);
            if (byBucket)
                payload["by_bucket"] = BuildUnusedResultsByBucketJson(resultList, jsonOptions);
        }

        if (!hasReferencesTable)
        {
            payload["graph_table_available"] = false;
            payload["degraded"] = true;
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        }

        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
        if (queryOptions != null)
        {
            var queryContext = unusedScope != null
                ? BuildUnusedQueryContextJson(queryOptions, unusedScope, jsonOptions)
                : BuildQueryContextJson(queryOptions, jsonOptions);
            if (suppression is { Applied: true })
                queryContext["default_suppression"] = true;
            if (queryOptions.All)
                queryContext["all"] = true;
            payload["query_context"] = queryContext;
        }
        return payload.ToJsonString(GetJsonNodeSerializationOptions(jsonOptions));
    }

    internal static JsonObject BuildUnusedBucketSummariesJson(IEnumerable<UnusedSymbolResult> results)
    {
        var grouped = results
            .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var summaries = new JsonObject();
        foreach (var bucket in OrderedUnusedBuckets)
        {
            grouped.TryGetValue(bucket, out var bucketResults);
            bucketResults ??= [];
            var summary = new JsonObject
            {
                ["count"] = bucketResults.Count,
                ["confidence"] = GetUnusedBucketConfidence(bucket),
                ["description"] = GetUnusedBucketDescription(bucket),
            };
            var representative = bucketResults.FirstOrDefault();
            if (representative != null)
            {
                summary["representative"] = new JsonObject
                {
                    ["name"] = representative.Name,
                    ["kind"] = representative.Kind,
                    ["path"] = representative.Path,
                    ["line"] = representative.Line,
                    ["confidence"] = representative.UnusedConfidence,
                };
            }
            summaries[bucket] = summary;
        }

        return summaries;
    }

    private static JsonSerializerOptions GetJsonNodeSerializationOptions(JsonSerializerOptions jsonOptions)
    {
        if (jsonOptions.TypeInfoResolver != null)
            return jsonOptions;

        return new JsonSerializerOptions(jsonOptions)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
    }

    private static JsonObject BuildUnusedQueryContextJson(QueryCommandOptions options, UnusedAuditScopeFilters unusedScope, JsonSerializerOptions jsonOptions)
    {
        var query = BuildQueryContextJson(options, jsonOptions);
        if (!unusedScope.AppliedSourceDefaults)
            return query;

        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        if (!options.ExcludeTests && unusedScope.ExcludeTests)
            query["effective_exclude_tests"] = true;
        if (!options.PathPatterns.SequenceEqual(unusedScope.PathPatterns, StringComparer.Ordinal))
            query["effective_path"] = JsonSerializer.SerializeToNode(unusedScope.PathPatterns.ToList(), context.ListString);
        if (!options.ExcludePaths.SequenceEqual(unusedScope.ExcludePaths, StringComparer.Ordinal))
            query["effective_exclude_path"] = JsonSerializer.SerializeToNode(unusedScope.ExcludePaths.ToList(), context.ListString);
        if (options.VisibilityFilters.Count == 0 && unusedScope.VisibilityFilters.Count > 0)
            query["effective_visibility"] = JsonSerializer.SerializeToNode(unusedScope.VisibilityFilters.ToList(), context.ListString);
        return query;
    }

    private static JsonObject BuildUnusedResultsByBucketJson(IEnumerable<UnusedSymbolResult> results, JsonSerializerOptions jsonOptions)
    {
        var grouped = results
            .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var byBucket = new JsonObject();
        foreach (var bucket in OrderedUnusedBuckets)
        {
            if (grouped.TryGetValue(bucket, out var bucketResults))
                byBucket[bucket] = JsonSerializer.SerializeToNode(bucketResults, CliJsonSerializerContextFactory.Create(jsonOptions).ListUnusedSymbolResult);
            else
                byBucket[bucket] = new JsonArray();
        }
        return byBucket;
    }

    private static string GetUnusedBucketHeading(string bucket) => bucket switch
    {
        "likely_unused_private" => "Likely unused private",
        "maybe_unused_nonpublic" => "Maybe unused non-public",
        "public_or_exported_no_refs" => "Public/exported with no refs",
        "reflection_or_config_suspect" => "Intentional-surface suspects",
        _ => bucket,
    };
}
