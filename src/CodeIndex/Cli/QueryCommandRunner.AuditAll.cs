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
    internal static TimeSpan? AuditAllTimeBudgetForTesting { get; set; }

    private static readonly TimeSpan DefaultAuditAllTimeBudget = TimeSpan.FromMinutes(5);

    private sealed class AuditAllRecipeRun(SearchAuditRecipe recipe)
    {
        internal SearchAuditRecipe Recipe { get; } = recipe;
        internal string Status { get; set; } = "completed";
        internal string? OmittedReason { get; set; }
        internal List<AuditAllQueryRun> Queries { get; } = [];
        internal int OmittedQueryCount { get; set; }
    }

    private sealed class AuditAllQueryRun(SearchAuditRecipeQuery query)
    {
        internal SearchAuditRecipeQuery Query { get; } = query;
        internal string Status { get; set; } = "completed";
        internal string? FailureReason { get; set; }
        internal SearchRecipeQueryResultJsonResult? Result { get; set; }
        internal int ByteOmittedResultCount { get; set; }
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
        internal bool Cancelled { get; set; }
        internal bool TimeBudgetExceeded { get; set; }
        internal bool ResultLimitReached { get; set; }
        internal long ElapsedMilliseconds { get; set; }
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
        Action? afterQueryForTesting = null)
        => RunAuditAll(
            subArgs,
            jsonOptions,
            cancellationToken,
            new SearchAuditRecipeRegistry(recipes, []),
            afterQueryForTesting);

    private static int RunAuditAll(
        string[] subArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        SearchAuditRecipeRegistry registry,
        Action? afterQueryForTesting = null)
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

        var normalizedArgs = AddAuditAllSummaryFormatIfNeeded(subArgs);
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
            afterQueryForTesting);
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
        Action? afterQueryForTesting)
    {
        var effectiveTotalLimit = options.TotalLimit ?? DefaultAuditAllTotalLimit;
        var timeBudget = AuditAllTimeBudgetForTesting ?? DefaultAuditAllTimeBudget;
        var state = new AuditAllRunState(selectedRecipes, registryDiagnostics, effectiveTotalLimit, timeBudget);
        if (cancellationToken.IsCancellationRequested)
        {
            state.Cancelled = true;
            AddOmittedAuditAllRecipes(state, 0, "cancelled");
            return WriteAuditAllOutput(options, jsonOptions, state);
        }
        return WithDb(
            options,
            jsonOptions,
            reader => ExecuteAuditAllWithReader(
                reader,
                options,
                jsonOptions,
                userExact,
                state,
                cancellationToken,
                afterQueryForTesting),
            cancellationToken: cancellationToken);
    }

    private static int ExecuteAuditAllWithReader(
        DbReader reader,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool userExact,
        AuditAllRunState state,
        CancellationToken cancellationToken,
        Action? afterQueryForTesting)
    {
        var stopwatch = Stopwatch.StartNew();
        var includeRows = !options.CountOnly && !options.SummaryOnly;
        var indexState = ResolveSearchQueryIndexFreshness(reader, options, out var indexReason);
        for (var recipeIndex = 0; recipeIndex < state.SelectedRecipes.Count; recipeIndex++)
        {
            var recipe = state.SelectedRecipes[recipeIndex];
            if (ShouldStopAuditAllAccumulation(state, stopwatch, cancellationToken, includeRows))
            {
                AddOmittedAuditAllRecipes(state, recipeIndex, GetAuditAllStopReason(state));
                break;
            }

            var recipeRun = new AuditAllRecipeRun(recipe);
            state.Recipes.Add(recipeRun);
            var scope = BuildSearchRecipeScope(recipe, options);
            var freshnessContext = BuildSearchRecipeFreshnessContext(
                recipe,
                recipe.Queries,
                indexState,
                indexReason);
            for (var queryIndex = 0; queryIndex < recipe.Queries.Count; queryIndex++)
            {
                if (ShouldStopAuditAllAccumulation(state, stopwatch, cancellationToken, includeRows))
                {
                    recipeRun.Status = "partial";
                    recipeRun.OmittedReason = GetAuditAllStopReason(state);
                    recipeRun.OmittedQueryCount = recipe.Queries.Count - queryIndex;
                    break;
                }

                var query = recipe.Queries[queryIndex];
                var queryRun = new AuditAllQueryRun(query);
                recipeRun.Queries.Add(queryRun);
                try
                {
                    var queryResults = CollectSearchRecipeQueryResults(
                        reader,
                        [query],
                        scope,
                        options,
                        userExact,
                        freshnessContext,
                        includeAuditClassifications: !options.SummaryOnly && !options.CountOnly,
                        out _,
                        out _,
                        out var observations,
                        out var hasFailures,
                        emittedBefore: includeRows ? state.MaterializedResultCount : 0,
                        aggregateResultLimit: includeRows ? state.EffectiveTotalLimit : options.Limit,
                        fetchLimitCap: AuditAllCandidateRowsPerQuery,
                        auditClassificationQueries: recipe.Queries);
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
                        state.MaterializedResultCount += queryRun.Result.Results.Count;
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
                    recipeRun.Status = "partial";
                    recipeRun.OmittedReason = "cancelled";
                    recipeRun.OmittedQueryCount = recipe.Queries.Count - queryIndex;
                    break;
                }
                catch (Exception ex)
                {
                    queryRun.Status = "failed";
                    queryRun.FailureReason = $"{ex.GetType().Name}: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}";
                    AddAuditAllError(state, recipe.Name, query.Name, queryRun.FailureReason);
                }
                finally
                {
                    afterQueryForTesting?.Invoke();
                }

                if (stopwatch.Elapsed >= state.TimeBudget)
                    state.TimeBudgetExceeded = true;
            }

            FinalizeAuditAllRecipeStatus(recipeRun);
            if (state.Cancelled || state.TimeBudgetExceeded || includeRows && state.ResultLimitReached)
            {
                AddOmittedAuditAllRecipes(state, recipeIndex + 1, GetAuditAllStopReason(state));
                break;
            }
        }

        stopwatch.Stop();
        state.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        return WriteAuditAllOutput(options, jsonOptions, state);
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
        return state.Cancelled || state.TimeBudgetExceeded || state.ResultLimitReached;
    }

    private static string GetAuditAllStopReason(AuditAllRunState state)
        => state.Cancelled
            ? "cancelled"
            : state.TimeBudgetExceeded
                ? "time_budget"
                : "total_limit";

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
            WriteAuditAllText(state);
            return GetAuditAllExitCode(options, state);
        }

        var serializationOptions = EnsureJsonNodeSerializerOptions(jsonOptions);
        var byteLimit = options.MaxJsonBytes ?? DefaultAuditAllJsonByteLimit;
        var ndjson = options.SearchFields != null
            || options.Json
            && options.JsonOutputFormatExplicit
            && options.JsonOutputFormat == JsonOutputFormatNdjson;
        var includePayloadResults = !ndjson && !options.CountOnly && !options.SummaryOnly;
        var payload = BuildAuditAllPayload(options, state, includeResults: includePayloadResults);
        var json = payload.ToJsonString(serializationOptions);
        while (GetJsonDocumentByteCount(json) > byteLimit && TryOmitLastAuditAllResult(state))
        {
            payload = BuildAuditAllPayload(options, state, includeResults: includePayloadResults);
            json = payload.ToJsonString(serializationOptions);
        }

        if (ndjson)
            return WriteAuditAllNdjson(options, jsonOptions, serializationOptions, state, byteLimit);

        if (GetJsonDocumentByteCount(json) > byteLimit)
        {
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "audit --all summary",
                "Increase --max-json-bytes or reduce the external recipe registry; the bounded summary metadata cannot be reduced further.",
                jsonOptions,
                "audit");
        }

        Console.WriteLine(json);
        return GetAuditAllExitCode(options, state);
    }

    private static int WriteAuditAllNdjson(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        JsonSerializerOptions serializationOptions,
        AuditAllRunState state,
        int byteLimit)
    {
        while (true)
        {
            var rowLines = BuildAuditAllNdjsonRows(options, state, serializationOptions);
            var terminal = BuildAuditAllPayload(options, state, includeResults: false);
            terminal["terminal_record"] = true;
            terminal["done"] = true;
            AddActiveSqliteDiagnostics(terminal);
            var terminalLine = terminal.ToJsonString(serializationOptions);
            var totalBytes = rowLines.Sum(JsonLineBytes) + JsonLineBytes(terminalLine);
            if (totalBytes <= byteLimit)
            {
                foreach (var rowLine in rowLines)
                    Console.WriteLine(rowLine);
                Console.WriteLine(terminalLine);
                return GetAuditAllExitCode(options, state);
            }

            if (!TryOmitLastAuditAllResult(state))
            {
                return CommandErrorWriter.WriteResponseBudgetError(
                    json: true,
                    jsonOptions,
                    "audit",
                    $"audit --all NDJSON terminal record exceeds the effective {byteLimit.ToString(CultureInfo.InvariantCulture)}-byte response budget.",
                    "Increase --max-json-bytes or reduce the external recipe registry; no partial NDJSON stream was written.",
                    requestedBytes: options.RequestedMaxJsonBytes ?? options.MaxJsonBytes,
                    effectiveBytes: byteLimit,
                    minimumRequiredBytes: JsonLineBytes(terminalLine),
                    recommendedBytes: Math.Min(MaxSearchJsonByteLimit, JsonLineBytes(terminalLine) + 1024),
                    usage: GetUsageLineOrThrow("audit"));
            }
        }
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

    private static bool TryOmitLastAuditAllResult(AuditAllRunState state)
    {
        for (var recipeIndex = state.Recipes.Count - 1; recipeIndex >= 0; recipeIndex--)
        {
            var recipeRun = state.Recipes[recipeIndex];
            for (var queryIndex = recipeRun.Queries.Count - 1; queryIndex >= 0; queryIndex--)
            {
                var queryRun = recipeRun.Queries[queryIndex];
                if (queryRun.Result is not { Results.Count: > 0 })
                    continue;
                queryRun.Result.Results.RemoveAt(queryRun.Result.Results.Count - 1);
                queryRun.ByteOmittedResultCount++;
                state.ByteOmittedResultCount++;
                state.EmittedResultCount--;
                return true;
            }
        }
        return false;
    }

    private static JsonObject BuildAuditAllPayload(
        QueryCommandOptions options,
        AuditAllRunState state,
        bool includeResults)
    {
        var recipeNodes = new JsonArray();
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
                    EnsureJsonNodeSerializerOptions(options.InvocationJsonOptions!)));
                emittedQueryDetails++;
            }
            omittedQueryDetails += Math.Max(0, recipeRun.Recipe.Queries.Count - recipeRun.Queries.Count);
            recipeNodes.Add(new JsonObject
            {
                ["name"] = recipeRun.Recipe.Name,
                ["status"] = recipeRun.Status,
                ["selected_query_count"] = recipeRun.Recipe.Queries.Count,
                ["completed_query_count"] = recipeRun.Queries.Count(query => query.Status == "completed"),
                ["failed_query_count"] = recipeRun.Queries.Count(query => query.Status == "failed"),
                ["omitted_query_count"] = recipeRun.OmittedQueryCount,
                ["minimum_matched_result_count"] = recipeRun.Queries.Sum(query => query.Result?.MinimumMatchedCount ?? 0),
                ["emitted_result_count"] = recipeRun.Queries.Sum(query => query.Result?.Results.Count ?? 0),
                ["minimum_omitted_result_count"] = recipeRun.Queries.Sum(query => (query.Result?.MinimumOmittedResultCount ?? 0) + query.ByteOmittedResultCount),
                ["count_authoritative"] = recipeRun.Status == "completed"
                    && recipeRun.Queries.All(query => query.Result?.SourceTotalAuthoritative == true),
                ["count_approximate"] = recipeRun.Status != "completed"
                    || recipeRun.Queries.Any(query => query.Result?.SourceTotalAuthoritative != true),
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
        var omittedRecipes = state.Recipes.Where(recipe => recipe.Status == "omitted").Select(recipe => recipe.Recipe.Name).Take(3).ToList();
        var recoveryCommands = new JsonArray();
        foreach (var recipeName in omittedRecipes)
            recoveryCommands.Add($"cdidx audit {recipeName} --format compact --limit {options.Limit.ToString(CultureInfo.InvariantCulture)}");

        return new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["mode"] = "all_recipes",
            ["recipe_semantics"] = "each_registered_recipe_executes_once_including_composites",
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
                ["minimum_omitted_result_count"] = state.Recipes.Sum(recipe => recipe.Queries.Sum(query => (query.Result?.MinimumOmittedResultCount ?? 0) + query.ByteOmittedResultCount)),
                ["count_semantics"] = "sum_of_recipe_query_observations_not_unique_matches",
                ["count_authoritative"] = state.Errors.Count == 0
                    && !state.Cancelled
                    && !state.TimeBudgetExceeded
                    && !state.ResultLimitReached
                    && state.ByteOmittedResultCount == 0
                    && state.Recipes.All(recipe => recipe.Queries.All(query => query.Result?.SourceTotalAuthoritative == true)),
                ["count_approximate"] = state.Errors.Count > 0
                    || state.Cancelled
                    || state.TimeBudgetExceeded
                    || state.ResultLimitReached
                    || state.ByteOmittedResultCount > 0
                    || state.Recipes.Any(recipe => recipe.Queries.Any(query => query.Result?.SourceTotalAuthoritative != true)),
                ["truncated"] = state.ResultLimitReached || state.ByteOmittedResultCount > 0,
                ["cancelled"] = state.Cancelled,
                ["time_budget_exceeded"] = state.TimeBudgetExceeded,
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
            },
            ["recovery"] = new JsonObject
            {
                ["guidance"] = "Increase --total-limit or --max-json-bytes, narrow with shared filters, or run an omitted recipe individually. --allow-partial changes exit 11 to 0 but does not make omitted work complete.",
                ["next_commands"] = recoveryCommands,
            },
            ["recipes"] = recipeNodes,
        };
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
            ["minimum_matched_result_count"] = result?.MinimumMatchedCount ?? 0,
            ["emitted_result_count"] = result?.Results.Count ?? 0,
            ["source_total"] = result?.SourceTotal,
            ["source_total_authoritative"] = result?.SourceTotalAuthoritative ?? false,
            ["count_approximate"] = result?.SourceTotalAuthoritative != true,
            ["source_total_lower_bound"] = result?.SourceTotalAuthoritative == true ? null : result?.SourceTotal,
            ["minimum_omitted_result_count"] = (result?.MinimumOmittedResultCount ?? 0) + queryRun.ByteOmittedResultCount,
            ["truncated"] = result?.Truncated == true || queryRun.ByteOmittedResultCount > 0,
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
            || state.Errors.Count > 0
            || state.OmittedErrorCount > 0
            || state.ByteOmittedResultCount > 0;
        return partial && !options.AllowPartial
            ? CommandExitCodes.PartialResult
            : CommandExitCodes.Success;
    }

    private static void WriteAuditAllText(AuditAllRunState state)
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
    }
}
