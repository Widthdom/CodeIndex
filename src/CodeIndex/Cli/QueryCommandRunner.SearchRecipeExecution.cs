using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static string BuildSearchRecipeCursorBinding(DbReader reader, SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options, SearchAuditRecipeQuery query)
        => AuditBaselineStore.Hash("recipe-cursor-v2-token-boundary", reader.GetPaginationGeneration().Identity,
            BuildSearchDefinitionVersion("audit-recipe-query-v2-token-boundary", query, CliJsonSerializerContext.Default.SearchAuditRecipeQuery),
            BuildAuditAllRecoveryCommand(query.Name, options, includeDb: false),
            scope.Name, string.Join('\0', scope.PathPatterns), string.Join('\0', scope.ExcludePaths), scope.ExcludeTests.ToString());

    private static string FormatSearchRecipeCursor(SearchResult result, DbReader reader,
        SearchRecipeScopeJsonResult scope, QueryCommandOptions options, SearchAuditRecipeQuery query)
        => "recipe:v2:" + BuildSearchRecipeCursorBinding(reader, scope, options, query) + ":" + FormatSearchCursor(result);

    private static (bool Exact, bool TokenBoundary) ResolveSearchRecipeMatchMode(
        SearchAuditRecipeQuery query, QueryCommandOptions options)
        => query.ResolveMatchMode(
            options.Exact || options.ExactSubstring ? true : null,
            options.TokenBoundary ? true : null);

    private readonly record struct SearchRecipeQueryMaterializationRequest(
        DbReader Reader,
        SearchRecipeScopeJsonResult Scope,
        QueryCommandOptions Options,
        SearchAuditRecipeQuery RecipeQuery,
        bool Exact,
        int? ResultLimit,
        bool? RawFtsOverride,
        int? FetchLimitCap = null);

    private readonly record struct SearchRecipeQueryMaterializationResult(
        List<SearchDisplayRow> Rows,
        bool SourceTotalAuthoritative,
        bool CandidateWindowExhausted,
        string? CoverageRestriction);

    private static SearchRecipeQueryMaterializationResult MaterializeSearchRecipeQuery(
        in SearchRecipeQueryMaterializationRequest request)
    {
        EnsureSearchRecipeCoverage(request.Reader, request.Scope, request.Options);
        var queryScope = BuildSearchRecipeQueryScope(request.Scope, request.RecipeQuery);
        var guardFilters = BuildSearchRecipeGuardFilters(request.Options, request.RecipeQuery);
        var fetchLimit = request.ResultLimit.HasValue
            ? GetSearchRecipeFetchLimit(request.Options, request.ResultLimit.Value, request.RecipeQuery)
            : int.MaxValue;
        if (request.FetchLimitCap.HasValue)
            fetchLimit = Math.Min(fetchLimit, request.FetchLimitCap.Value);
        var candidateWindowExhausted = false;
        var results = request.Reader.SearchWithCandidateEvidence(
            request.RecipeQuery.Query,
            fetchLimit,
            request.Options.Lang,
            false,
            queryScope.PathPatterns,
            queryScope.ExcludePaths,
            queryScope.ExcludeTests,
            !request.Options.NoDedup,
            request.Options.Since,
            request.Exact,
            false,
            !request.Options.NoVisibilityRank,
            cursor: request.Options.SearchCursor,
            guardFilters: guardFilters,
            guardWindow: request.Options.GuardWindow,
            guardScope: request.Options.GuardScope,
            requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(request.Options, request.RecipeQuery),
            tokenBoundary: ResolveSearchRecipeMatchMode(request.RecipeQuery, request.Options).TokenBoundary,
            resultRanking: request.ResultLimit.HasValue
                ? GetSearchRecipeResultRanking(request.RecipeQuery.ResultRanking, request.ResultLimit.Value)
                : SearchResultRanking.Default,
            candidateWindowObserver: exhausted => candidateWindowExhausted = exhausted);
        var coverageRestriction = candidateWindowExhausted ? "raw_candidate_window_exhausted"
            : guardFilters.Count > 0 ? "guard_filter_coverage_unverified"
            : request.RecipeQuery.RejectFileQueries.Count > 0 ? "file_reject_coverage_unverified"
            : request.RecipeQuery.SemanticFilter != SearchRecipeSemanticFilter.None ? "semantic_filter_coverage_unverified"
            : BuildSearchDisplayFacetFilters(request.Options, request.RecipeQuery).ResultKinds.Count > 0 ? "result_kind_filter_coverage_unverified"
            : null;
        results = ApplySearchRecipeFileRejectQueries(
            request.Reader,
            results,
            request.Options,
            request.RecipeQuery);
        var originCoverageComplete = true;
        var rows = BuildSearchDisplayRows(
            results,
            request.Options,
            request.Exact,
            request.RecipeQuery.Query,
            rawFtsOverride: request.RawFtsOverride,
            recipeQuery: request.RecipeQuery,
            originCoverageObserver: complete => originCoverageComplete &= complete);
        if (!originCoverageComplete) coverageRestriction = "origin_classification_incomplete";
        var sourceTotalAuthoritative = request.ResultLimit.HasValue && coverageRestriction == null;
        rows = ApplySearchRecipeSemanticFilter(
            request.Reader,
            request.Options,
            request.RecipeQuery,
            rows);
        ApplyXmlSettingsAuditClassifications(request.Reader, request.RecipeQuery, rows);
        MarkSearchRecipeQueryExecuted(request.Scope, request.RecipeQuery.Name);
        return new SearchRecipeQueryMaterializationResult(rows, sourceTotalAuthoritative, candidateWindowExhausted, coverageRestriction);
    }

    private static List<SearchRecipeQueryResultJsonResult> CollectSearchRecipeQueryResults(
        DbReader reader,
        IReadOnlyList<SearchAuditRecipeQuery> recipeQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        bool userExact,
        SearchQueryFreshnessContext? freshnessContext,
        bool includeAuditClassifications,
        out int total,
        out int minimumMatchedTotal,
        out List<SearchQueryFreshnessObservation> freshnessObservations,
        out bool hasFailures,
        int emittedBefore = 0,
        int? aggregateResultLimit = null,
        int? fetchLimitCap = null,
        IReadOnlyList<SearchAuditRecipeQuery>? auditClassificationQueries = null,
        int? auditRowOffset = null)
    {
        var queryResults = new List<SearchRecipeQueryResultJsonResult>();
        freshnessObservations = [];
        total = 0;
        minimumMatchedTotal = 0;
        hasFailures = false;
        foreach (var recipeQuery in recipeQueries)
        {
            try
            {
                var exact = ResolveSearchRecipeMatchMode(recipeQuery, options).Exact;
                var resultLimit = aggregateResultLimit.HasValue
                    ? Math.Max(0, Math.Min(options.Limit, aggregateResultLimit.Value - emittedBefore - total))
                    : GetSearchRecipeEffectiveResultLimit(options, total);
                var materializationRequest = new SearchRecipeQueryMaterializationRequest(
                    reader,
                    scope,
                    options,
                    recipeQuery,
                    exact,
                    auditRowOffset.HasValue ? AuditAllCandidateRowsPerQuery : resultLimit,
                    RawFtsOverride: false,
                    FetchLimitCap: fetchLimitCap);
                var materialization = MaterializeSearchRecipeQuery(in materializationRequest);
                var rows = materialization.Rows;
                var summaryEvidencePaths = BuildSearchRecipeTopFiles(rows);
                var summaryEvidencePathCount = rows
                    .Select(row => row.Result.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                var outputSelection = ApplySearchOutputSelection(
                    rows,
                    options,
                    auditRowOffset.HasValue ? AuditAllCandidateRowsPerQuery : resultLimit,
                    materialization.SourceTotalAuthoritative);
                if (auditRowOffset.HasValue)
                {
                    // Replay a fixed, bounded selection before slicing. Candidate ranking and
                    // selectors must not change when a caller changes the page budget.
                    var page = outputSelection.Rows.Skip(auditRowOffset.Value).Take(resultLimit).ToList();
                    var remaining = Math.Max(0, outputSelection.Rows.Count - auditRowOffset.Value - page.Count);
                    outputSelection = outputSelection with
                    {
                        Rows = page,
                        Returned = page.Count,
                        LimitOmittedCount = remaining,
                        LimitTruncated = remaining > 0,
                        Truncated = remaining > 0 || outputSelection.SelectorOmittedCount > 0,
                    };
                }
                rows = outputSelection.Rows;
                if (includeAuditClassifications)
                    ApplySearchRecipeAuditClassifications(
                        reader,
                        recipeQuery,
                        auditClassificationQueries ?? recipeQueries,
                        rows);
                var minimumOmitted = auditRowOffset.HasValue
                    ? outputSelection.LimitOmittedCount
                    : Math.Max(0, outputSelection.OriginalCount - rows.Count);
                var selectionReason = GetSearchRecipeSelectionReason(outputSelection);
                total += rows.Count;
                minimumMatchedTotal += outputSelection.OriginalCount;
                queryResults.Add(new SearchRecipeQueryResultJsonResult(
                    recipeQuery.Name,
                    recipeQuery.Query,
                    recipeQuery.Description,
                    recipeQuery.RecommendedLabels,
                    recipeQuery.FalsePositiveGuidance,
                    [.. recipeQuery.RiskEvidence],
                    ToSearchRecipeGuardFilterJsonResults(recipeQuery.GuardFilters),
                    exact,
                    recipeQuery.Severity,
                    [.. recipeQuery.PathPatterns],
                    [.. recipeQuery.ExcludePaths],
                    [.. recipeQuery.MatchOrigins],
                    [.. recipeQuery.ExcludeOrigins],
                    [.. recipeQuery.ResultKinds],
                    [.. recipeQuery.Classifiers],
                    recipeQuery.StringComparisonTaxonomy,
                    recipeQuery.BroadCatchTaxonomy,
                    recipeQuery.NullableContractTaxonomy,
                    BuildSearchRecipeClassifierCounts(rows),
                    rows.Count,
                    rows.Count,
                    outputSelection.OriginalCount,
                    minimumOmitted,
                    selectionReason,
                    selectionReason != null ? outputSelection.SelectionOmittedCount : null,
                    resultLimit,
                    minimumOmitted,
                    BuildSearchRecipeTopFiles(rows),
                    outputSelection.LimitTruncated,
                    outputSelection.LimitTruncated
                        && !options.FirstPerFile
                        && !options.SampleSize.HasValue
                        && rows.Count > 0
                            ? FormatSearchRecipeCursor(rows[^1].Result, reader, scope, options, recipeQuery)
                            : null,
                    rows.Select(row => row.Compact).ToList(),
                    outputSelection.SourceTotal,
                    outputSelection.SourceTotalAuthoritative,
                    outputSelection.SourceTotalAuthoritative ? null : outputSelection.SourceTotal,
                    outputSelection.SelectedTotal,
                    outputSelection.Returned,
                    outputSelection.SelectorOmittedCount,
                    outputSelection.LimitOmittedCount,
                    outputSelection.Selectors)
                {
                    TokenBoundary = ResolveSearchRecipeMatchMode(recipeQuery, options).TokenBoundary,
                    SummaryEvidencePaths = summaryEvidencePaths,
                    SummaryEvidencePathCount = summaryEvidencePathCount,
                    SummaryEvidencePathCountAuthoritative = materialization.SourceTotalAuthoritative,
                    CandidateWindowExhausted = materialization.CandidateWindowExhausted,
                    CoverageRestriction = materialization.CoverageRestriction,
                });
                if (freshnessContext != null)
                {
                    freshnessObservations.Add(SuccessfulSearchQueryObservation(
                        freshnessContext,
                        recipeQuery.Name,
                        outputSelection.OriginalCount));
                }
            }
            catch (Exception ex) when (
                freshnessContext != null
                && TryClassifySearchQueryExecutionFailure(ex, out _))
            {
                TryClassifySearchQueryExecutionFailure(ex, out var failureReason);
                hasFailures = true;
                freshnessObservations.Add(FailedSearchQueryObservation(
                    freshnessContext,
                    recipeQuery.Name,
                    failureReason));
            }
        }

        return queryResults;
    }

    private static List<SearchRecipeCompactQueryResultJsonResult> CollectSearchRecipeCompactQueryResults(
        DbReader reader,
        IReadOnlyList<SearchAuditRecipeQuery> recipeQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        bool userExact,
        SearchQueryFreshnessContext freshnessContext,
        out int total,
        out List<SearchQueryFreshnessObservation> freshnessObservations,
        out bool hasFailures)
    {
        var queryResults = new List<SearchRecipeCompactQueryResultJsonResult>();
        freshnessObservations = [];
        total = 0;
        hasFailures = false;
        foreach (var recipeQuery in recipeQueries)
        {
            try
            {
                var exact = ResolveSearchRecipeMatchMode(recipeQuery, options).Exact;
                var resultLimit = GetSearchRecipeEffectiveResultLimit(options, total);
                var materializationRequest = new SearchRecipeQueryMaterializationRequest(
                    reader,
                    scope,
                    options,
                    recipeQuery,
                    exact,
                    resultLimit,
                    RawFtsOverride: null);
                var materialization = MaterializeSearchRecipeQuery(in materializationRequest);
                var rows = materialization.Rows;
                var outputSelection = ApplySearchOutputSelection(
                    rows,
                    options,
                    resultLimit,
                    materialization.SourceTotalAuthoritative);
                rows = outputSelection.Rows;
                if (!options.SummaryOnly)
                    ApplySearchRecipeAuditClassifications(reader, recipeQuery, recipeQueries, rows);
                var minimumOmitted = Math.Max(0, outputSelection.OriginalCount - rows.Count);
                var selectionReason = GetSearchRecipeSelectionReason(outputSelection);
                total += rows.Count;
                queryResults.Add(new SearchRecipeCompactQueryResultJsonResult(
                    recipeQuery.Name,
                    recipeQuery.Query,
                    recipeQuery.Description,
                    recipeQuery.Severity,
                    [.. recipeQuery.RiskEvidence],
                    ToSearchRecipeGuardFilterJsonResults(recipeQuery.GuardFilters),
                    [.. recipeQuery.PathPatterns],
                    [.. recipeQuery.ExcludePaths],
                    [.. recipeQuery.MatchOrigins],
                    [.. recipeQuery.ExcludeOrigins],
                    [.. recipeQuery.ResultKinds],
                    [.. recipeQuery.Classifiers],
                    recipeQuery.StringComparisonTaxonomy,
                    recipeQuery.BroadCatchTaxonomy,
                    BuildSearchRecipeClassifierCounts(rows),
                    rows.Count,
                    rows.Count,
                    outputSelection.OriginalCount,
                    minimumOmitted,
                    selectionReason,
                    selectionReason != null ? outputSelection.SelectionOmittedCount : null,
                    resultLimit,
                    minimumOmitted,
                    BuildSearchRecipeTopFiles(rows),
                    outputSelection.LimitTruncated,
                    outputSelection.LimitTruncated
                        && !options.FirstPerFile
                        && !options.SampleSize.HasValue
                        && rows.Count > 0
                            ? FormatSearchRecipeCursor(rows[^1].Result, reader, scope, options, recipeQuery)
                            : null,
                    rows.Select(row => new SearchRecipeCompactResultJsonResult(
                        row.Result.Path,
                        row.Result.Lang,
                        row.Result.Visibility,
                        [.. recipeQuery.RiskEvidence],
                        row.Compact.AuditClassifications,
                        row.Result.StartLine,
                        row.Result.EndLine,
                        row.Compact.MatchLines,
                        row.Compact.EnclosingSymbolName,
                        row.Compact.EnclosingSymbolKind)).ToList(),
                    outputSelection.SourceTotal,
                    outputSelection.SourceTotalAuthoritative,
                    outputSelection.SourceTotalAuthoritative ? null : outputSelection.SourceTotal,
                    outputSelection.SelectedTotal,
                    outputSelection.Returned,
                    outputSelection.SelectorOmittedCount,
                    outputSelection.LimitOmittedCount,
                    outputSelection.Selectors));
                freshnessObservations.Add(SuccessfulSearchQueryObservation(
                    freshnessContext,
                    recipeQuery.Name,
                    outputSelection.OriginalCount));
            }
            catch (Exception ex) when (TryClassifySearchQueryExecutionFailure(ex, out _))
            {
                TryClassifySearchQueryExecutionFailure(ex, out var failureReason);
                hasFailures = true;
                freshnessObservations.Add(FailedSearchQueryObservation(
                    freshnessContext,
                    recipeQuery.Name,
                    failureReason));
            }
        }

        return queryResults;
    }

    private static string? GetSearchRecipeSelectionReason(SearchOutputSelection selection)
        => selection.SelectionOmittedCount > 0
            && selection.TruncationReason is "first_per_file" or "sample"
                ? selection.TruncationReason
                : null;

    private static int GetSearchRecipeFetchLimit(
        QueryCommandOptions options,
        int resultLimit,
        SearchAuditRecipeQuery? recipeQuery = null)
    {
        if (recipeQuery is { SemanticFilter: not SearchRecipeSemanticFilter.None })
            return int.MaxValue;
        if (options.ExcludeSafeXml)
            return SearchOriginFilterMaxCandidates;

        var selectionTarget = resultLimit > 0 && options.SampleSize.HasValue
            ? Math.Max(resultLimit, options.SampleSize.Value)
            : resultLimit;
        return FetchLimitForSearchEnvelope(selectionTarget);
    }

    private static List<SearchRecipeCountQueryJsonResult> CountSearchRecipeQueryResults(
        DbReader reader,
        IReadOnlyList<SearchAuditRecipeQuery> recipeQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        bool userExact,
        SearchQueryFreshnessContext? freshnessContext,
        out int total,
        out int fileCount,
        out List<SearchQueryFreshnessObservation> freshnessObservations,
        out bool hasFailures)
    {
        var queryCounts = new List<SearchRecipeCountQueryJsonResult>();
        freshnessObservations = [];
        var paths = new HashSet<string>(StringComparer.Ordinal);
        total = 0;
        hasFailures = false;
        foreach (var recipeQuery in recipeQueries)
        {
            try
            {
                var exact = ResolveSearchRecipeMatchMode(recipeQuery, options).Exact;
                var materializationRequest = new SearchRecipeQueryMaterializationRequest(
                    reader,
                    scope,
                    options,
                    recipeQuery,
                    exact,
                    ResultLimit: null,
                    RawFtsOverride: false);
                var materialization = MaterializeSearchRecipeQuery(in materializationRequest);
                var rows = materialization.Rows;
                if (options.Json && !options.SummaryOnly)
                    ApplySearchRecipeAuditClassifications(reader, recipeQuery, recipeQueries, rows);
                var count = rows.Count;
                var fileCountForQuery = rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count();
                foreach (var path in rows.Select(row => row.Result.Path))
                    paths.Add(path);

                total += count;
                queryCounts.Add(new SearchRecipeCountQueryJsonResult(
                    recipeQuery.Name,
                    recipeQuery.Query,
                    recipeQuery.Description,
                    recipeQuery.Severity,
                    count,
                    count,
                    0,
                    count,
                    fileCountForQuery,
                    false,
                    BuildSearchRecipeClassifierCounts(rows),
                    BuildSearchRecipeTopFiles(rows)));
                if (freshnessContext != null)
                {
                    freshnessObservations.Add(SuccessfulSearchQueryObservation(
                        freshnessContext,
                        recipeQuery.Name,
                        count));
                }
            }
            catch (Exception ex) when (
                freshnessContext != null
                && TryClassifySearchQueryExecutionFailure(ex, out _))
            {
                TryClassifySearchQueryExecutionFailure(ex, out var failureReason);
                hasFailures = true;
                freshnessObservations.Add(FailedSearchQueryObservation(
                    freshnessContext,
                    recipeQuery.Name,
                    failureReason));
            }
        }

        fileCount = paths.Count;
        return queryCounts;
    }

    private static List<SearchRecipeAggregationQueryJsonResult> CollectSearchRecipeAggregationResults(
        DbReader reader,
        IReadOnlyList<SearchAuditRecipeQuery> recipeQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        bool userExact,
        string groupBy,
        out int total,
        out int fileCount)
    {
        var queryResults = new List<SearchRecipeAggregationQueryJsonResult>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        total = 0;
        foreach (var recipeQuery in recipeQueries)
        {
            var exact = ResolveSearchRecipeMatchMode(recipeQuery, options).Exact;
            var materializationRequest = new SearchRecipeQueryMaterializationRequest(
                reader,
                scope,
                options,
                recipeQuery,
                exact,
                ResultLimit: null,
                RawFtsOverride: false);
            var materialization = MaterializeSearchRecipeQuery(in materializationRequest);
            var rows = materialization.Rows;
            foreach (var path in rows.Select(row => row.Result.Path))
                paths.Add(path);

            var groups = BuildSearchGroupedCounts(groupBy, rows);
            var selection = ApplySearchGroupOutputSelection(groups, options);
            total += rows.Count;
            queryResults.Add(new SearchRecipeAggregationQueryJsonResult(
                recipeQuery.Name,
                recipeQuery.Query,
                recipeQuery.Description,
                recipeQuery.Severity,
                rows.Count,
                rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count(),
                selection.Groups.Count,
                selection.TotalGroups,
                selection.Truncated,
                options.Limit,
                selection.Groups));
        }

        fileCount = paths.Count;
        return queryResults;
    }

    private static IReadOnlyList<SearchGuardFilter> BuildSearchRecipeGuardFilters(
        QueryCommandOptions options,
        SearchAuditRecipeQuery recipeQuery)
    {
        if (recipeQuery.GuardFilters.Count == 0)
            return options.GuardFilters;
        if (options.GuardFilters.Count == 0)
            return recipeQuery.GuardFilters;

        var guardFilters = new List<SearchGuardFilter>(recipeQuery.GuardFilters.Count + options.GuardFilters.Count);
        guardFilters.AddRange(recipeQuery.GuardFilters);
        guardFilters.AddRange(options.GuardFilters);
        return guardFilters;
    }
}
