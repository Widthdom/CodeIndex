using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal const int DefaultAuditAllTotalLimit = 200;
    internal const int DefaultAuditAllJsonByteLimit = 4 * 1024 * 1024;
    internal const int AuditAllCandidateRowsPerQuery = 10_000;
    internal const int AuditAllQueryDetailLimit = 512;
    internal const int AuditAllErrorLimit = 32;
    internal const int AuditAllRecoveryCommandLimit = 3;
    internal static TimeSpan? AuditAllTimeBudgetForTesting { get; set; }

    private static readonly TimeSpan DefaultAuditAllTimeBudget = TimeSpan.FromMinutes(5);

    private sealed class AuditAllRecipeRun(SearchAuditRecipe recipe)
    {
        internal SearchAuditRecipe Recipe { get; } = recipe;
        internal SearchRecipeScopeJsonResult? Scope { get; set; }
        internal string Status { get; set; } = "completed";
        internal string? OmittedReason { get; set; }
        internal List<AuditAllQueryRun> Queries { get; } = [];
        internal int OmittedQueryCount { get; set; }
    }

    private sealed class AuditAllQueryRun(SearchAuditRecipeQuery query)
    {
        internal int Position { get; set; }
        internal int RowOffset { get; set; }
        internal SearchAuditRecipeQuery Query { get; } = query;
        internal string Status { get; set; } = "completed";
        internal string? FailureReason { get; set; }
        internal SearchRecipeQueryResultJsonResult? Result { get; set; }
        internal SearchRecipeQueryFreshnessStateJsonResult? Freshness { get; set; }
        internal int ByteOmittedResultCount { get; set; }
        internal int DetailOmittedResultCount { get; set; }
    }

    private sealed class AuditAllRunState(
        IReadOnlyList<SearchAuditRecipe> selectedRecipes,
        IReadOnlyList<string> registryDiagnostics,
        int effectiveTotalLimit,
        TimeSpan timeBudget)
    {
        internal IReadOnlyList<SearchAuditRecipe> SelectedRecipes { get; } = selectedRecipes;
        internal IReadOnlyList<string> RegistryDiagnostics { get; } = registryDiagnostics;
        internal int EffectiveTotalLimit { get; } = effectiveTotalLimit;
        internal TimeSpan TimeBudget { get; } = timeBudget;
        internal List<AuditAllRecipeRun> Recipes { get; } = [];
        internal List<JsonObject> Errors { get; } = [];
        internal int OmittedErrorCount { get; set; }
        internal int EmittedResultCount { get; set; }
        internal int MaterializedResultCount { get; set; }
        internal int ByteOmittedResultCount { get; set; }
        internal int DetailOmittedResultCount { get; set; }
        internal long AccumulatedResultBytes { get; set; }
        internal bool Cancelled { get; set; }
        internal bool TimeBudgetExceeded { get; set; }
        internal bool ResultLimitReached { get; set; }
        internal bool ByteBudgetReached { get; set; }
        internal string IndexState { get; set; } = "unknown";
        internal string? IndexReason { get; set; }
        internal string? BaselineStartGeneration { get; set; }
        internal long ElapsedMilliseconds { get; set; }
        internal string? ContinuationInput { get; set; }
        internal string? ContinuationBinding { get; set; }
        internal int[]? InitialOffsets { get; set; }
        internal bool GenerationChanged { get; set; }
        internal bool SuppressRows { get; set; }
    }

    private sealed class AuditAllResultSnapshot
    {
        private readonly List<(AuditAllQueryRun QueryRun, CompactSearchResult[] Results, int InitialByteOmittedCount)> _entries = [];

        private int InitialByteOmittedResultCount { get; set; }

        internal int TotalResultCount { get; private set; }

        internal List<int> QueryPrefixEnds()
        {
            var ends = new List<int>();
            var count = 0;
            foreach (var (_, results, _) in _entries)
            {
                if (results.Length == 0) continue;
                count += results.Length;
                ends.Add(count);
            }
            return ends;
        }

        internal static AuditAllResultSnapshot Capture(AuditAllRunState state)
        {
            var snapshot = new AuditAllResultSnapshot();
            foreach (var recipeRun in state.Recipes)
            {
                foreach (var queryRun in recipeRun.Queries)
                {
                    if (queryRun.Result == null)
                        continue;
                    var results = queryRun.Result.Results.ToArray();
                    snapshot._entries.Add((queryRun, results, queryRun.ByteOmittedResultCount));
                    snapshot.TotalResultCount += results.Length;
                    snapshot.InitialByteOmittedResultCount += queryRun.ByteOmittedResultCount;
                }
            }
            return snapshot;
        }

        internal void ApplyPrefix(AuditAllRunState state, int resultCount)
        {
            var remaining = Math.Clamp(resultCount, 0, TotalResultCount);
            foreach (var (queryRun, results, initialByteOmittedCount) in _entries)
            {
                var keep = Math.Min(remaining, results.Length);
                queryRun.Result!.Results.Clear();
                for (var i = 0; i < keep; i++)
                    queryRun.Result.Results.Add(results[i]);
                queryRun.ByteOmittedResultCount = initialByteOmittedCount + results.Length - keep;
                remaining -= keep;
            }

            state.EmittedResultCount = Math.Clamp(resultCount, 0, TotalResultCount);
            state.ByteOmittedResultCount = InitialByteOmittedResultCount + TotalResultCount - state.EmittedResultCount;
        }
    }

    private static int RunAuditAll(
        string[] subArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
        => RunAuditAll(
            subArgs,
            jsonOptions,
            cancellationToken,
            SearchAuditRecipes.Load());

    internal static int RunAuditAllForTesting(
        string[] subArgs,
        JsonSerializerOptions jsonOptions,
        IReadOnlyList<SearchAuditRecipe> recipes,
        CancellationToken cancellationToken = default,
        Action? afterQueryForTesting = null,
        Action<DbReader>? beforeQueryForTesting = null)
        => RunAuditAll(
            subArgs,
            jsonOptions,
            cancellationToken,
            new SearchAuditRecipeRegistry(recipes, []),
            afterQueryForTesting,
            beforeQueryForTesting);

    private static int RunAuditAll(
        string[] subArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        SearchAuditRecipeRegistry registry,
        Action? afterQueryForTesting = null,
        Action<DbReader>? beforeQueryForTesting = null,
        Func<DbReader?, QueryCommandOptions, AuditAllRunState, int>? consume = null)
    {
        var selectedRecipes = registry.Recipes
            .OrderBy(recipe => recipe.Name, StringComparer.Ordinal)
            .ToList();
        if (selectedRecipes.Count == 0)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                ProgramRunner.ContainsJsonOutputFlag(subArgs),
                jsonOptions,
                "audit --all could not select any registered recipes.",
                CommandExitCodes.UsageError,
                "Run `cdidx recipes` to inspect recipe source diagnostics, then add or restore at least one valid recipe.",
                GetUsageLineOrThrow("audit"),
                CommandErrorCodes.UsageError,
                command: "audit");
        }

        if (!TryExtractAuditContinuation(subArgs, out var cleanArgs, out var continuation))
            return WriteAuditContinuationError(subArgs, jsonOptions);
        var normalizedArgs = AddAuditAllSummaryFormatIfNeeded(cleanArgs);
        var searchArgs = new string[normalizedArgs.Length + 2];
        searchArgs[0] = "--recipe";
        searchArgs[1] = selectedRecipes[0].Name;
        Array.Copy(normalizedArgs, 0, searchArgs, 2, normalizedArgs.Length);
        var validationArgs = normalizedArgs.Where(arg => arg != "--all").ToArray();
        if (!TryPrepareSearchRoute(
                searchArgs,
                validationArgs,
                QueryCommandInvocationContext.Audit,
                jsonOptions,
                cancellationToken,
                out var route))
        {
            return CommandExitCodes.UsageError;
        }

        var options = route.Options;
        if (!TryValidateAuditAllOptions(options))
            return CommandExitCodes.UsageError;

        using var exactLanguageScope = DbReader.BeginExactQueryLanguageScope(options.Lang);
        return ExecuteAuditAll(
            options,
            jsonOptions,
            route.Exact,
            selectedRecipes,
            registry.Diagnostics,
            cancellationToken,
            afterQueryForTesting,
            beforeQueryForTesting,
            consume,
            continuation);
    }

    private static string[] AddAuditAllSummaryFormatIfNeeded(string[] args)
    {
        var hasSummaryOnly = false;
        var hasExplicitOutputFormat = false;
        var passthroughIndex = args.Length;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                passthroughIndex = i;
                break;
            }

            hasSummaryOnly |= args[i] == "--summary-only";
            hasExplicitOutputFormat |= args[i] is "--compact" or "--format"
                || args[i].StartsWith("--format=", StringComparison.Ordinal);
        }

        if (!hasSummaryOnly || hasExplicitOutputFormat)
            return args;

        var normalized = new string[args.Length + 2];
        Array.Copy(args, 0, normalized, 0, passthroughIndex);
        normalized[passthroughIndex] = "--format";
        normalized[passthroughIndex + 1] = OutputFormatCompact;
        Array.Copy(args, passthroughIndex, normalized, passthroughIndex + 2, args.Length - passthroughIndex);
        return normalized;
    }

    private static bool TryValidateAuditAllOptions(QueryCommandOptions options)
    {
        if (!options.All)
            return RejectSearchUsage(options, "audit --all requires --all.", "Add --all, or pass one recipe name after `cdidx audit`.");
        if (options.IncludeRecipeQueries.Count > 0 || options.ExcludeRecipeQueries.Count > 0)
        {
            return RejectSearchUsage(
                options,
                "audit --all cannot be combined with --include-query or --exclude-query because those selectors are recipe-specific.",
                "Remove the child-query selectors, or run one named recipe instead of --all.");
        }
        if (options.SearchCursor.HasValue)
        {
            return RejectSearchUsage(
                options,
                "audit --all cannot be combined with --cursor because one cursor cannot resume multiple recipes.",
                "Remove --cursor and use --total-limit, or resume an individual recipe/query.");
        }
        if (HasSearchAggregation(options))
        {
            return RejectSearchUsage(
                options,
                "audit --all does not support cross-recipe aggregation.",
                "Use --count, --summary-only, or --format compact; run one recipe when grouped or unique aggregation is required.");
        }
        if (options.OutputFormat is OutputFormatSarif or OutputFormatIssueDrafts)
        {
            return RejectSearchUsage(
                options,
                "audit --all supports text, JSON, NDJSON, count, and compact output only.",
                "Use --format compact for a bounded cross-recipe summary, or export SARIF/issue drafts one recipe at a time.");
        }
        if (options.ResultsOnly)
        {
            return RejectSearchUsage(
                options,
                "audit --all does not support --results-only because the terminal cross-recipe summary is required.",
                "Remove --results-only; NDJSON row output still includes recipe and query attribution plus a terminal summary.");
        }

        return true;
    }

    private static int ExecuteAuditAll(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool userExact,
        IReadOnlyList<SearchAuditRecipe> selectedRecipes,
        IReadOnlyList<string> registryDiagnostics,
        CancellationToken cancellationToken,
        Action? afterQueryForTesting,
        Action<DbReader>? beforeQueryForTesting,
        Func<DbReader?, QueryCommandOptions, AuditAllRunState, int>? consume = null,
        string? continuation = null)
    {
        var effectiveTotalLimit = options.TotalLimit ?? DefaultAuditAllTotalLimit;
        var timeBudget = AuditAllTimeBudgetForTesting ?? DefaultAuditAllTimeBudget;
        var state = new AuditAllRunState(selectedRecipes, registryDiagnostics, effectiveTotalLimit, timeBudget);
        state.ContinuationInput = continuation;
        using var progress = ConsoleUi.ShouldUseProgressAnimation()
            && (options.Progress || ConsoleUi.ShouldUseInteractiveStandardError())
                ? new ConsoleUi.AuditProgress(selectedRecipes.Count, selectedRecipes.Sum(recipe => (long)recipe.Queries.Count),
                    Console.Error, ConsoleUi.ShouldUseInteractiveStandardError(), ConsoleUi.GetWindowWidth(), startImmediately: false)
                : null;
        if (cancellationToken.IsCancellationRequested)
        {
            state.Cancelled = true;
            AddOmittedAuditAllRecipes(state, 0, "cancelled");
            progress?.Start();
            progress?.PauseForOutput();
            var cancelledExitCode = consume != null ? consume(null, options, state) : WriteAuditAllOutput(options, jsonOptions, state);
            progress?.Finish("cancelled");
            return cancelledExitCode;
        }
        var exitCode = WithDb(
            options,
            jsonOptions,
            reader => ExecuteAuditAllWithReader(
                reader,
                options,
                jsonOptions,
                userExact,
                state,
                progress,
                cancellationToken,
                afterQueryForTesting,
                beforeQueryForTesting,
                consume),
            cancellationToken: cancellationToken);
        progress?.Finish(state.Cancelled || exitCode == CommandExitCodes.CancelledBySignal ? "cancelled"
            : exitCode != CommandExitCodes.Success && exitCode != CommandExitCodes.PartialResult ? "failed"
            : !AuditExecutionComplete(state) || AuditHasObservationOmissions(state) || state.ByteBudgetReached
                || state.ByteOmittedResultCount > 0 || state.Errors.Count > 0 || state.OmittedErrorCount > 0 ? "partial"
            : "completed");
        return exitCode;
    }

    private static int ExecuteAuditAllWithReader(
        DbReader reader,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool userExact,
        AuditAllRunState state,
        ConsoleUi.AuditProgress? progress,
        CancellationToken cancellationToken,
        Action? afterQueryForTesting,
        Action<DbReader>? beforeQueryForTesting,
        Func<DbReader?, QueryCommandOptions, AuditAllRunState, int>? consume = null)
    {
        var stopwatch = Stopwatch.StartNew();
        if (consume == null && !InitializeAuditContinuation(reader, options, state))
            return WriteAuditContinuationError([], jsonOptions, options.Json);
        if (consume != null)
            state.BaselineStartGeneration = reader.GetPaginationGeneration().Identity;
        var includeRows = !options.CountOnly && !options.SummaryOnly;
        state.SuppressRows = !includeRows;
        var indexState = ResolveSearchQueryIndexFreshness(reader, options, out var indexReason);
        state.IndexState = indexState;
        state.IndexReason = indexReason;
        var completedRecipes = 0;
        long completedQueries = 0;
        long failedQueries = 0;
        progress?.Start();
        try
        {
            for (var recipeIndex = 0; recipeIndex < state.SelectedRecipes.Count; recipeIndex++)
            {
                var recipe = state.SelectedRecipes[recipeIndex];
                var recipeOffset = state.SelectedRecipes.Take(recipeIndex).Sum(item => item.Queries.Count);
                var previouslyAccounted = state.InitialOffsets != null
                    && state.InitialOffsets.Skip(recipeOffset).Take(recipe.Queries.Count).All(offset => offset < 0);
                if (!previouslyAccounted && ShouldStopAuditAllAccumulation(state, stopwatch, cancellationToken, includeRows))
                {
                    AddOmittedAuditAllRecipes(state, recipeIndex, GetAuditAllStopReason(state));
                    break;
                }

                var recipeRun = new AuditAllRecipeRun(recipe);
                state.Recipes.Add(recipeRun);
                var scope = BuildSearchRecipeScope(recipe, options);
                recipeRun.Scope = scope;
                var freshnessContext = BuildSearchRecipeFreshnessContext(
                    recipe,
                    recipe.Queries,
                    indexState,
                    indexReason);
                for (var queryIndex = 0; queryIndex < recipe.Queries.Count; queryIndex++)
                {
                    var position = recipeOffset + queryIndex;
                    if (state.InitialOffsets is { } offsets && offsets[position] < 0)
                    {
                        recipeRun.Queries.Add(new AuditAllQueryRun(recipe.Queries[queryIndex])
                        {
                            Position = position,
                            Status = "previously_accounted",
                        });
                        completedQueries++;
                        progress?.SetCompleted(completedRecipes, completedQueries, failedQueries);
                        continue;
                    }
                    if (ShouldStopAuditAllAccumulation(state, stopwatch, cancellationToken, includeRows))
                    {
                        recipeRun.Status = "partial";
                        recipeRun.OmittedReason = GetAuditAllStopReason(state);
                        recipeRun.OmittedQueryCount = recipe.Queries.Count - queryIndex;
                        break;
                    }

                    var query = recipe.Queries[queryIndex];
                    progress?.SetActive(recipeIndex + 1, queryIndex + 1);
                    var queryRun = new AuditAllQueryRun(query);
                    queryRun.Position = position;
                    queryRun.RowOffset = state.InitialOffsets?[position] ?? 0;
                    recipeRun.Queries.Add(queryRun);
                    using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var remainingBudget = state.TimeBudget - stopwatch.Elapsed;
                    queryCancellation.CancelAfter(remainingBudget > TimeSpan.Zero ? remainingBudget : TimeSpan.Zero);
                    try
                    {
                        using var cancellationScope = reader.BeginCancellationScope(queryCancellation.Token);
                        beforeQueryForTesting?.Invoke(reader);
                        List<SearchQueryFreshnessObservation> observations = [];
                        var hasFailures = false;
                        var queryResults = reader.RunWithCancellationInterrupt(() => CollectSearchRecipeQueryResults(
                            reader,
                            [query],
                            scope,
                            options,
                            userExact,
                            freshnessContext,
                            includeAuditClassifications: !options.SummaryOnly && !options.CountOnly,
                            out _,
                            out _,
                            out observations,
                            out hasFailures,
                            emittedBefore: includeRows ? state.MaterializedResultCount : 0,
                            aggregateResultLimit: includeRows ? state.EffectiveTotalLimit : options.Limit,
                            fetchLimitCap: AuditAllCandidateRowsPerQuery,
                            auditClassificationQueries: recipe.Queries,
                            auditRowOffset: state.InitialOffsets != null && includeRows ? queryRun.RowOffset : null));
                        queryRun.Freshness = BuildAuditAllQueryFreshness(
                            freshnessContext,
                            query,
                            observations.FirstOrDefault(observation => string.Equals(observation.Name, query.Name, StringComparison.Ordinal)));
                        if (hasFailures || queryResults.Count == 0)
                        {
                            queryRun.Status = "failed";
                            queryRun.FailureReason = observations
                                .FirstOrDefault(observation => !observation.ExecutionSucceeded)
                                ?.FailureReason
                                ?? "query_execution_failed";
                            AddAuditAllError(state, recipe.Name, query.Name, queryRun.FailureReason);
                            continue;
                        }

                        queryRun.Result = queryResults[0];
                        if (includeRows)
                        {
                            var materializedCount = queryRun.Result.Results.Count;
                            state.MaterializedResultCount += materializedCount;
                            TrimAuditAllQueryResultsToByteBudget(options, jsonOptions, state, recipeRun, queryRun);
                            state.EmittedResultCount += queryRun.Result.Results.Count;
                            if (queryRun.Result.Truncated && state.MaterializedResultCount >= state.EffectiveTotalLimit)
                                state.ResultLimitReached = true;
                        }
                        else
                        {
                            queryRun.Result.Results.Clear();
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        state.Cancelled = true;
                        queryRun.Status = "omitted";
                        queryRun.FailureReason = "cancelled";
                        queryRun.Freshness = BuildAuditAllQueryFreshness(
                            freshnessContext,
                            query,
                            FailedSearchQueryObservation(freshnessContext, query.Name, "cancelled"));
                        recipeRun.Status = "partial";
                        recipeRun.OmittedReason = "cancelled";
                        recipeRun.OmittedQueryCount = recipe.Queries.Count - queryIndex;
                        break;
                    }
                    catch (OperationCanceledException) when (queryCancellation.IsCancellationRequested)
                    {
                        state.TimeBudgetExceeded = true;
                        queryRun.Status = "omitted";
                        queryRun.FailureReason = "time_budget";
                        queryRun.Freshness = BuildAuditAllQueryFreshness(
                            freshnessContext,
                            query,
                            FailedSearchQueryObservation(freshnessContext, query.Name, "time_budget"));
                        recipeRun.Status = "partial";
                        recipeRun.OmittedReason = "time_budget";
                        recipeRun.OmittedQueryCount = recipe.Queries.Count - queryIndex;
                        break;
                    }
                    catch (Exception ex)
                    {
                        queryRun.Status = "failed";
                        queryRun.FailureReason = $"{ex.GetType().Name}: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}";
                        queryRun.Freshness = BuildAuditAllQueryFreshness(
                            freshnessContext,
                            query,
                            FailedSearchQueryObservation(freshnessContext, query.Name, queryRun.FailureReason));
                        AddAuditAllError(state, recipe.Name, query.Name, queryRun.FailureReason);
                    }
                    finally
                    {
                        if (queryRun.Status == "completed")
                            completedQueries++;
                        else if (queryRun.Status == "failed")
                            failedQueries++;
                        progress?.SetCompleted(completedRecipes, completedQueries, failedQueries);
                        progress?.SetActive(0, 0);
                        afterQueryForTesting?.Invoke();
                    }

                    if (stopwatch.Elapsed >= state.TimeBudget)
                        state.TimeBudgetExceeded = true;
                }

                FinalizeAuditAllRecipeStatus(recipeRun);
                if (recipeRun.Status == "completed")
                    completedRecipes++;
                progress?.SetCompleted(completedRecipes, completedQueries, failedQueries);
                if (state.Cancelled || state.TimeBudgetExceeded)
                {
                    AddOmittedAuditAllRecipes(state, recipeIndex + 1, GetAuditAllStopReason(state));
                    break;
                }
            }
        }
        finally
        {
            progress?.SetActive(0, 0);
            progress?.PauseForOutput();
        }

        stopwatch.Stop();
        state.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        if (state.ContinuationBinding != null)
            state.GenerationChanged = state.ContinuationBinding != BuildAuditContinuationBinding(reader, options, state);
        return consume != null ? consume(reader, options, state) : WriteAuditAllOutput(options, jsonOptions, state);
    }

    private static void TrimAuditAllQueryResultsToByteBudget(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        AuditAllRunState state,
        AuditAllRecipeRun recipeRun,
        AuditAllQueryRun queryRun)
    {
        if (!options.Json && options.OutputFormat == OutputFormatText || queryRun.Result == null)
            return;

        var results = queryRun.Result.Results;
        var byteLimit = options.MaxJsonBytes ?? DefaultAuditAllJsonByteLimit;
        var serializationOptions = EnsureJsonNodeSerializerOptions(jsonOptions);
        var ndjson = IsAuditAllNdjson(options);
        var retainedCount = 0;
        foreach (var result in results)
        {
            var row = options.SearchFields != null
                ? BuildProjectedSearchResult(
                    result,
                    options.SearchFields,
                    queryRun.Query.Name,
                    recipeRun.Recipe.Name)
                : BuildAuditAllResultNode(
                    recipeRun.Recipe.Name,
                    queryRun.Query.Name,
                    result,
                    options.OutputFormat == OutputFormatCompact,
                    serializationOptions);
            if (ndjson)
                AddActiveSqliteDiagnostics(row);
            var rowBytes = JsonLineBytes(row.ToJsonString(serializationOptions));
            if (state.AccumulatedResultBytes + rowBytes > byteLimit)
                break;

            state.AccumulatedResultBytes += rowBytes;
            retainedCount++;
        }

        if (retainedCount == results.Count)
            return;

        var omittedCount = results.Count - retainedCount;
        results.RemoveRange(retainedCount, omittedCount);
        queryRun.ByteOmittedResultCount += omittedCount;
        state.ByteOmittedResultCount += omittedCount;
        state.ByteBudgetReached = true;
    }

    private static bool IsAuditAllNdjson(QueryCommandOptions options)
        => options.SearchFields != null
           || options.Json
           && options.JsonOutputFormatExplicit
           && options.JsonOutputFormat == JsonOutputFormatNdjson;

    private static SearchRecipeQueryFreshnessStateJsonResult BuildAuditAllQueryFreshness(
        SearchQueryFreshnessContext context,
        SearchAuditRecipeQuery query,
        SearchQueryFreshnessObservation? observation)
    {
        var expected = context.ExpectedQueries
            .FirstOrDefault(candidate => string.Equals(candidate.Name, query.Name, StringComparison.Ordinal))
            ?? new SearchQueryFreshnessExpectedQuery(query.Name, SearchQueryFreshnessUnknownDefinitionVersion);
        var queryContext = context with
        {
            ExpectedQueries = [expected],
        };
        var observations = observation == null
            ? Array.Empty<SearchQueryFreshnessObservation>()
            : [observation];
        return BuildSearchRecipeQueryFreshness(queryContext, observations).Queries[0];
    }

    private static bool ShouldStopAuditAllAccumulation(
        AuditAllRunState state,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        bool includeRows)
    {
        if (cancellationToken.IsCancellationRequested)
            state.Cancelled = true;
        if (stopwatch.Elapsed >= state.TimeBudget)
            state.TimeBudgetExceeded = true;
        if (includeRows && state.MaterializedResultCount >= state.EffectiveTotalLimit)
            state.ResultLimitReached = true;
        return state.Cancelled || state.TimeBudgetExceeded || state.ResultLimitReached || state.ByteBudgetReached;
    }

    private static string GetAuditAllStopReason(AuditAllRunState state)
        => state.Cancelled
            ? "cancelled"
            : state.TimeBudgetExceeded
                ? "time_budget"
                : state.ResultLimitReached
                    ? "total_limit"
                    : "response_byte_limit";

    private static void AddOmittedAuditAllRecipes(AuditAllRunState state, int startIndex, string reason)
    {
        for (var i = startIndex; i < state.SelectedRecipes.Count; i++)
        {
            state.Recipes.Add(new AuditAllRecipeRun(state.SelectedRecipes[i])
            {
                Status = "omitted",
                OmittedReason = reason,
                OmittedQueryCount = state.SelectedRecipes[i].Queries.Count,
            });
        }
    }

    private static void FinalizeAuditAllRecipeStatus(AuditAllRecipeRun recipeRun)
    {
        if (recipeRun.Status is "partial" or "omitted")
            return;
        var failed = recipeRun.Queries.Count(query => query.Status == "failed");
        if (failed == 0)
            return;
        recipeRun.Status = failed == recipeRun.Recipe.Queries.Count ? "failed" : "partial";
    }

    private static void AddAuditAllError(
        AuditAllRunState state,
        string recipeName,
        string queryName,
        string reason)
    {
        if (state.Errors.Count >= AuditAllErrorLimit)
        {
            state.OmittedErrorCount++;
            return;
        }

        state.Errors.Add(new JsonObject
        {
            ["recipe"] = recipeName,
            ["query_name"] = queryName,
            ["category"] = "query_execution",
            ["reason"] = reason.Length <= 512 ? reason : reason[..512],
        });
    }

    private static int WriteAuditAllOutput(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        AuditAllRunState state)
    {
        if (!options.Json && options.OutputFormat == OutputFormatText)
        {
            WriteAuditAllText(options, state);
            return GetAuditAllExitCode(options, state);
        }

        var serializationOptions = EnsureJsonNodeSerializerOptions(jsonOptions);
        var byteLimit = options.MaxJsonBytes ?? DefaultAuditAllJsonByteLimit;
        var ndjson = IsAuditAllNdjson(options);
        if (ndjson)
            return WriteAuditAllNdjson(options, jsonOptions, serializationOptions, state, byteLimit);

        // JSON query-detail admission is also row admission. Hidden query payloads
        // must stay pending instead of being acknowledged by continuation.
        foreach (var query in state.Recipes.SelectMany(recipe => recipe.Queries).Skip(AuditAllQueryDetailLimit))
        {
            var count = query.Result?.Results.Count ?? 0;
            query.Result?.Results.Clear();
            query.DetailOmittedResultCount += count;
            state.DetailOmittedResultCount += count;
            state.EmittedResultCount -= count;
        }

        var includePayloadResults = !ndjson && !options.CountOnly && !options.SummaryOnly;
        var payload = BuildAuditAllPayload(options, state, includeResults: includePayloadResults);
        var json = payload.ToJsonString(serializationOptions);
        if (GetJsonDocumentByteCount(json) > byteLimit)
        {
            var snapshot = AuditAllResultSnapshot.Capture(state);
            if (snapshot.TotalResultCount > 0)
                json = FindLargestAuditAllJsonPrefix(options, serializationOptions, state, snapshot, byteLimit, includePayloadResults);
        }

        if (GetJsonDocumentByteCount(json) > byteLimit)
        {
            var minimumRequiredBytes = GetJsonDocumentByteCount(json);
            var retryByIncreasingBudget = minimumRequiredBytes <= MaxSearchJsonByteLimit;
            return CommandErrorWriter.WriteResponseBudgetError(
                json: true,
                jsonOptions,
                "audit",
                $"audit --all summary JSON output is {minimumRequiredBytes.ToString(CultureInfo.InvariantCulture)} bytes and exceeds the effective {byteLimit.ToString(CultureInfo.InvariantCulture)}-byte response budget.",
                retryByIncreasingBudget
                    ? "Increase --max-json-bytes or reduce the external recipe registry; the bounded summary metadata cannot be reduced further."
                    : "Reduce the external recipe registry; the bounded summary exceeds the maximum effective --max-json-bytes value.",
                requestedBytes: options.RequestedMaxJsonBytes ?? options.MaxJsonBytes,
                effectiveBytes: byteLimit,
                minimumRequiredBytes: minimumRequiredBytes,
                recommendedBytes: retryByIncreasingBudget ? minimumRequiredBytes : null,
                usage: GetUsageLineOrThrow("audit"),
                retryByIncreasingBudget: retryByIncreasingBudget,
                maximumEffectiveBytes: MaxSearchJsonByteLimit);
        }

        Console.WriteLine(json);
        return GetAuditAllExitCode(options, state);
    }

    private static string FindLargestAuditAllJsonPrefix(
        QueryCommandOptions options,
        JsonSerializerOptions serializationOptions,
        AuditAllRunState state,
        AuditAllResultSnapshot snapshot,
        int byteLimit,
        bool includeResults)
    {
        string? bestJson = null;
        var bestCount = FindLargestAuditAllPrefix(snapshot, candidateCount =>
        {
            snapshot.ApplyPrefix(state, candidateCount);
            var candidateJson = BuildAuditAllPayload(options, state, includeResults)
                .ToJsonString(serializationOptions);
            if (GetJsonDocumentByteCount(candidateJson) <= byteLimit)
            {
                bestJson = candidateJson;
                return true;
            }
            return false;
        });

        if (bestCount >= 0)
        {
            snapshot.ApplyPrefix(state, bestCount);
            return bestJson!;
        }

        snapshot.ApplyPrefix(state, 0);
        return BuildAuditAllPayload(options, state, includeResults).ToJsonString(serializationOptions);
    }

    private static int FindLargestAuditAllPrefix(AuditAllResultSnapshot snapshot, Func<int, bool> fits)
    {
        // Completing a query can remove continuation/fallback/restart metadata.
        // Probe those discontinuities separately, descending from the full response.
        var ends = snapshot.QueryPrefixEnds();
        for (var i = ends.Count - 1; i >= 0; i--)
        {
            if (fits(ends[i])) return ends[i];
            var low = (i == 0 ? 0 : ends[i - 1]) + 1;
            var high = ends[i] - 1;
            var best = -1;
            while (low <= high)
            {
                var candidate = low + (high - low) / 2;
                if (fits(candidate)) { best = candidate; low = candidate + 1; }
                else high = candidate - 1;
            }
            if (best >= 0) return best;
        }
        return fits(0) ? 0 : -1;
    }

    private static int WriteAuditAllNdjson(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        JsonSerializerOptions serializationOptions,
        AuditAllRunState state,
        int byteLimit)
    {
        var snapshot = AuditAllResultSnapshot.Capture(state);
        var rowLines = BuildAuditAllNdjsonRows(options, state, serializationOptions);
        var prefixBytes = new long[rowLines.Count + 1];
        for (var i = 0; i < rowLines.Count; i++)
            prefixBytes[i + 1] = prefixBytes[i] + JsonLineBytes(rowLines[i]);

        string? bestTerminalLine = null;
        var bestCount = FindLargestAuditAllPrefix(snapshot, candidateCount =>
        {
            snapshot.ApplyPrefix(state, candidateCount);
            var terminalLine = BuildAuditAllTerminalLine(options, state, serializationOptions);
            var totalBytes = prefixBytes[candidateCount] + JsonLineBytes(terminalLine);
            if (totalBytes <= byteLimit)
            {
                bestTerminalLine = terminalLine;
                return true;
            }
            return false;
        });

        if (bestCount < 0)
        {
            snapshot.ApplyPrefix(state, 0);
            var terminalLine = BuildAuditAllTerminalLine(options, state, serializationOptions);
            var minimumRequiredBytes = JsonLineBytes(terminalLine);
            return CommandErrorWriter.WriteResponseBudgetError(
                json: true,
                jsonOptions,
                "audit",
                $"audit --all NDJSON terminal record exceeds the effective {byteLimit.ToString(CultureInfo.InvariantCulture)}-byte response budget.",
                "Increase --max-json-bytes or reduce the external recipe registry; no partial NDJSON stream was written.",
                requestedBytes: options.RequestedMaxJsonBytes ?? options.MaxJsonBytes,
                effectiveBytes: byteLimit,
                minimumRequiredBytes: minimumRequiredBytes,
                recommendedBytes: minimumRequiredBytes <= MaxSearchJsonByteLimit ? minimumRequiredBytes : null,
                usage: GetUsageLineOrThrow("audit"),
                retryByIncreasingBudget: minimumRequiredBytes <= MaxSearchJsonByteLimit,
                maximumEffectiveBytes: MaxSearchJsonByteLimit);
        }

        snapshot.ApplyPrefix(state, bestCount);
        for (var i = 0; i < bestCount; i++)
            Console.WriteLine(rowLines[i]);
        Console.WriteLine(bestTerminalLine);
        return GetAuditAllExitCode(options, state);
    }

    private static string BuildAuditAllTerminalLine(
        QueryCommandOptions options,
        AuditAllRunState state,
        JsonSerializerOptions serializationOptions)
    {
        var terminal = BuildAuditAllPayload(options, state, includeResults: false);
        terminal["terminal_record"] = true;
        terminal["done"] = true;
        AddActiveSqliteDiagnostics(terminal);
        return terminal.ToJsonString(serializationOptions);
    }

    private static List<string> BuildAuditAllNdjsonRows(
        QueryCommandOptions options,
        AuditAllRunState state,
        JsonSerializerOptions jsonOptions)
    {
        var rows = new List<string>(state.EmittedResultCount);
        foreach (var recipeRun in state.Recipes)
        {
            foreach (var queryRun in recipeRun.Queries)
            {
                if (queryRun.Result == null)
                    continue;
                foreach (var result in queryRun.Result.Results)
                {
                    var row = options.SearchFields != null
                        ? BuildProjectedSearchResult(
                            result,
                            options.SearchFields,
                            queryRun.Query.Name,
                            recipeRun.Recipe.Name)
                        : BuildAuditAllResultNode(
                            recipeRun.Recipe.Name,
                            queryRun.Query.Name,
                            result,
                            options.OutputFormat == OutputFormatCompact,
                            jsonOptions);
                    row["recipe"] = recipeRun.Recipe.Name;
                    row["query_name"] = queryRun.Query.Name;
                    AddActiveSqliteDiagnostics(row);
                    rows.Add(row.ToJsonString(jsonOptions));
                }
            }
        }
        return rows;
    }

    private static JsonObject BuildAuditAllPayload(
        QueryCommandOptions options,
        AuditAllRunState state,
        bool includeResults)
    {
        var recipeNodes = new JsonArray();
        var serializationOptions = EnsureJsonNodeSerializerOptions(options.InvocationJsonOptions!);
        var emittedQueryDetails = 0;
        var omittedQueryDetails = 0;
        foreach (var recipeRun in state.Recipes)
        {
            var queryNodes = new JsonArray();
            foreach (var queryRun in recipeRun.Queries)
            {
                if (emittedQueryDetails >= AuditAllQueryDetailLimit)
                {
                    omittedQueryDetails++;
                    continue;
                }
                queryNodes.Add(BuildAuditAllQueryNode(
                    recipeRun.Recipe.Name,
                    queryRun,
                    options.OutputFormat == OutputFormatCompact,
                    includeResults,
                    serializationOptions));
                emittedQueryDetails++;
            }
            omittedQueryDetails += Math.Max(0, recipeRun.Recipe.Queries.Count - recipeRun.Queries.Count);
            var recipeCountAuthoritative = string.Equals(state.IndexState, "current", StringComparison.Ordinal)
                && recipeRun.Status == "completed"
                && recipeRun.Queries.All(query => query.Result?.SourceTotalAuthoritative == true)
                && recipeRun.Queries.All(query => query.Freshness?.FreshnessState == "clean");
            recipeNodes.Add(new JsonObject
            {
                ["name"] = recipeRun.Recipe.Name,
                ["status"] = recipeRun.Status,
                ["scope"] = recipeRun.Scope == null
                    ? null
                    : JsonSerializer.SerializeToNode(
                        recipeRun.Scope,
                        CliJsonSerializerContextFactory.Create(serializationOptions).SearchRecipeScopeJsonResult),
                ["selected_query_count"] = recipeRun.Recipe.Queries.Count,
                ["completed_query_count"] = recipeRun.Queries.Count(query => query.Status is "completed" or "previously_accounted"),
                ["previously_accounted_query_count"] = recipeRun.Queries.Count(query => query.Status == "previously_accounted"),
                ["failed_query_count"] = recipeRun.Queries.Count(query => query.Status == "failed"),
                ["omitted_query_count"] = recipeRun.OmittedQueryCount,
                ["minimum_matched_result_count"] = recipeRun.Queries.Sum(query => query.Result?.MinimumMatchedCount ?? 0),
                ["emitted_result_count"] = recipeRun.Queries.Sum(query => query.Result?.Results.Count ?? 0),
                ["minimum_omitted_result_count"] = recipeRun.Queries.Sum(query => (query.Result?.MinimumOmittedResultCount ?? 0) + query.ByteOmittedResultCount + query.DetailOmittedResultCount),
                ["count_authoritative"] = recipeCountAuthoritative,
                ["count_approximate"] = !recipeCountAuthoritative,
                ["omitted_reason"] = recipeRun.OmittedReason,
                ["queries"] = queryNodes,
            });
        }

        var selectedNames = new JsonArray();
        foreach (var recipe in state.SelectedRecipes)
            selectedNames.Add(recipe.Name);
        var errorEntries = new JsonArray();
        foreach (var error in state.Errors)
            errorEntries.Add(error.DeepClone());
        var diagnostics = new JsonArray();
        foreach (var diagnostic in state.RegistryDiagnostics)
            diagnostics.Add(diagnostic);
        var incompleteRecipes = state.Recipes
            .Where(recipe => recipe.Status is "partial" or "failed" or "omitted"
                             || recipe.Queries.Any(query => query.ByteOmittedResultCount > 0 || query.Result?.Truncated == true))
            .DistinctBy(recipe => recipe.Recipe.Name, StringComparer.Ordinal)
            .ToList();
        var returnedRecoveryRecipes = incompleteRecipes
            .Take(AuditAllRecoveryCommandLimit)
            .ToList();
        var recoveryCommands = new JsonArray();
        foreach (var recipeRun in returnedRecoveryRecipes)
            recoveryCommands.Add(BuildAuditAllRecoveryCommand(recipeRun.Recipe.Name, options));
        var freshnessStates = state.Recipes
            .SelectMany(recipe => recipe.Queries)
            .Select(query => query.Freshness)
            .Where(freshness => freshness != null)
            .Cast<SearchRecipeQueryFreshnessStateJsonResult>()
            .ToList();
        var aggregateFreshnessState = freshnessStates.Any(freshness => freshness.FreshnessState == "invalid")
            ? "invalid"
            : freshnessStates.Any(freshness => freshness.FreshnessState == "stale")
                ? "stale"
                : freshnessStates.Count > 0
                    ? "clean"
                    : state.IndexState;
        var countAuthoritative = string.Equals(state.IndexState, "current", StringComparison.Ordinal)
            && state.Errors.Count == 0
            && !state.Cancelled
            && !state.TimeBudgetExceeded
            && !state.ResultLimitReached
            && state.ByteOmittedResultCount == 0
            && state.Recipes.All(recipe => recipe.Queries.All(query => query.Result?.SourceTotalAuthoritative == true))
            && freshnessStates.All(freshness => freshness.FreshnessState == "clean");

        return new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["mode"] = "all_recipes",
            ["recipe_semantics"] = "each_registered_recipe_selected_once_budgeted_execution_including_composites",
            ["cross_recipe_deduplication"] = false,
            ["selected_recipe_count"] = state.SelectedRecipes.Count,
            ["selected_recipe_names"] = selectedNames,
            ["summary"] = new JsonObject
            {
                ["completed_recipe_count"] = state.Recipes.Count(recipe => recipe.Status == "completed"),
                ["failed_recipe_count"] = state.Recipes.Count(recipe => recipe.Status == "failed"),
                ["partial_recipe_count"] = state.Recipes.Count(recipe => recipe.Status == "partial"),
                ["omitted_recipe_count"] = state.Recipes.Count(recipe => recipe.Status == "omitted"),
                ["emitted_result_count"] = state.EmittedResultCount,
                ["minimum_matched_result_count"] = state.Recipes.Sum(recipe => recipe.Queries.Sum(query => query.Result?.MinimumMatchedCount ?? 0)),
                ["minimum_omitted_result_count"] = state.Recipes.Sum(recipe => recipe.Queries.Sum(query => (query.Result?.MinimumOmittedResultCount ?? 0) + query.ByteOmittedResultCount + query.DetailOmittedResultCount)),
                ["count_semantics"] = "sum_of_recipe_query_observations_not_unique_matches",
                ["count_scope"] = "current_page_evaluated_candidate_windows_not_cumulative",
                ["count_authoritative"] = countAuthoritative,
                ["count_approximate"] = !countAuthoritative,
                ["truncated"] = !AuditExecutionComplete(state) || AuditHasObservationOmissions(state),
                ["execution_complete"] = AuditExecutionComplete(state),
                ["observation_emission_complete"] = AuditExecutionComplete(state) && !AuditHasObservationOmissions(state),
                ["intentional_selection_omitted_count"] = state.Recipes.Sum(recipe => recipe.Queries.Sum(query => query.Result?.SelectorOmittedCount ?? 0)),
                ["cancelled"] = state.Cancelled,
                ["time_budget_exceeded"] = state.TimeBudgetExceeded,
                ["query_freshness"] = new JsonObject
                {
                    ["state"] = aggregateFreshnessState,
                    ["index_state"] = state.IndexState,
                    ["index_reason"] = state.IndexReason,
                    ["clean_query_count"] = freshnessStates.Count(freshness => freshness.FreshnessState == "clean"),
                    ["stale_query_count"] = freshnessStates.Count(freshness => freshness.FreshnessState == "stale"),
                    ["invalid_query_count"] = freshnessStates.Count(freshness => freshness.FreshnessState == "invalid"),
                    ["omitted_query_count"] = state.Recipes.Sum(recipe => recipe.OmittedQueryCount),
                },
            },
            ["limits"] = new JsonObject
            {
                ["limit_per_query"] = options.Limit,
                ["requested_total_limit"] = options.TotalLimit,
                ["effective_total_limit"] = state.EffectiveTotalLimit,
                ["total_limit_defaulted"] = !options.TotalLimit.HasValue,
                ["candidate_rows_per_query"] = AuditAllCandidateRowsPerQuery,
                ["time_budget_ms"] = (long)state.TimeBudget.TotalMilliseconds,
                ["elapsed_ms"] = state.ElapsedMilliseconds,
                ["requested_max_json_bytes"] = options.RequestedMaxJsonBytes,
                ["effective_max_json_bytes"] = options.MaxJsonBytes ?? DefaultAuditAllJsonByteLimit,
                ["max_json_bytes_defaulted"] = !options.MaxJsonBytes.HasValue,
                ["byte_omitted_result_count"] = state.ByteOmittedResultCount,
                ["byte_budget_reached_during_accumulation"] = state.ByteBudgetReached,
            },
            ["recipe_source_diagnostics"] = diagnostics,
            ["errors"] = new JsonObject
            {
                ["count"] = state.Errors.Count + state.OmittedErrorCount,
                ["returned"] = state.Errors.Count,
                ["omitted_count"] = state.OmittedErrorCount,
                ["entries"] = errorEntries,
            },
            ["query_details"] = new JsonObject
            {
                ["returned"] = emittedQueryDetails,
                ["omitted_count"] = omittedQueryDetails,
                ["limit"] = AuditAllQueryDetailLimit,
                ["omitted_result_count"] = state.DetailOmittedResultCount,
            },
            ["recovery"] = new JsonObject
            {
                ["guidance"] = "Prefer continuation.next_command to resume accounted observations. The recipe commands below restart their recipes and can repeat observations. --allow-partial changes exit 11 to 0 without changing completeness.",
                ["returned"] = returnedRecoveryRecipes.Count,
                ["omitted_count"] = incompleteRecipes.Count - returnedRecoveryRecipes.Count,
                ["limit"] = AuditAllRecoveryCommandLimit,
                ["truncated"] = incompleteRecipes.Count > returnedRecoveryRecipes.Count,
                ["next_commands"] = recoveryCommands,
            },
            ["recipes"] = recipeNodes,
            ["continuation"] = BuildAuditContinuation(options, state),
        };
    }

    private static string BuildAuditAllRecoveryCommand(string recipeName, QueryCommandOptions options, bool includeDb = true)
    {
        var args = new List<string>();
        options.InvocationContext.AddRecipeCommandPrefix(args, recipeName);
        args.Add("--format");
        args.Add(OutputFormatCompact);
        AddReplayValueOption(args, "--limit", options.Limit.ToString(CultureInfo.InvariantCulture));
        if (includeDb && options.DbPathExplicit)
            AddReplayValueOption(args, "--db", options.DbPath);
        if (options.SourceOnly)
            args.Add("--source-only");
        else if (options.AuditScopeExplicit)
            AddReplayValueOption(args, "--audit-scope", options.AuditScope);
        if (!string.IsNullOrWhiteSpace(options.Lang))
            AddReplayValueOption(args, "--lang", options.Lang);
        if (options.AllowUnknownLang)
            args.Add("--allow-unknown-lang");
        foreach (var pathPattern in options.PathPatterns)
            AddReplayValueOption(args, "--path", pathPattern);
        foreach (var excludePath in options.ExcludePaths)
            AddReplayValueOption(args, "--exclude-path", excludePath);
        if (options.ExcludeTests)
            args.Add("--exclude-tests");
        if (options.IncludeGenerated)
            args.Add("--include-generated");
        if (options.ShowExcluded)
            args.Add("--show-excluded");
        if (options.Since.HasValue)
            AddReplayValueOption(args, "--since", options.Since.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (options.NoDedup)
            args.Add("--no-dedup");
        if (options.NoVisibilityRank)
            args.Add("--no-visibility-rank");
        if (options.Exact)
            args.Add("--exact");
        if (options.ExactSubstring)
            args.Add("--exact-substring");
        AddSearchRecipeRowSelectionReplayOptions(args, options);
        foreach (var guardFilter in options.GuardFilters)
            AddReplayValueOption(args, BuildSearchGuardReplayOptionName(guardFilter), guardFilter.Query);
        if (options.GuardFilters.Count > 0 && options.GuardWindow != DbReader.DefaultSearchGuardWindow)
            AddReplayValueOption(args, "--guard-window", options.GuardWindow.ToString(CultureInfo.InvariantCulture));
        if (options.GuardFilters.Count > 0 && options.GuardScope != SearchGuardScope.Window)
            AddReplayValueOption(args, "--guard-scope", FormatSearchGuardScope(options.GuardScope));
        if (options.ExcludeComments)
            args.Add("--exclude-comments");
        if (options.ExcludeStrings)
            args.Add("--exclude-strings");
        if (options.ExcludeFixtures)
            args.Add("--exclude-fixtures");
        foreach (var origin in options.MatchOrigins)
            AddReplayValueOption(args, "--origin", origin);
        foreach (var origin in options.ExcludeOrigins)
            AddReplayValueOption(args, "--exclude-origin", origin);
        foreach (var kind in options.ResultKinds)
            AddReplayValueOption(args, "--result-kind", kind);
        AddReplayValueOption(args, "--snippet-lines", options.SnippetLines.ToString(CultureInfo.InvariantCulture));
        AddReplayValueOption(args, "--snippet-focus", FormatSearchSnippetFocusMode(options.SnippetFocus));
        AddReplayValueOption(args, "--max-line-width", options.MaxLineWidth.ToString(CultureInfo.InvariantCulture));
        return string.Join(" ", args.Select(QuoteReplayShellArg));
    }

    private static JsonObject BuildAuditAllQueryNode(
        string recipeName,
        AuditAllQueryRun queryRun,
        bool compact,
        bool includeResults,
        JsonSerializerOptions jsonOptions)
    {
        var result = queryRun.Result;
        var results = new JsonArray();
        if (includeResults && result != null)
        {
            foreach (var row in result.Results)
                results.Add(BuildAuditAllResultNode(recipeName, queryRun.Query.Name, row, compact, jsonOptions));
        }
        return new JsonObject
        {
            ["name"] = queryRun.Query.Name,
            ["status"] = queryRun.Status,
            ["failure_reason"] = queryRun.FailureReason,
            ["query_freshness"] = queryRun.Freshness == null
                ? null
                : JsonSerializer.SerializeToNode(
                    queryRun.Freshness,
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeQueryFreshnessStateJsonResult),
            ["minimum_matched_result_count"] = result?.MinimumMatchedCount ?? 0,
            ["emitted_result_count"] = result?.Results.Count ?? 0,
            ["source_total"] = result?.SourceTotal,
            ["source_total_authoritative"] = result?.SourceTotalAuthoritative ?? false,
            ["count_approximate"] = result?.SourceTotalAuthoritative != true,
            ["source_total_lower_bound"] = result?.SourceTotalAuthoritative == true ? null : result?.SourceTotal,
            ["minimum_omitted_result_count"] = (result?.MinimumOmittedResultCount ?? 0) + queryRun.ByteOmittedResultCount,
            ["truncated"] = result?.Truncated == true || result?.CandidateWindowExhausted == true || queryRun.ByteOmittedResultCount > 0,
            ["candidate_window_exhausted"] = result?.CandidateWindowExhausted == true,
            ["row_offset"] = queryRun.RowOffset,
            ["intentional_selection_omitted_count"] = result?.SelectorOmittedCount ?? 0,
            ["results"] = results,
        };
    }

    private static JsonObject BuildAuditAllResultNode(
        string recipeName,
        string queryName,
        CompactSearchResult result,
        bool compact,
        JsonSerializerOptions jsonOptions)
    {
        var full = BuildRecipeSearchResultRow(recipeName, queryName, result, jsonOptions);
        if (!compact)
            return full;
        var compactRow = new JsonObject();
        foreach (var name in new[]
                 {
                     "path", "lang", "focus_line", "focus_column", "enclosing_symbol_name",
                     "enclosing_symbol_kind", "match_lines", "recipe", "query_name",
                 })
        {
            if (full.TryGetPropertyValue(name, out var value))
                compactRow[name] = value?.DeepClone();
        }
        return compactRow;
    }

    private static int GetAuditAllExitCode(QueryCommandOptions options, AuditAllRunState state)
    {
        if (state.Cancelled)
            return CommandExitCodes.CancelledBySignal;
        var partial = state.TimeBudgetExceeded
            || !AuditExecutionComplete(state)
            || AuditHasObservationOmissions(state)
            || state.Errors.Count > 0
            || state.OmittedErrorCount > 0
            || state.ByteOmittedResultCount > 0;
        return partial && !options.AllowPartial
            ? CommandExitCodes.PartialResult
            : CommandExitCodes.Success;
    }

    private static void WriteAuditAllText(QueryCommandOptions options, AuditAllRunState state)
    {
        Console.WriteLine($"Audit all: {state.SelectedRecipes.Count.ToString(CultureInfo.InvariantCulture)} recipes selected");
        foreach (var recipe in state.Recipes)
        {
            var matched = recipe.Queries.Sum(query => query.Result?.MinimumMatchedCount ?? 0);
            var emitted = recipe.Queries.Sum(query => query.Result?.Results.Count ?? 0);
            Console.WriteLine($"[{recipe.Status}] {recipe.Recipe.Name}: matched>={matched.ToString(CultureInfo.InvariantCulture)}, emitted={emitted.ToString(CultureInfo.InvariantCulture)}");
            foreach (var query in recipe.Queries)
            {
                if (query.Result == null)
                    continue;
                foreach (var result in query.Result.Results)
                    Console.WriteLine($"  {recipe.Recipe.Name}/{query.Query.Name} {result.Path}:{result.ChunkStartLine.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        CommandErrorWriter.WriteStderr(
            $"({state.EmittedResultCount.ToString(CultureInfo.InvariantCulture)} emitted recipe-query observations; counts are not cross-recipe unique)");
        Console.WriteLine($"Execution complete: {AuditExecutionComplete(state)}; observation emission complete: {AuditExecutionComplete(state) && !AuditHasObservationOmissions(state)}");
        var continuation = BuildAuditContinuation(options, state);
        if (continuation["next_command"] is { } command) Console.WriteLine($"Continue: {command.GetValue<string>()}");
        foreach (var fallback in continuation["fallbacks"]!.AsArray())
            Console.WriteLine($"Fallback ({fallback!["reason"]}): {fallback["command"]}");
    }
}
