using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static int WriteSearchRecipeList(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string usageCommandName)
    {
        var emitsJson = options.NamesOnly
            ? options.Json
            : options.Json || options.OutputFormat == OutputFormatCompact;
        if (options.MaxJsonBytes.HasValue && !emitsJson)
        {
            WriteUsageError(
                "--max-json-bytes is only supported with JSON recipe-list output.",
                GetUsageLineOrThrow(usageCommandName),
                "Add `--json` or `--format compact`, or remove --max-json-bytes for text recipe output.");
            return CommandExitCodes.UsageError;
        }

        var recipes = SearchAuditRecipes.All
            .Select(recipe => ToFilteredSearchRecipeListItem(recipe, options.Query))
            .OfType<SearchRecipeListItemJsonResult>()
            .ToList();
        if (options.NamesOnly)
        {
            var names = recipes
                .Select(recipe => recipe.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                    new SearchRecipeNameListJsonResult(JsonOutputContract.ApiVersion, names.Count, names),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeNameListJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "recipe-name list",
                    "Use a larger --max-json-bytes value or remove recipe filters.",
                    usageCommandName);
            }

            foreach (var name in names)
                Console.WriteLine(name);
            return CommandExitCodes.Success;
        }
        if (options.OutputFormat == OutputFormatCompact || (options.SummaryOnly && options.Json))
        {
            var compactRecipes = recipes
                .Select(recipe => ToSearchRecipeCompactListItem(recipe, recipe.Queries))
                .ToList();
            var json = JsonSerializer.Serialize(
                new SearchRecipeCompactListJsonResult(JsonOutputContract.ApiVersion, compactRecipes.Count, compactRecipes),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeCompactListJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "recipe summary",
                "Use `cdidx recipes --names --json` for the smallest recipe-list JSON.",
                usageCommandName);
        }
        if (options.SummaryOnly)
        {
            foreach (var recipe in recipes)
                Console.WriteLine($"{recipe.Name}: {recipe.Description} (queries: {recipe.Queries.Count}, scope: {recipe.DefaultScope})");
            return CommandExitCodes.Success;
        }
        if (options.Json)
        {
            var json = JsonSerializer.Serialize(
                new SearchRecipeListJsonResult(JsonOutputContract.ApiVersion, recipes.Count, recipes),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeListJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "recipe list",
                "Use `cdidx recipes --names --json` or `cdidx recipes --summary-only --json` for smaller output.",
                usageCommandName);
        }

        foreach (var recipe in recipes)
        {
            Console.WriteLine($"{recipe.Name}: {recipe.Description}");
            Console.WriteLine($"  labels: {string.Join(", ", recipe.RecommendedLabels)}");
            Console.WriteLine($"  default scope: {recipe.DefaultScope}");
            if (recipe.DefaultPathPatterns.Count > 0)
                Console.WriteLine($"  default paths: {string.Join(", ", recipe.DefaultPathPatterns)}");
            if (recipe.DefaultExcludePaths.Count > 0)
                Console.WriteLine($"  default excludes: {string.Join(", ", recipe.DefaultExcludePaths)}");
            foreach (var query in recipe.Queries)
            {
                var mode = query.ExactSubstring ? "exact-substring" : "fts";
                Console.WriteLine($"  - {query.Name}: {query.Query} ({mode})");
                Console.WriteLine($"    {query.Description}");
                Console.WriteLine($"    false positives: {query.FalsePositiveGuidance}");
                if (query.StringComparisonTaxonomy is not null)
                    Console.WriteLine($"    string comparison domains: {FormatSearchRecipeStringComparisonDomains(query.StringComparisonTaxonomy)}");
                if (query.Classifiers.Count > 0)
                    Console.WriteLine($"    classifiers: {string.Join(", ", query.Classifiers.Select(classifier => classifier.Name))}");
                if (query.BroadCatchTaxonomy is not null)
                {
                    Console.WriteLine($"    broad catch boundaries: {string.Join(", ", query.BroadCatchTaxonomy.BoundaryCategories.Select(category => category.Name))}");
                    Console.WriteLine($"    broad catch diagnostics: {string.Join(", ", query.BroadCatchTaxonomy.DiagnosticBehaviors.Select(behavior => behavior.Name))}");
                }
            }
        }

        return CommandExitCodes.Success;
    }

    private static int WriteJsonObjectWithOptionalByteLimit(
        string json,
        QueryCommandOptions options,
        string outputDescription,
        string hint,
        string commandName = "search")
    {
        json = AddActiveSqliteDiagnostics(json);
        if (options.MaxJsonBytes.HasValue)
        {
            var byteCount = Encoding.UTF8.GetByteCount(json) + Environment.NewLine.Length;
            if (byteCount > options.MaxJsonBytes.Value)
            {
                WriteUsageError(
                    $"{outputDescription} JSON output is {byteCount.ToString(CultureInfo.InvariantCulture)} bytes and exceeds --max-json-bytes {options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture)}.",
                    GetUsageLineOrThrow(commandName),
                    hint);
                return CommandExitCodes.UsageError;
            }
        }

        Console.WriteLine(json);
        return CommandExitCodes.Success;
    }

    private static int WriteJsonPayloadWithOptionalByteLimit(
        JsonObject payload,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string commandName,
        string outputDescription,
        string hint)
        => WriteJsonObjectWithOptionalByteLimit(
            payload.ToJsonString(EnsureJsonNodeSerializerOptions(jsonOptions)),
            options,
            outputDescription,
            hint,
            commandName);

    private static JsonSerializerOptions EnsureJsonNodeSerializerOptions(JsonSerializerOptions jsonOptions)
    {
        if (jsonOptions.TypeInfoResolver != null)
            return jsonOptions;

        return new JsonSerializerOptions(jsonOptions)
        {
            TypeInfoResolver = CliJsonSerializerContext.Default,
        };
    }

    private static bool ShouldEmitGraphLiveness(QueryCommandOptions options, bool machineReadable)
        => options.Verbose || (!machineReadable && options.Limit >= GraphLivenessLimitThreshold);

    private static void WriteGraphLiveness(
        string commandName,
        string phase,
        QueryCommandOptions options,
        string? format = null,
        string? groupBy = null,
        int? rows = null,
        int? cycleCount = null,
        bool machineReadable = false)
    {
        if (!ShouldEmitGraphLiveness(options, machineReadable))
            return;

        var parts = new List<string>
        {
            $"Progress: {commandName}",
            $"phase={phase}",
            $"limit={options.Limit.ToString(CultureInfo.InvariantCulture)}",
        };
        if (format != null)
            parts.Add($"format={format}");
        if (groupBy != null)
            parts.Add($"group_by={groupBy}");
        if (rows.HasValue)
            parts.Add($"rows={rows.Value.ToString(CultureInfo.InvariantCulture)}");
        if (cycleCount.HasValue)
            parts.Add($"cycles={cycleCount.Value.ToString(CultureInfo.InvariantCulture)}");
        if (options.PathPatterns.Count > 0)
            parts.Add($"path_filters={options.PathPatterns.Count.ToString(CultureInfo.InvariantCulture)}");
        if (options.ExcludePaths.Count > 0)
            parts.Add($"exclude_filters={options.ExcludePaths.Count.ToString(CultureInfo.InvariantCulture)}");
        if (options.ExcludeTests)
            parts.Add("exclude_tests=true");
        if (options.DependencyCycles)
            parts.Add("cycles=true");
        if (options.SummaryOnly)
            parts.Add("summary_only=true");
        if (options.MaxJsonBytes.HasValue)
            parts.Add($"max_json_bytes={options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture)}");

        CommandErrorWriter.WriteStderr(string.Join(" ", parts));
    }

    private static SearchRecipeListItemJsonResult? ToFilteredSearchRecipeListItem(SearchAuditRecipe recipe, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return ToSearchRecipeListItem(recipe);

        var recipeMatches = SearchRecipeMatchesFilter(recipe, filter);
        var queries = recipe.Queries
            .Where(query => recipeMatches || SearchRecipeQueryMatchesFilter(recipe, query, filter))
            .ToList();
        return recipeMatches || queries.Count > 0
            ? ToSearchRecipeListItem(recipe, queries)
            : null;
    }

    private static bool TryResolveSearchRecipeSelection(
        QueryCommandOptions options,
        out SearchRecipeSelection selection,
        out string? error)
    {
        selection = default!;
        error = null;
        var recipeSelector = options.RecipeName!;
        var recipeName = recipeSelector;
        string? directQueryName = null;
        var slash = recipeSelector.IndexOf('/');
        if (slash >= 0)
        {
            if (slash == 0 || slash == recipeSelector.Length - 1 || slash != recipeSelector.LastIndexOf('/'))
            {
                error = "--recipe child selection must use recipe/query form.";
                return false;
            }
            if (options.IncludeRecipeQueries.Count > 0 || options.ExcludeRecipeQueries.Count > 0)
            {
                error = "--recipe recipe/query cannot be combined with --include-query or --exclude-query.";
                return false;
            }

            recipeName = recipeSelector[..slash];
            directQueryName = recipeSelector[(slash + 1)..];
        }

        if (!SearchAuditRecipes.TryGet(recipeName, out var recipe))
        {
            var available = string.Join(", ", SearchAuditRecipes.All.Select(r => r.Name));
            var suggestions = BuildRecipeSelectorSuggestions(recipeSelector);
            var suggestionText = suggestions.Count > 0
                ? $" Did you mean: {string.Join(", ", suggestions)}?"
                : string.Empty;
            error = $"unknown search recipe '{recipeName}'. Available recipes: {available}.{suggestionText}";
            return false;
        }

        var queryByName = recipe.Queries.ToDictionary(query => query.Name, StringComparer.OrdinalIgnoreCase);
        var availableQueries = string.Join(", ", recipe.Queries.Select(query => query.Name));
        if (!TryValidateRecipeQuerySelectors(queryByName, availableQueries, recipe.Name, options.IncludeRecipeQueries, "--include-query", out error) ||
            !TryValidateRecipeQuerySelectors(queryByName, availableQueries, recipe.Name, options.ExcludeRecipeQueries, "--exclude-query", out error))
        {
            return false;
        }
        if (directQueryName != null && !queryByName.ContainsKey(directQueryName))
        {
            var suggestions = BuildRecipeSelectorSuggestions(directQueryName);
            var suggestionText = suggestions.Count > 0
                ? $" Suggestions across all recipes: {string.Join(", ", suggestions)}."
                : string.Empty;
            error = $"unknown recipe query '{directQueryName}' for recipe '{recipe.Name}'. Available queries: {availableQueries}.{suggestionText}";
            return false;
        }

        var selected = new List<SearchAuditRecipeQuery>();
        if (directQueryName != null)
        {
            selected.Add(queryByName[directQueryName]);
        }
        else if (options.IncludeRecipeQueries.Count > 0)
        {
            foreach (var queryName in options.IncludeRecipeQueries)
            {
                var query = queryByName[queryName];
                if (!selected.Any(existing => string.Equals(existing.Name, query.Name, StringComparison.OrdinalIgnoreCase)))
                    selected.Add(query);
            }
        }
        else
        {
            selected.AddRange(recipe.Queries);
        }

        if (options.ExcludeRecipeQueries.Count > 0)
        {
            var excludeSet = options.ExcludeRecipeQueries.ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = selected
                .Where(query => !excludeSet.Contains(query.Name))
                .ToList();
        }

        if (selected.Count == 0)
        {
            error = $"recipe query selection for '{recipe.Name}' is empty after applying --include-query/--exclude-query.";
            return false;
        }

        selection = new SearchRecipeSelection(recipe, selected);
        return true;
    }

    private static bool TryValidateRecipeQuerySelectors(
        IReadOnlyDictionary<string, SearchAuditRecipeQuery> queryByName,
        string availableQueries,
        string recipeName,
        IReadOnlyList<string> selectors,
        string optionName,
        out string? error)
    {
        foreach (var selector in selectors)
        {
            if (!queryByName.ContainsKey(selector))
            {
                error = $"unknown recipe query '{selector}' for recipe '{recipeName}' in {optionName}. Available queries: {availableQueries}.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static List<string> BuildRecipeSelectorSuggestions(string rawSelector)
    {
        var tokens = NormalizeDiscoveryTokens(rawSelector);
        if (tokens.Count == 0)
            return [];

        return SearchAuditRecipes.All
            .SelectMany(recipe => recipe.Queries.Select(query => new
            {
                Selector = $"{recipe.Name}/{query.Name}",
                Score = ScoreRecipeSelectorSuggestion(tokens, recipe, query),
            }))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Selector, StringComparer.Ordinal)
            .Take(3)
            .Select(item => item.Selector)
            .ToList();
    }

    private static int ScoreRecipeSelectorSuggestion(IReadOnlyList<string> tokens, SearchAuditRecipe recipe, SearchAuditRecipeQuery query)
    {
        var haystack = NormalizeDiscoveryText(string.Join(' ', BuildRecipeQuerySearchFields(recipe, query)));
        var score = 0;
        foreach (var token in tokens)
        {
            if (haystack.Contains(token, StringComparison.Ordinal))
                score += token == "sql" && haystack.Contains("sqlite", StringComparison.Ordinal) ? 80 : 25;
        }

        var normalizedSelector = NormalizeDiscoveryText($"{recipe.Name} {query.Name}");
        var normalizedRaw = string.Join(' ', tokens);
        if (normalizedSelector.Contains(normalizedRaw, StringComparison.Ordinal))
            score += 100;
        return score;
    }

    private static bool SearchRecipeMatchesFilter(SearchAuditRecipe recipe, string filter)
        => DiscoveryFilterMatches(filter,
            recipe.Name,
            recipe.Description,
            recipe.DefaultScope,
            string.Join(' ', recipe.RecommendedLabels),
            string.Join(' ', recipe.DefaultPathPatterns),
            string.Join(' ', recipe.DefaultExcludePaths));

    private static bool SearchRecipeQueryMatchesFilter(SearchAuditRecipe recipe, SearchAuditRecipeQuery query, string filter)
        => DiscoveryFilterMatches(filter, BuildRecipeQuerySearchFields(recipe, query));

    private static IEnumerable<string> BuildRecipeQuerySearchFields(SearchAuditRecipe? recipe, SearchAuditRecipeQuery query)
    {
        if (recipe != null)
        {
            yield return recipe.Name;
            yield return recipe.Description;
            yield return recipe.DefaultScope;
        }

        yield return query.Name;
        yield return query.Query;
        yield return query.Description;
        yield return query.FalsePositiveGuidance;
        yield return query.Severity;
        foreach (var label in query.RecommendedLabels)
            yield return label;
        foreach (var path in query.PathPatterns)
            yield return path;
        foreach (var path in query.ExcludePaths)
            yield return path;
        foreach (var origin in query.MatchOrigins)
            yield return origin;
        foreach (var origin in query.ExcludeOrigins)
            yield return origin;
        foreach (var kind in query.ResultKinds)
            yield return kind;
        foreach (var classifier in query.Classifiers)
        {
            yield return classifier.Name;
            yield return classifier.Description;
            yield return classifier.TriageGuidance;
            foreach (var category in classifier.Categories)
            {
                yield return category.Name;
                yield return category.Description;
                yield return category.ReviewGuidance;
            }
            foreach (var evidenceField in classifier.EvidenceFields)
                yield return evidenceField;
        }
    }

    private static bool DiscoveryFilterMatches(string filter, params string[] fields)
        => DiscoveryFilterMatches(filter, (IEnumerable<string>)fields);

    private static bool DiscoveryFilterMatches(string filter, IEnumerable<string> fields)
    {
        var tokens = NormalizeDiscoveryTokens(filter);
        if (tokens.Count == 0)
            return true;
        var haystack = NormalizeDiscoveryText(string.Join(' ', fields));
        return tokens.All(token => haystack.Contains(token, StringComparison.Ordinal));
    }

    private static List<string> NormalizeDiscoveryTokens(string value)
        => NormalizeDiscoveryText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string NormalizeDiscoveryText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private sealed record SearchRecipeSelection(
        SearchAuditRecipe Recipe,
        List<SearchAuditRecipeQuery> Queries);

    private static int RunSearchRecipe(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
    {
        if (!TryResolveSearchRecipeSelection(options, out var selection, out var selectionError))
        {
            WriteUsageError(
                selectionError!,
                GetUsageLineOrThrow("search"),
                "Use `cdidx search --recipe risky-code/raw-diagnostic-echo`, or `--include-query` / `--exclude-query` with a recipe name.");
            return CommandExitCodes.UsageError;
        }
        var recipe = selection.Recipe;
        var scope = BuildSearchRecipeScope(recipe, options);
        if (options.SearchCursor.HasValue && selection.Queries.Count != 1)
        {
            WriteUsageError(
                "--cursor requires exactly one selected recipe query.",
                GetUsageLineOrThrow("search"),
                "Use `--recipe recipe/query` or a single `--include-query` value with --cursor.");
            return CommandExitCodes.UsageError;
        }

        string? ndjsonTerminalLine = null;
        return WithDb(options, jsonOptions, reader =>
        {
            if (options.ResultsOnly || options.SearchFields != null || (options.Json && options.JsonOutputFormatExplicit && options.JsonOutputFormat == JsonOutputFormatNdjson))
            {
                var rowQueryResults = CollectSearchRecipeQueryResults(
                    reader,
                    selection.Queries,
                    scope,
                    options,
                    userExact,
                    out _,
                    out var rowMinimumMatchedTotal);
                var stream = WriteRecipeSearchResultRows(
                    reader,
                    recipe.Name,
                    rowQueryResults,
                    rowMinimumMatchedTotal,
                    options,
                    GetCompactJsonOptions(jsonOptions));
                ndjsonTerminalLine = stream.TerminalLine;
                return stream.ExitCode;
            }

            if (options.OutputFormat == OutputFormatCompact)
            {
                var compactQueryResults = CollectSearchRecipeCompactQueryResults(reader, selection.Queries, scope, options, userExact, out var compactTotal);
                var compactPayload = BuildSearchRecipeCompactRunPayload(
                    recipe,
                    selection.Queries,
                    scope,
                    options,
                    jsonOptions,
                    compactQueryResults,
                    compactTotal);
                var compactJson = compactPayload.ToJsonString(GetJsonNodeSerializationOptions(jsonOptions));
                return WriteJsonObjectWithOptionalByteLimit(
                    compactJson,
                    options,
                    "recipe compact",
                    "Reduce --limit or --total-limit, select one child query with --recipe <recipe>/<query>, stream rows with --json=ndjson, or increase --max-json-bytes.");
            }

            var queryResults = CollectSearchRecipeQueryResults(reader, selection.Queries, scope, options, userExact, out var total, out _);

            if (options.OutputFormat == OutputFormatSarif)
            {
                WriteSearchRecipeSarif(recipe, scope, queryResults, total, options, jsonOptions);
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                        new SearchRecipeRunJsonResult(
                            JsonOutputContract.ApiVersion,
                            ToSearchRecipeListItem(recipe, selection.Queries),
                            scope,
                            selection.Queries.Count,
                            total,
                            BuildSearchRecipeRunSummary(queryResults, options.Limit, options.TotalLimit, total),
                            queryResults),
                        CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeRunJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "recipe search",
                    "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.");
            }

            Console.WriteLine($"Recipe: {recipe.Name}");
            Console.WriteLine(recipe.Description);
            Console.WriteLine($"Scope: {scope.Name}");
            if (scope.PathPatterns.Count > 0)
                Console.WriteLine($"Paths: {string.Join(", ", scope.PathPatterns)}");
            if (scope.ExcludePaths.Count > 0)
                Console.WriteLine($"Excludes: {string.Join(", ", scope.ExcludePaths)}");
            Console.WriteLine($"Exclude tests: {scope.ExcludeTests.ToString().ToLowerInvariant()}");
            if (scope.ExcludedDiagnostics is { Count: > 0 })
            {
                Console.WriteLine("Excluded diagnostics:");
                foreach (var diagnostic in scope.ExcludedDiagnostics)
                {
                    var patterns = diagnostic.Patterns.Count == 0
                        ? string.Empty
                        : $" ({string.Join(", ", diagnostic.Patterns)})";
                    Console.WriteLine($"  - {diagnostic.Reason}: applied={diagnostic.Applied.ToString().ToLowerInvariant()}{patterns}");
                    Console.WriteLine($"    {diagnostic.Description}");
                }
            }
            Console.WriteLine();
            foreach (var queryResult in queryResults)
            {
                Console.WriteLine($"[{queryResult.Name}] {queryResult.Query}");
                Console.WriteLine(queryResult.Description);
                Console.WriteLine($"labels: {string.Join(", ", queryResult.RecommendedLabels)}");
                Console.WriteLine($"false positives: {queryResult.FalsePositiveGuidance}");
                if (queryResult.StringComparisonTaxonomy is not null)
                    Console.WriteLine($"string comparison domains: {FormatSearchRecipeStringComparisonDomains(queryResult.StringComparisonTaxonomy)}");
                if (queryResult.BroadCatchTaxonomy is not null)
                {
                    Console.WriteLine($"broad catch boundaries: {string.Join(", ", queryResult.BroadCatchTaxonomy.BoundaryCategories.Select(category => category.Name))}");
                    Console.WriteLine($"broad catch diagnostics: {string.Join(", ", queryResult.BroadCatchTaxonomy.DiagnosticBehaviors.Select(behavior => behavior.Name))}");
                }
                Console.WriteLine($"results: {queryResult.Count}");
                foreach (var result in queryResult.Results)
                {
                    Console.WriteLine($"{result.Path}:{result.ChunkStartLine}-{result.ChunkEndLine}");
                    foreach (var line in result.Snippet.Split('\n', StringSplitOptions.None))
                        Console.WriteLine($"  {line}");
                }
                Console.WriteLine();
            }

            CommandErrorWriter.WriteStderr($"({total} recipe results across {selection.Queries.Count} queries)");
            return CommandExitCodes.Success;
        }, _ =>
        {
            if (ndjsonTerminalLine != null && !options.ResultsOnly)
                Console.WriteLine(ndjsonTerminalLine);
        });
    }

    private static void WriteSearchRecipeSarif(
        SearchAuditRecipe recipe,
        SearchRecipeScopeJsonResult scope,
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        int total,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        var summary = BuildSearchRecipeRunSummary(queryResults, options.Limit, options.TotalLimit, total);
        var querySummaries = new JsonArray();
        foreach (var queryResult in queryResults)
        {
            querySummaries.Add(new JsonObject
            {
                ["name"] = queryResult.Name,
                ["result_count"] = queryResult.Count,
                ["result_limit"] = queryResult.ResultLimit,
                ["truncated"] = queryResult.Truncated,
                ["minimum_omitted_result_count"] = queryResult.MinimumOmittedResultCount,
                ["next_cursor"] = queryResult.NextCursor,
            });
        }
        var runProperties = new JsonObject
        {
            ["format"] = "audit-recipe",
            ["recipe"] = recipe.Name,
            ["scope"] = JsonSerializer.SerializeToNode(
                scope,
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeScopeJsonResult),
            ["query_count"] = queryResults.Count,
            ["result_count"] = total,
            ["limit_per_query"] = options.Limit,
            ["total_limit"] = JsonValue.Create(options.TotalLimit),
            ["queries"] = querySummaries,
            ["truncation"] = new JsonObject
            {
                ["truncated"] = summary.TruncatedQueryCount > 0,
                ["truncated_query_count"] = summary.TruncatedQueryCount,
                ["minimum_omitted_result_count"] = summary.MinimumOmittedResultCount,
            },
        };
        var items = new List<SarifLocation>(total);
        foreach (var queryResult in queryResults)
        {
            var ruleId = $"{recipe.Name}/{queryResult.Name}";
            var level = GetSearchRecipeSarifLevel(queryResult.Severity);
            var confidence = GetSearchRecipeConfidence(queryResult.Count);
            var descriptor = new SarifRuleDescriptor(
                queryResult.Name,
                queryResult.Description,
                $"Audit recipe {recipe.Name}: {queryResult.Description}",
                $"{queryResult.FalsePositiveGuidance} Review the referenced location and surrounding code before filing or acting on this result.",
                ["cdidx", "audit-recipe", recipe.Name, .. queryResult.RecommendedLabels]);
            foreach (var result in queryResult.Results)
            {
                var (line, column, endColumn) = GetSearchRecipeSarifRegion(result);
                var fingerprint = BuildSearchRecipeSarifFingerprint(recipe.Name, queryResult.Name, result.Path, line, column);
                var properties = new JsonObject
                {
                    ["stable_result_id"] = fingerprint,
                    ["recipe"] = recipe.Name,
                    ["query_name"] = queryResult.Name,
                    ["query"] = queryResult.Query,
                    ["severity"] = queryResult.Severity,
                    ["confidence"] = confidence,
                    ["query_result_count"] = queryResult.Count,
                    ["query_truncated"] = queryResult.Truncated,
                    ["result_limit"] = queryResult.ResultLimit,
                    ["minimum_omitted_result_count"] = queryResult.MinimumOmittedResultCount,
                };
                if (result.MatchOrigins.Count > 0)
                    properties["match_origins"] = ToSarifJsonArray(result.MatchOrigins);
                if (result.ResultKinds.Count > 0)
                    properties["result_kinds"] = ToSarifJsonArray(result.ResultKinds);

                items.Add(new SarifLocation(
                    result.Path,
                    line,
                    column,
                    endColumn,
                    $"{ruleId}: {queryResult.Description}",
                    ruleId,
                    level,
                    properties,
                    fingerprint,
                    descriptor));
            }
        }

        WriteSarif(items, jsonOptions, runProperties: runProperties);
    }

    private static (int Line, int Column, int? EndColumn) GetSearchRecipeSarifRegion(CompactSearchResult result)
    {
        var facet = result.MatchFacets
            .Where(candidate => candidate.Line > 0 && candidate.Column > 0)
            .OrderBy(candidate => candidate.Line)
            .ThenBy(candidate => candidate.Column)
            .ThenBy(candidate => candidate.Length)
            .FirstOrDefault();
        var firstMatchLine = result.MatchLines
            .Where(candidate => candidate > 0)
            .DefaultIfEmpty()
            .Min();
        var line = Math.Max(
            1,
            facet?.Line
                ?? (firstMatchLine > 0
                    ? firstMatchLine
                    : result.FocusLine
                        ?? (result.SnippetStartLine > 0
                            ? result.SnippetStartLine
                            : result.ChunkStartLine)));
        var column = Math.Max(1, facet?.Column ?? (result.FocusLine == line ? result.FocusColumn ?? 1 : 1));
        var endColumn = facet is { Length: > 0 } && facet.Line == line && facet.Column == column
            ? column + facet.Length
            : (int?)null;
        return (line, column, endColumn);
    }

    private static string BuildSearchRecipeSarifFingerprint(
        string recipeName,
        string queryName,
        string path,
        int line,
        int column)
    {
        var identity = string.Join(
            '\0',
            "cdidx-sarif-v1",
            recipeName,
            queryName,
            NormalizeSarifArtifactUri(path),
            line.ToString(CultureInfo.InvariantCulture),
            column.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static string GetSearchRecipeSarifLevel(string severity)
        => severity switch
        {
            "critical" or "high" => "error",
            "low" or "info" => "note",
            _ => "warning",
        };

    private static JsonArray ToSarifJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Distinct(StringComparer.Ordinal))
            array.Add(value);
        return array;
    }

    private static JsonObject BuildSearchRecipeCompactRunPayload(
        SearchAuditRecipe recipe,
        IReadOnlyList<SearchAuditRecipeQuery> selectedQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        List<SearchRecipeCompactQueryResultJsonResult> compactQueryResults,
        int compactTotal)
    {
        var run = new SearchRecipeCompactRunJsonResult(
            JsonOutputContract.ApiVersion,
            new SearchRecipeCompactListItemJsonResult(
                recipe.Name,
                recipe.Description,
                recipe.DefaultScope,
                selectedQueries.Count,
                recipe.RecommendedLabels,
                recipe.DefaultPathPatterns,
                recipe.DefaultExcludePaths),
            scope,
            selectedQueries.Count,
            compactTotal,
            BuildSearchRecipeRunSummary(compactQueryResults, options.Limit, options.TotalLimit, compactTotal),
            compactQueryResults);
        var payload = JsonSerializer.SerializeToNode(
            run,
            CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeCompactRunJsonResult)!.AsObject();
        payload["compact"] = true;
        AddJsonByteLimitField(payload, options);
        payload["truncation"] = BuildSearchRecipeCompactTruncationMetadata(compactQueryResults, options);
        payload["next_commands"] = BuildSearchRecipeCompactNextCommands(recipe.Name, compactQueryResults, options);
        return payload;
    }

    private static JsonObject BuildSearchRecipeCompactTruncationMetadata(
        IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults,
        QueryCommandOptions options)
    {
        var queries = new JsonArray();
        var truncatedQueryCount = 0;
        var emittedResultCount = 0;
        var minimumMatchedResultCount = 0;
        var minimumOmittedResultCount = 0;
        foreach (var query in queryResults)
        {
            if (query.Truncated)
                truncatedQueryCount++;
            emittedResultCount += query.EmittedCount;
            minimumMatchedResultCount += query.MinimumMatchedCount;
            minimumOmittedResultCount += query.MinimumOmittedResultCount;
            queries.Add(new JsonObject
            {
                ["name"] = query.Name,
                ["returned"] = query.Results.Count,
                ["emitted_count"] = query.EmittedCount,
                ["minimum_matched_count"] = query.MinimumMatchedCount,
                ["minimum_omitted_result_count"] = query.MinimumOmittedResultCount,
                ["result_limit"] = query.ResultLimit,
                ["truncated"] = query.Truncated,
                ["next_cursor"] = query.NextCursor,
            });
        }

        var metadata = new JsonObject
        {
            ["selected_query_count"] = queryResults.Count,
            ["limit_per_query"] = options.Limit,
            ["total_limit"] = options.TotalLimit,
            ["emitted_result_count"] = emittedResultCount,
            ["minimum_matched_result_count"] = minimumMatchedResultCount,
            ["minimum_omitted_result_count"] = minimumOmittedResultCount,
            ["truncated_query_count"] = truncatedQueryCount,
            ["queries"] = queries,
        };
        if (options.MaxJsonBytes.HasValue)
            metadata["aggregate_byte_limit"] = options.MaxJsonBytes.Value;
        return metadata;
    }

    private static JsonArray BuildSearchRecipeCompactNextCommands(
        string recipeName,
        IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults,
        QueryCommandOptions options)
    {
        var commands = new JsonArray();
        foreach (var query in queryResults.Where(query => query.NextCursor != null).Take(3))
        {
            commands.Add(BuildSearchRecipeCompactReplayCommand(
                $"{recipeName}/{query.Name}",
                options,
                query.NextCursor,
                resultsOnly: false,
                includeRecipeQuerySelectors: false));
        }

        if (commands.Count == 0 && queryResults.Count > 1)
        {
            commands.Add(BuildSearchRecipeCompactReplayCommand(
                $"{recipeName}/{queryResults[0].Name}",
                options,
                cursor: null,
                resultsOnly: false,
                includeRecipeQuerySelectors: false));
        }
        var resultsOnlySelector = queryResults.Count == 1
            ? $"{recipeName}/{queryResults[0].Name}"
            : recipeName;
        commands.Add(BuildSearchRecipeCompactReplayCommand(
            resultsOnlySelector,
            options,
            cursor: null,
            resultsOnly: true,
            includeRecipeQuerySelectors: queryResults.Count != 1));
        return commands;
    }

    private static string BuildSearchRecipeCompactReplayCommand(
        string recipeSelector,
        QueryCommandOptions options,
        string? cursor,
        bool resultsOnly,
        bool includeRecipeQuerySelectors)
    {
        var args = new List<string>
        {
            "cdidx",
            "search",
            "--recipe",
            recipeSelector,
        };
        if (resultsOnly)
        {
            args.Add("--json=ndjson");
            args.Add("--results-only");
        }
        else
        {
            args.Add("--format");
            args.Add(OutputFormatCompact);
        }
        if (!string.IsNullOrWhiteSpace(cursor))
            AddReplayValueOption(args, "--cursor", cursor);
        AddReplayValueOption(args, "--limit", options.Limit.ToString(CultureInfo.InvariantCulture));
        AddSearchRecipeCompactReplayOptions(args, options, includeRecipeQuerySelectors);
        var command = string.Join(" ", args.Select(QuoteReplayShellArg));
        return resultsOnly && !options.MaxJsonBytes.HasValue
            ? command + " --max-json-bytes <bytes>"
            : command;
    }

    private static void AddSearchRecipeCompactReplayOptions(List<string> args, QueryCommandOptions options, bool includeRecipeQuerySelectors)
    {
        if (options.DbPathExplicit)
            AddReplayValueOption(args, "--db", options.DbPath);
        if (options.SourceOnly)
            args.Add("--source-only");
        else if (options.AuditScopeExplicit)
            AddReplayValueOption(args, "--audit-scope", options.AuditScope);
        if (!string.IsNullOrWhiteSpace(options.Lang))
            AddReplayValueOption(args, "--lang", options.Lang);
        foreach (var pathPattern in options.PathPatterns)
            AddReplayValueOption(args, "--path", pathPattern);
        foreach (var excludePath in options.ExcludePaths)
            AddReplayValueOption(args, "--exclude-path", excludePath);
        if (options.ExcludeTests)
            args.Add("--exclude-tests");
        if (options.IncludeGenerated)
            args.Add("--include-generated");
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
        if (options.TokenBoundary)
            args.Add("--token-boundary");
        if (options.Prefix)
            args.Add("--prefix");
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
        if (options.TotalLimit.HasValue)
            AddReplayValueOption(args, "--total-limit", options.TotalLimit.Value.ToString(CultureInfo.InvariantCulture));
        AddSearchRecipeRowSelectionReplayOptions(args, options);
        if (options.MaxJsonBytes.HasValue)
            AddReplayValueOption(args, "--max-json-bytes", options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture));
        if (options.ShowExcluded)
            args.Add("--show-excluded");
        if (includeRecipeQuerySelectors)
        {
            foreach (var includeQuery in options.IncludeRecipeQueries)
                AddReplayValueOption(args, "--include-query", includeQuery);
            foreach (var excludeQuery in options.ExcludeRecipeQueries)
                AddReplayValueOption(args, "--exclude-query", excludeQuery);
        }
    }

    private static int RunSearchRecipeAggregation(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
    {
        if (!TryResolveSearchRecipeSelection(options, out var selection, out var selectionError))
        {
            WriteUsageError(
                selectionError!,
                GetUsageLineOrThrow("search"),
                "Use `cdidx search --recipe risky-code/raw-diagnostic-echo`, or `--include-query` / `--exclude-query` with a recipe name.");
            return CommandExitCodes.UsageError;
        }

        var recipe = selection.Recipe;
        var scope = BuildSearchRecipeScope(recipe, options);
        var groupBy = NormalizeSearchAggregationKey(options.GroupBy ?? options.CountBy ?? options.UniqueBy!);
        var uniqueOnly = options.UniqueBy != null;
        var mode = uniqueOnly ? "unique" : options.GroupBy != null ? "group_by" : "count_by";
        return WithDb(options, jsonOptions, reader =>
        {
            var queryResults = CollectSearchRecipeAggregationResults(
                reader,
                selection.Queries,
                scope,
                options,
                userExact,
                groupBy,
                out var total,
                out var fileCount);

            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                        new SearchRecipeAggregationRunJsonResult(
                            JsonOutputContract.ApiVersion,
                            ToSearchRecipeListItem(recipe, selection.Queries),
                            scope,
                            mode,
                            groupBy,
                            uniqueOnly,
                            selection.Queries.Count,
                            total,
                            fileCount,
                            queryResults),
                        CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeAggregationRunJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "recipe aggregation",
                    "Reduce --limit or increase --max-json-bytes.");
            }
            else
            {
                foreach (var query in queryResults)
                {
                    Console.WriteLine($"[{query.Name}] {query.Query}");
                    if (uniqueOnly)
                    {
                        foreach (var group in query.Groups)
                            Console.WriteLine(group.Key);
                        var truncation = query.GroupsTruncated
                            ? $"showing {query.ReturnedGroups} of {query.TotalGroups}"
                            : query.Groups.Count.ToString(CultureInfo.InvariantCulture);
                        CommandErrorWriter.WriteStderr($"({truncation} unique {groupBy} values from {query.Count} results in {query.FileCount} files)");
                    }
                    else
                    {
                        WriteSearchGroupedCounts(groupBy, query.Groups, query.Count, query.FileCount, query.TotalGroups);
                    }
                    Console.WriteLine();
                }
                CommandErrorWriter.WriteStderr($"({total} recipe results in {fileCount} files across {selection.Queries.Count} queries; {mode} {groupBy})");
            }

            return CommandExitCodes.Success;
        });
    }

    private static NdjsonStreamWriteResult WriteRecipeSearchResultRows(
        DbReader reader,
        string recipeName,
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        int totalCount,
        QueryCommandOptions options,
        JsonSerializerOptions ndjsonOptions)
    {
        var records = new List<NdjsonOutputRecord>();
        foreach (var query in queryResults)
        {
            foreach (var result in query.Results)
            {
                JsonObject payload = options.SearchFields != null
                    ? BuildProjectedSearchResult(result, options.SearchFields, query.Name, recipeName)
                    : BuildRecipeSearchResultRow(recipeName, query.Name, result, ndjsonOptions);
                AddActiveSqliteDiagnostics(payload);
                records.Add(new NdjsonOutputRecord(payload.ToJsonString(ndjsonOptions)));
            }
        }

        var limitTruncated = queryResults.Any(query => query.Truncated);
        var selectionReason = queryResults
            .Select(query => query.SelectionReason)
            .FirstOrDefault(reason => reason != null);
        var selectionOmittedCount = queryResults
            .Where(query => query.SelectionOmittedCount.HasValue)
            .Sum(query => query.SelectionOmittedCount!.Value);
        return WriteNdjsonStream(
            records,
            totalCount,
            options,
            ndjsonOptions,
            reader,
            "search",
            limitTruncated,
            "Increase --limit or --total-limit, select one recipe query, or narrow the recipe scope.",
            totalCountAuthoritative: false,
            truncationReason: limitTruncated ? "limit" : null,
            selectionReason: selectionReason,
            selectionOmittedCount: selectionReason != null ? selectionOmittedCount : null);
    }

    private static JsonObject BuildRecipeSearchResultRow(
        string recipeName,
        string queryName,
        CompactSearchResult result,
        JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonOptions)?.AsObject() ?? [];
        payload["recipe"] = recipeName;
        payload["query_name"] = queryName;
        return payload;
    }

    private static int RunSearchRecipeIssueDrafts(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool userExact,
        CancellationToken cancellationToken)
    {
        if (!TryResolveSearchRecipeSelection(options, out var selection, out var selectionError))
        {
            WriteUsageError(
                selectionError!,
                GetUsageLineOrThrow("search"),
                "Use `cdidx search --recipe risky-code/raw-diagnostic-echo`, or `--include-query` / `--exclude-query` with a recipe name.");
            return CommandExitCodes.UsageError;
        }
        var recipe = selection.Recipe;
        var scope = BuildSearchRecipeScope(recipe, options);
        var preflightResult = IssueDuplicatePreflight.TryLoadAsync(
                options.OpenIssuesPath,
                options.OpenIssuesRepository,
                cancellationToken,
                options.IssueState)
            .GetAwaiter()
            .GetResult();
        if (!preflightResult.Loaded)
        {
            WriteUsageError(
                preflightResult.Error!,
                GetUsageLineOrThrow("search"),
                "Pass a readable JSON array from `gh issue list --state open --json number,title,labels,url`, or use `--open-issues github --repo owner/name`.");
            return CommandExitCodes.UsageError;
        }
        var preflight = preflightResult.Preflight;

        return WithDb(options, jsonOptions, reader =>
        {
            var queryResults = CollectSearchRecipeQueryResults(reader, selection.Queries, scope, options, userExact, out var total, out _);
            var drafts = queryResults
                .Where(queryResult => queryResult.Count > 0)
                .Select(queryResult => ToSearchIssueDraft(recipe, queryResult, preflight, options))
                .ToList();
            var fullRecipeMetadata = options.SummaryOnly ? null : ToSearchRecipeListItem(recipe, selection.Queries);
            var recipeSummaryMetadata = options.SummaryOnly ? ToSearchRecipeCompactListItem(recipe, selection.Queries) : null;
            var json = JsonSerializer.Serialize(
                new SearchIssueDraftExportJsonResult(
                    JsonOutputContract.ApiVersion,
                    fullRecipeMetadata,
                    recipeSummaryMetadata,
                    options.SummaryOnly ? "summary" : "full",
                    scope,
                    selection.Queries.Count,
                    total,
                    BuildSearchRecipeQueryFreshness(queryResults),
                    drafts.Count,
                    new SuggestionIssueDraftPreflightSummaryJsonResult(
                        preflight.Checked,
                        preflight.Source,
                        preflight.OpenIssueCount,
                        options.DuplicateConfidence,
                        options.DuplicateThreshold),
                    drafts),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchIssueDraftExportJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "issue-draft",
                "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.");
        });
    }

    private static int RunSearchRecipeCount(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
    {
        if (!TryResolveSearchRecipeSelection(options, out var selection, out var selectionError))
        {
            WriteUsageError(
                selectionError!,
                GetUsageLineOrThrow("search"),
                "Use `cdidx search --recipe risky-code/raw-diagnostic-echo`, or `--include-query` / `--exclude-query` with a recipe name.");
            return CommandExitCodes.UsageError;
        }

        var recipe = selection.Recipe;
        var scope = BuildSearchRecipeScope(recipe, options);
        return WithDb(options, jsonOptions, reader =>
        {
            var queryCounts = CountSearchRecipeQueryResults(
                reader,
                selection.Queries,
                scope,
                options,
                userExact,
                out var total,
                out var fileCount);

            if (options.Json)
            {
                if (options.SummaryOnly)
                {
                    var summaryQueries = queryCounts
                        .Select(query => new SearchRecipeCountSummaryQueryJsonResult(
                            query.Name,
                            query.Count,
                            query.FileCount))
                        .ToList();
                    var summaryJson = JsonSerializer.Serialize(
                        new SearchRecipeCountSummaryRunJsonResult(
                            JsonOutputContract.ApiVersion,
                            recipe.Name,
                            scope.Name,
                            selection.Queries.Count,
                            total,
                            fileCount,
                            BuildSearchRecipeQueryFreshness(queryCounts),
                            summaryQueries),
                        CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeCountSummaryRunJsonResult);
                    return WriteJsonObjectWithOptionalByteLimit(
                        summaryJson,
                        options,
                        "recipe count summary",
                        "Use a larger --max-json-bytes value or narrow the recipe/query selection.");
                }

                var json = JsonSerializer.Serialize(
                    new SearchRecipeCountRunJsonResult(
                        JsonOutputContract.ApiVersion,
                        ToSearchRecipeListItem(recipe, selection.Queries),
                        scope,
                        selection.Queries.Count,
                        total,
                        fileCount,
                        queryCounts),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeCountRunJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "recipe count",
                    "Use `--summary-only` to omit recipe metadata from count output.");
            }
            else
            {
                Console.WriteLine(total.ToString(CultureInfo.InvariantCulture));
                CommandErrorWriter.WriteStderr($"({total} recipe results in {fileCount} files across {selection.Queries.Count} queries)");
            }

            return CommandExitCodes.Success;
        });
    }

    private static int RunSearchIssueDrafts(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool exact,
        CancellationToken cancellationToken)
    {
        var preflightResult = IssueDuplicatePreflight.TryLoadAsync(
                options.OpenIssuesPath,
                options.OpenIssuesRepository,
                cancellationToken,
                options.IssueState)
            .GetAwaiter()
            .GetResult();
        if (!preflightResult.Loaded)
        {
            WriteUsageError(
                preflightResult.Error!,
                GetUsageLineOrThrow("search"),
                "Pass a readable JSON array from `gh issue list --state open --json number,title,labels,url`, or use `--open-issues github --repo owner/name`.");
            return CommandExitCodes.UsageError;
        }
        var preflight = preflightResult.Preflight;

        return WithDb(options, jsonOptions, reader =>
        {
            var resultLimit = GetAdHocIssueDraftResultLimit(options);
            var sourceTotalCountAuthoritative = options.GuardFilters.Count == 0;
            var sourceFetchLimit = sourceTotalCountAuthoritative
                ? int.MaxValue
                : GetSearchRecipeFetchLimit(options, resultLimit);
            var results = ReadSearchResults(
                reader,
                options,
                exact,
                sourceFetchLimit,
                guardRequestedLimit: resultLimit);
            var sourceRows = BuildSearchDisplayRows(results, options, exact);
            var outputSelection = ApplySearchOutputSelection(sourceRows, options, resultLimit);
            var rows = outputSelection.Rows;
            var omittedCount = Math.Max(0, outputSelection.OriginalCount - rows.Count);
            var selectionReason = GetSearchRecipeSelectionReason(outputSelection);
            var queryResult = new SearchRecipeQueryResultJsonResult(
                "ad-hoc",
                options.Query!,
                $"Ad hoc search for `{options.Query}`.",
                BuildAdHocIssueDraftLabels(options),
                "Review the evidence paths and surrounding code before filing.",
                [],
                [],
                exact,
                SearchAuditRecipes.DefaultQuerySeverity,
                [],
                [],
                [],
                [],
                [],
                [],
                null,
                null,
                null,
                null,
                rows.Count,
                rows.Count,
                outputSelection.OriginalCount,
                omittedCount,
                selectionReason,
                selectionReason != null ? outputSelection.SelectionOmittedCount : null,
                resultLimit,
                omittedCount,
                BuildSearchRecipeTopFiles(rows),
                outputSelection.Truncated || !sourceTotalCountAuthoritative,
                null,
                rows.Select(row => row.Compact).ToList());
            var drafts = rows.Count == 0
                ? []
                : new List<SearchIssueDraftJsonResult>
                {
                    ToAdHocSearchIssueDraft(
                        options,
                        queryResult,
                        preflight,
                        sourceTotalCountAuthoritative,
                        sourceTotalCountAuthoritative ? null : sourceFetchLimit)
                };

            var json = JsonSerializer.Serialize(
                new SearchIssueDraftExportJsonResult(
                    JsonOutputContract.ApiVersion,
                    null,
                    null,
                    "none",
                    null,
                    1,
                    rows.Count,
                    null,
                    drafts.Count,
                    new SuggestionIssueDraftPreflightSummaryJsonResult(
                        preflight.Checked,
                        preflight.Source,
                        preflight.OpenIssueCount,
                        options.DuplicateConfidence,
                        options.DuplicateThreshold),
                    drafts),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchIssueDraftExportJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "issue-draft",
                "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.");
        });
    }

    private static int GetAdHocIssueDraftResultLimit(QueryCommandOptions options)
        => options.TotalLimit.HasValue
            ? Math.Min(options.Limit, options.TotalLimit.Value)
            : options.Limit;

    private static List<SearchRecipeQueryResultJsonResult> CollectSearchRecipeQueryResults(
        DbReader reader,
        IReadOnlyList<SearchAuditRecipeQuery> recipeQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        bool userExact,
        out int total,
        out int minimumMatchedTotal)
    {
        var queryResults = new List<SearchRecipeQueryResultJsonResult>();
        total = 0;
        minimumMatchedTotal = 0;
        foreach (var recipeQuery in recipeQueries)
        {
            var exact = userExact || recipeQuery.ExactSubstring;
            var queryScope = BuildSearchRecipeQueryScope(scope, recipeQuery);
            var resultLimit = GetSearchRecipeEffectiveResultLimit(options, total);
            var guardFilters = BuildSearchRecipeGuardFilters(options, recipeQuery);
            var results = reader.Search(
                recipeQuery.Query,
                GetSearchRecipeFetchLimit(options, resultLimit),
                options.Lang,
                false,
                queryScope.PathPatterns,
                queryScope.ExcludePaths,
                queryScope.ExcludeTests,
                !options.NoDedup,
                options.Since,
                exact,
                false,
                !options.NoVisibilityRank,
                cursor: options.SearchCursor,
                guardFilters: guardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(options, recipeQuery),
                resultRanking: GetSearchRecipeResultRanking(recipeQuery.ResultRanking, resultLimit));
            results = ApplySearchRecipeFileRejectQueries(reader, results, options, recipeQuery);
            var rows = BuildSearchDisplayRows(results, options, exact, recipeQuery.Query, rawFtsOverride: false, recipeQuery: recipeQuery);
            var outputSelection = ApplySearchOutputSelection(rows, options, resultLimit);
            rows = outputSelection.Rows;
            ApplySearchRecipeAuditClassifications(recipeQuery, rows);
            var minimumOmitted = Math.Max(0, outputSelection.OriginalCount - rows.Count);
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
                        ? FormatSearchCursor(rows[^1].Result)
                        : null,
                rows.Select(row => row.Compact).ToList()));
        }

        return queryResults;
    }

    private static List<SearchRecipeCompactQueryResultJsonResult> CollectSearchRecipeCompactQueryResults(
        DbReader reader,
        IReadOnlyList<SearchAuditRecipeQuery> recipeQueries,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options,
        bool userExact,
        out int total)
    {
        var queryResults = new List<SearchRecipeCompactQueryResultJsonResult>();
        total = 0;
        foreach (var recipeQuery in recipeQueries)
        {
            var exact = userExact || recipeQuery.ExactSubstring;
            var queryScope = BuildSearchRecipeQueryScope(scope, recipeQuery);
            var resultLimit = GetSearchRecipeEffectiveResultLimit(options, total);
            var guardFilters = BuildSearchRecipeGuardFilters(options, recipeQuery);
            var results = reader.Search(
                recipeQuery.Query,
                GetSearchRecipeFetchLimit(options, resultLimit),
                options.Lang,
                false,
                queryScope.PathPatterns,
                queryScope.ExcludePaths,
                queryScope.ExcludeTests,
                !options.NoDedup,
                options.Since,
                exact,
                false,
                !options.NoVisibilityRank,
                cursor: options.SearchCursor,
                guardFilters: guardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(options, recipeQuery),
                resultRanking: GetSearchRecipeResultRanking(recipeQuery.ResultRanking, resultLimit));
            results = ApplySearchRecipeFileRejectQueries(reader, results, options, recipeQuery);
            var rows = BuildSearchDisplayRows(results, options, exact, recipeQuery.Query, recipeQuery: recipeQuery);
            var outputSelection = ApplySearchOutputSelection(rows, options, resultLimit);
            rows = outputSelection.Rows;
            ApplySearchRecipeAuditClassifications(recipeQuery, rows);
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
                        ? FormatSearchCursor(rows[^1].Result)
                        : null,
                rows.Select(row => new SearchRecipeCompactResultJsonResult(
                    row.Result.Path,
                    row.Result.Lang,
                    row.Result.Visibility,
                    [.. recipeQuery.RiskEvidence],
                    row.Result.StartLine,
                    row.Result.EndLine,
                    row.Compact.MatchLines,
                    row.Compact.EnclosingSymbolName,
                    row.Compact.EnclosingSymbolKind)).ToList()));
        }

        return queryResults;
    }

    private static string? GetSearchRecipeSelectionReason(SearchOutputSelection selection)
        => selection.SelectionOmittedCount > 0
            && selection.TruncationReason is "first_per_file" or "sample"
                ? selection.TruncationReason
                : null;

    private static int GetSearchRecipeFetchLimit(QueryCommandOptions options, int resultLimit)
    {
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
        out int total,
        out int fileCount)
    {
        var queryCounts = new List<SearchRecipeCountQueryJsonResult>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        total = 0;
        foreach (var recipeQuery in recipeQueries)
        {
            var exact = userExact || recipeQuery.ExactSubstring;
            var queryScope = BuildSearchRecipeQueryScope(scope, recipeQuery);
            var guardFilters = BuildSearchRecipeGuardFilters(options, recipeQuery);
            var results = reader.Search(
                recipeQuery.Query,
                int.MaxValue,
                options.Lang,
                false,
                queryScope.PathPatterns,
                queryScope.ExcludePaths,
                queryScope.ExcludeTests,
                !options.NoDedup,
                options.Since,
                exact,
                false,
                !options.NoVisibilityRank,
                cursor: options.SearchCursor,
                guardFilters: guardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(options, recipeQuery));
            results = ApplySearchRecipeFileRejectQueries(reader, results, options, recipeQuery);
            var rows = BuildSearchDisplayRows(results, options, exact, recipeQuery.Query, rawFtsOverride: false, recipeQuery: recipeQuery);
            ApplySearchRecipeAuditClassifications(recipeQuery, rows);
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
        }

        fileCount = paths.Count;
        return queryCounts;
    }

    private static void ApplySearchRecipeAuditClassifications(SearchAuditRecipeQuery recipeQuery, List<SearchDisplayRow> rows)
    {
        var taskResultClassifier = recipeQuery.Classifiers
            .FirstOrDefault(classifier => string.Equals(classifier.Name, "task_result_intent", StringComparison.Ordinal));
        if (taskResultClassifier == null)
            return;

        foreach (var row in rows)
        {
            var classification = TryClassifyTaskResultIntent(taskResultClassifier, row);
            if (classification == null)
                continue;

            row.Compact.AuditClassifications ??= [];
            row.Compact.AuditClassifications.Add(classification);
        }
    }

    private static SearchAuditClassificationJsonResult? TryClassifyTaskResultIntent(
        SearchRecipeClassifierJsonResult classifier,
        SearchDisplayRow row)
    {
        var evidence = GetTaskResultIntentEvidence(row);
        if (evidence == null)
            return null;

        var categoryMetadata = classifier.Categories
            .FirstOrDefault(category => string.Equals(category.Name, evidence.Category, StringComparison.Ordinal));
        if (categoryMetadata == null)
            return null;

        var details = new List<string>
        {
            $"reason:{evidence.Reason}",
        };
        if (!string.IsNullOrWhiteSpace(evidence.Receiver))
            details.Add($"receiver:{evidence.Receiver}");
        if (evidence.Line.HasValue)
            details.Add($"line:{evidence.Line.Value.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(row.Compact.EnclosingSymbolName))
            details.Add($"enclosing_symbol_name:{row.Compact.EnclosingSymbolName}");
        if (!string.IsNullOrWhiteSpace(row.Compact.EnclosingSymbolKind))
            details.Add($"enclosing_symbol_kind:{row.Compact.EnclosingSymbolKind}");

        return new SearchAuditClassificationJsonResult(
            classifier.Name,
            categoryMetadata.Name,
            categoryMetadata.Description,
            categoryMetadata.ReviewGuidance,
            details);
    }

    private static TaskResultIntentEvidence? GetTaskResultIntentEvidence(SearchDisplayRow row)
    {
        var highlight = row.Compact.Highlights
            .FirstOrDefault(highlight => highlight.Text.Contains(".Result", StringComparison.Ordinal));
        var lineText = highlight?.Text
            ?? row.Compact.Snippet
                .Split('\n', StringSplitOptions.None)
                .FirstOrDefault(line => line.Contains(".Result", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(lineText))
            return null;

        var receiver = ExtractTaskResultReceiver(lineText);
        var category = ClassifyTaskResultIntent(lineText, receiver, out var reason);
        return new TaskResultIntentEvidence(category, receiver, highlight?.Line, reason);
    }

    private static string ClassifyTaskResultIntent(string lineText, string? receiver, out string reason)
    {
        if (lineText.Contains("Task.FromResult", StringComparison.Ordinal)
            || lineText.Contains("Task.Run", StringComparison.Ordinal)
            || lineText.Contains(".AsTask().Result", StringComparison.Ordinal)
            || ContainsAsyncInvocationResult(lineText)
            || ContainsIdentifierFragment(receiver, "task")
            || ContainsIdentifierFragment(receiver, "valueTask"))
        {
            reason = "task_like_receiver";
            return "task_blocking";
        }

        if (lineText.Contains(".Result.", StringComparison.Ordinal)
            || ContainsIdentifierFragment(receiver, "result")
            || ContainsIdentifierFragment(receiver, "dto")
            || ContainsIdentifierFragment(receiver, "model")
            || ContainsIdentifierFragment(receiver, "response")
            || ContainsIdentifierFragment(receiver, "payload")
            || IsKnownResultWrapperReceiver(receiver))
        {
            reason = "result_wrapper_receiver";
            return "dto_result_property";
        }

        reason = "receiver_unclear";
        return "unclear_receiver";
    }

    private static bool ContainsIdentifierFragment(string? value, string fragment)
        => !string.IsNullOrWhiteSpace(value)
            && value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownResultWrapperReceiver(string? receiver)
        => receiver is not null
            && (receiver.Equals("row", StringComparison.OrdinalIgnoreCase)
                || receiver.Equals("query", StringComparison.OrdinalIgnoreCase)
                || receiver.Equals("command", StringComparison.OrdinalIgnoreCase)
                || receiver.Equals("parse", StringComparison.OrdinalIgnoreCase)
                || receiver.Equals("preflight", StringComparison.OrdinalIgnoreCase));

    private static string? ExtractTaskResultReceiver(string lineText)
    {
        var resultIndex = lineText.IndexOf(".Result", StringComparison.Ordinal);
        if (resultIndex <= 0)
            return null;

        var invocationReceiver = ExtractInvocationReceiverBeforeResult(lineText, resultIndex);
        if (!string.IsNullOrWhiteSpace(invocationReceiver))
            return invocationReceiver;

        var end = resultIndex;
        while (end > 0 && char.IsWhiteSpace(lineText[end - 1]))
            end--;

        var start = end - 1;
        while (start >= 0 && IsTaskResultReceiverChar(lineText[start]))
            start--;

        var receiver = lineText[(start + 1)..end].Trim('.', '?');
        return string.IsNullOrWhiteSpace(receiver)
            ? null
            : receiver.Length <= 96 ? receiver : receiver[^96..];
    }

    private static bool IsTaskResultReceiverChar(char ch)
        => char.IsLetterOrDigit(ch) || ch is '_' or '.' or ')' or ']' or '?';

    private static bool ContainsAsyncInvocationResult(string lineText)
    {
        var resultIndex = lineText.IndexOf(".Result", StringComparison.Ordinal);
        var invocationReceiver = resultIndex > 0
            ? ExtractInvocationReceiverBeforeResult(lineText, resultIndex)
            : null;
        return invocationReceiver?.EndsWith("Async", StringComparison.Ordinal) == true;
    }

    private static string? ExtractInvocationReceiverBeforeResult(string lineText, int resultIndex)
    {
        var end = resultIndex;
        while (end > 0 && char.IsWhiteSpace(lineText[end - 1]))
            end--;
        if (end == 0 || lineText[end - 1] != ')')
            return null;

        var depth = 0;
        for (var i = end - 1; i >= 0; i--)
        {
            if (lineText[i] == ')')
            {
                depth++;
                continue;
            }

            if (lineText[i] != '(')
                continue;

            depth--;
            if (depth != 0)
                continue;

            var nameEnd = i;
            while (nameEnd > 0 && char.IsWhiteSpace(lineText[nameEnd - 1]))
                nameEnd--;
            var nameStart = nameEnd - 1;
            while (nameStart >= 0 && (char.IsLetterOrDigit(lineText[nameStart]) || lineText[nameStart] == '_'))
                nameStart--;

            var receiver = lineText[(nameStart + 1)..nameEnd];
            return string.IsNullOrWhiteSpace(receiver) ? null : receiver;
        }

        return null;
    }

    private static List<SearchRecipeClassifierCountJsonResult>? BuildSearchRecipeClassifierCounts(List<SearchDisplayRow> rows)
    {
        var classifications = rows
            .SelectMany(row => row.Compact.AuditClassifications ?? [])
            .ToList();
        if (classifications.Count == 0)
            return null;

        return classifications
            .GroupBy(classification => classification.Classifier, StringComparer.Ordinal)
            .Select(classifierGroup => new SearchRecipeClassifierCountJsonResult(
                classifierGroup.Key,
                classifierGroup
                    .GroupBy(classification => classification.Category, StringComparer.Ordinal)
                    .Select(categoryGroup =>
                    {
                        var representative = categoryGroup.First();
                        return new SearchRecipeClassifierCategoryCountJsonResult(
                            categoryGroup.Key,
                            categoryGroup.Count(),
                            representative.Description,
                            representative.ReviewGuidance);
                    })
                    .OrderByDescending(category => category.Count)
                    .ThenBy(category => category.Name, StringComparer.Ordinal)
                    .ToList()))
            .OrderBy(classifier => classifier.Classifier, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record TaskResultIntentEvidence(
        string Category,
        string? Receiver,
        int? Line,
        string Reason);

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
            var exact = userExact || recipeQuery.ExactSubstring;
            var queryScope = BuildSearchRecipeQueryScope(scope, recipeQuery);
            var guardFilters = BuildSearchRecipeGuardFilters(options, recipeQuery);
            var results = reader.Search(
                recipeQuery.Query,
                int.MaxValue,
                options.Lang,
                false,
                queryScope.PathPatterns,
                queryScope.ExcludePaths,
                queryScope.ExcludeTests,
                !options.NoDedup,
                options.Since,
                exact,
                false,
                !options.NoVisibilityRank,
                cursor: options.SearchCursor,
                guardFilters: guardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                requiredPathPatterns: GetSearchRecipeRequiredPathPatterns(options, recipeQuery));
            results = ApplySearchRecipeFileRejectQueries(reader, results, options, recipeQuery);
            var rows = BuildSearchDisplayRows(results, options, exact, recipeQuery.Query, rawFtsOverride: false, recipeQuery: recipeQuery);
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

    private static IReadOnlyList<SearchGuardFilter> BuildSearchRecipeGuardFilters(QueryCommandOptions options, SearchAuditRecipeQuery recipeQuery)
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

    private static SearchRecipeRunSummaryJsonResult BuildSearchRecipeRunSummary(
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        int limitPerQuery,
        int? totalLimit,
        int emittedResultCount)
        => new(
            limitPerQuery,
            totalLimit,
            emittedResultCount,
            queryResults.Count(query => query.Truncated),
            queryResults.Sum(query => query.MinimumOmittedResultCount),
            BuildSearchRecipeQueryFreshness(queryResults),
            queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor)),
            BuildSearchRecipeCursoringHint(
                queryResults.Any(query => query.Truncated),
                queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor))));

    private static SearchRecipeRunSummaryJsonResult BuildSearchRecipeRunSummary(
        IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults,
        int limitPerQuery,
        int? totalLimit,
        int emittedResultCount)
        => new(
            limitPerQuery,
            totalLimit,
            emittedResultCount,
            queryResults.Count(query => query.Truncated),
            queryResults.Sum(query => query.MinimumOmittedResultCount),
            BuildSearchRecipeQueryFreshness(queryResults),
            queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor)),
            BuildSearchRecipeCursoringHint(
                queryResults.Any(query => query.Truncated),
                queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor))));

    private static string BuildSearchRecipeCursoringHint(bool hasTruncatedQuery, bool cursoringAvailable)
        => cursoringAvailable
            ? "When a query is truncated, rerun a single child query with --recipe <recipe>/<query> --cursor <next_cursor> to page the next result set."
            : hasTruncatedQuery
                ? "Continuation cursors are unavailable for the selected rows; increase --limit or --total-limit and rerun."
                : "No query is truncated, so no continuation cursor is needed.";

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults)
        => BuildSearchRecipeQueryFreshness(queryResults.Select(query => (query.Name, query.MinimumMatchedCount)));

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults)
        => BuildSearchRecipeQueryFreshness(queryResults.Select(query => (query.Name, query.MinimumMatchedCount)));

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(IReadOnlyList<SearchRecipeCountQueryJsonResult> queryResults)
        => BuildSearchRecipeQueryFreshness(queryResults.Select(query => (query.Name, query.Count)));

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(IEnumerable<(string Name, int Count)> queryResults)
    {
        var results = queryResults.ToList();
        var staleQueryNames = results
            .Where(query => query.Count == 0)
            .Select(query => query.Name)
            .ToList();
        return new(
            results.Count(query => query.Count > 0),
            staleQueryNames.Count,
            staleQueryNames);
    }

    private static SearchRecipeScopeJsonResult BuildSearchRecipeScope(SearchAuditRecipe recipe, QueryCommandOptions options)
    {
        var scopeName = options.AuditScopeExplicit ? options.AuditScope : recipe.DefaultScope;
        var pathPatterns = new List<string>(options.PathPatterns);
        var excludePaths = new List<string>(options.ExcludePaths);
        var excludeTests = options.ExcludeTests;

        if (string.Equals(scopeName, SearchAuditRecipes.DefaultAuditScope, StringComparison.OrdinalIgnoreCase))
        {
            if (pathPatterns.Count == 0)
                AddDistinct(pathPatterns, recipe.DefaultPathPatterns);
            AddDistinct(excludePaths, recipe.DefaultExcludePaths);
            excludeTests = true;
        }

        return new SearchRecipeScopeJsonResult(
            scopeName,
            pathPatterns,
            excludePaths,
            excludeTests,
            [.. recipe.DefaultPathPatterns],
            [.. recipe.DefaultExcludePaths],
            options.ShowExcluded ? BuildSearchRecipeExcludedDiagnostics(recipe, options, scopeName, excludeTests) : null);
    }

    private static SearchRecipeScopeJsonResult BuildSearchRecipeQueryScope(
        SearchRecipeScopeJsonResult scope,
        SearchAuditRecipeQuery query)
    {
        var pathPatterns = query.PathPatterns.Count > 0
            ? [.. query.PathPatterns]
            : new List<string>(scope.PathPatterns);
        var excludePaths = new List<string>(scope.ExcludePaths);
        AddDistinct(excludePaths, query.ExcludePaths);

        return scope with
        {
            PathPatterns = pathPatterns,
            ExcludePaths = excludePaths
        };
    }

    private static List<SearchRecipeExcludedDiagnosticJsonResult> BuildSearchRecipeExcludedDiagnostics(
        SearchAuditRecipe recipe,
        QueryCommandOptions options,
        string scopeName,
        bool excludeTests)
    {
        var diagnostics = new List<SearchRecipeExcludedDiagnosticJsonResult>();
        var sourceScope = string.Equals(scopeName, SearchAuditRecipes.DefaultAuditScope, StringComparison.OrdinalIgnoreCase);
        diagnostics.Add(new SearchRecipeExcludedDiagnosticJsonResult(
            "recipe_default_path_patterns",
            sourceScope && options.PathPatterns.Count == 0 && recipe.DefaultPathPatterns.Count > 0,
            [.. recipe.DefaultPathPatterns],
            "Default source-scope include patterns applied when a recipe runs without user --path filters."));
        diagnostics.Add(new SearchRecipeExcludedDiagnosticJsonResult(
            "recipe_default_exclude_paths",
            sourceScope && recipe.DefaultExcludePaths.Count > 0,
            [.. recipe.DefaultExcludePaths],
            "Default source-scope exclusions suppress recipe definitions, tests, docs, changelog text, and agent/workflow metadata."));
        if (options.ExcludePaths.Count > 0)
        {
            diagnostics.Add(new SearchRecipeExcludedDiagnosticJsonResult(
                "user_exclude_paths",
                true,
                [.. options.ExcludePaths],
                "User-provided --exclude-path filters are applied after recipe defaults."));
        }
        diagnostics.Add(new SearchRecipeExcludedDiagnosticJsonResult(
            "exclude_tests",
            excludeTests,
            [],
            "The test-file classifier is enabled for this recipe scope; exact excluded paths depend on indexed file metadata."));
        return diagnostics;
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (!target.Contains(value, StringComparer.Ordinal))
                target.Add(value);
        }
    }

    private static List<SearchRecipeTopFileJsonResult> BuildSearchRecipeTopFiles(IReadOnlyList<SearchDisplayRow> rows)
        => rows
            .GroupBy(row => row.Result.Path, StringComparer.Ordinal)
            .Select(group => new SearchRecipeTopFileJsonResult(group.Key, group.Count()))
            .OrderByDescending(file => file.Count)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .Take(10)
            .ToList();

    private static List<SearchRecipeTopFileJsonResult> BuildSearchRecipeTopFiles(IReadOnlyList<SearchFileCountResult> fileCounts)
        => fileCounts
            .Select(file => new SearchRecipeTopFileJsonResult(file.Path, file.Count))
            .OrderByDescending(file => file.Count)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .Take(10)
            .ToList();

    private static IReadOnlyList<string>? GetSearchRecipeRequiredPathPatterns(QueryCommandOptions options, SearchAuditRecipeQuery recipeQuery)
        => options.PathPatterns.Count > 0 && recipeQuery.PathPatterns.Count > 0
            ? options.PathPatterns
            : null;

    private static int GetSearchRecipeEffectiveResultLimit(QueryCommandOptions options, int emittedSoFar)
    {
        if (!options.TotalLimit.HasValue)
            return options.Limit;

        var remaining = options.TotalLimit.Value - emittedSoFar;
        if (remaining <= 0)
            return 0;

        return Math.Min(options.Limit, remaining);
    }

    internal static SearchResultRanking GetSearchRecipeResultRanking(
        SearchResultRanking requestedRanking,
        int resultLimit)
        => resultLimit > 0 ? requestedRanking : SearchResultRanking.Default;

    private static int FetchLimitForSearchEnvelope(int limit)
    {
        if (limit <= 0)
            return 1;

        var requested = (long)limit + 1;
        var overFetched = requested * SearchEnvelopeOverFetchFactor;
        var candidateLimit = Math.Max(SearchEnvelopeMinCandidates, Math.Max(requested, overFetched));
        return (int)Math.Min(SearchEnvelopeMaxCandidates, candidateLimit);
    }

    internal static int FetchLimitForSearchEnvelopeForTests(int limit) => FetchLimitForSearchEnvelope(limit);

    private static bool TrimSearchRowsToRequestedLimit(List<SearchDisplayRow> rows, int limit)
    {
        if (rows.Count <= limit)
            return false;
        rows.RemoveRange(limit, rows.Count - limit);
        return true;
    }

    private static List<SearchNamedBatchQueryResultJsonResult> CollectSearchNamedBatchQueryResults(
        DbReader reader,
        QueryCommandOptions options,
        bool userExact,
        out int total)
    {
        var queryResults = new List<SearchNamedBatchQueryResultJsonResult>();
        total = 0;
        foreach (var namedQuery in options.NamedSearchQueries)
        {
            var results = reader.Search(
                namedQuery.Query,
                FetchLimitForSearchEnvelope(options.Limit),
                options.Lang,
                options.RawFts,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                !options.NoDedup,
                options.Since,
                userExact,
                options.Prefix,
                !options.NoVisibilityRank,
                guardFilters: options.GuardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope,
                tokenBoundary: options.TokenBoundary);
            var rows = BuildSearchDisplayRows(results, options, userExact, namedQuery.Query);
            var truncated = TrimSearchRowsToRequestedLimit(rows, options.Limit);
            AttachExactSubstringHint(
                rows.Select(row => row.Compact),
                SearchQueryAdvisor.BuildExactSubstringHint(namedQuery.Query, options.RawFts, userExact, options.Prefix));
            total += rows.Count;
            queryResults.Add(new SearchNamedBatchQueryResultJsonResult(
                namedQuery.Name,
                namedQuery.Query,
                userExact,
                rows.Count,
                BuildSearchRecipeTopFiles(rows),
                truncated,
                null,
                rows.Select(row => row.Compact).ToList()));
        }

        return queryResults;
    }

    private static List<SearchNamedBatchCountSummaryQueryJsonResult> CountSearchNamedBatchQueryResults(
        DbReader reader,
        QueryCommandOptions options,
        bool userExact,
        out int total,
        out int fileCount)
    {
        var queryCounts = new List<SearchNamedBatchCountSummaryQueryJsonResult>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        total = 0;
        foreach (var namedQuery in options.NamedSearchQueries)
        {
            var results = reader.Search(
                namedQuery.Query,
                int.MaxValue,
                options.Lang,
                options.RawFts,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                !options.NoDedup,
                options.Since,
                userExact,
                options.Prefix,
                !options.NoVisibilityRank,
                guardFilters: options.GuardFilters,
                guardWindow: options.GuardWindow,
                guardScope: options.GuardScope);
            var rows = BuildSearchDisplayRows(results, options, userExact, namedQuery.Query);
            var count = rows.Count;
            var fileCountForQuery = rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count();
            foreach (var path in rows.Select(row => row.Result.Path))
                paths.Add(path);

            total += count;
            queryCounts.Add(new SearchNamedBatchCountSummaryQueryJsonResult(
                namedQuery.Name,
                namedQuery.Query,
                count,
                fileCountForQuery));
        }

        fileCount = paths.Count;
        return queryCounts;
    }

    private static List<SearchResult> ApplySearchRecipeFileRejectQueries(
        DbReader reader,
        List<SearchResult> results,
        QueryCommandOptions options,
        SearchAuditRecipeQuery recipeQuery)
    {
        if (recipeQuery.RejectFileQueries.Count == 0 || results.Count == 0)
            return results;

        var rejectedPaths = new Dictionary<string, bool>(StringComparer.Ordinal);
        return results
            .Where(result => !ShouldRejectSearchRecipeFile(reader, result.Path, options, recipeQuery, rejectedPaths))
            .ToList();
    }

    private static bool ShouldRejectSearchRecipeFile(
        DbReader reader,
        string path,
        QueryCommandOptions options,
        SearchAuditRecipeQuery recipeQuery,
        Dictionary<string, bool> rejectedPaths)
    {
        if (rejectedPaths.TryGetValue(path, out var rejected))
            return rejected;

        foreach (var rejectQuery in recipeQuery.RejectFileQueries)
        {
            var matches = reader.Search(
                rejectQuery,
                1,
                options.Lang,
                rawQuery: false,
                pathPatterns: [path],
                excludePathPatterns: null,
                excludeTests: false,
                deduplicate: true,
                since: options.Since,
                exact: true,
                prefix: false,
                visibilityRank: false);
            if (matches.Count == 0)
                continue;

            rejectedPaths[path] = true;
            return true;
        }

        rejectedPaths[path] = false;
        return false;
    }

    private static SearchIssueDraftJsonResult ToSearchIssueDraft(
        SearchAuditRecipe recipe,
        SearchRecipeQueryResultJsonResult queryResult,
        IssueDuplicatePreflight preflight,
        QueryCommandOptions options)
    {
        var labels = queryResult.RecommendedLabels
            .Concat(options.IssueLabels)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var title = BuildSearchIssueDraftTitle(recipe, queryResult);
        var evidencePaths = queryResult.Results
            .Select(result => result.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
        var evidence = BuildSearchIssueDraftEvidence(queryResult, includeSnippets: options.SnippetLines > 0);
        var missingLabels = BuildMissingIssueDraftLabels(labels, preflight);
        var labelWarning = BuildIssueDraftLabelWarning(missingLabels, preflight);
        var duplicateProbeTriage = BuildSearchIssueDraftTriage(queryResult, preflight.Checked, 0);
        var duplicateProbeBody = BuildSearchIssueDraftBody(recipe, queryResult, evidencePaths, evidence, duplicateProbeTriage, options);
        var duplicateMatches = preflight.FindMatches(
            title,
            labels,
            options.DuplicateThreshold,
            evidencePaths,
            duplicateProbeBody);
        var triage = BuildSearchIssueDraftTriage(queryResult, preflight.Checked, duplicateMatches.Count);
        return new SearchIssueDraftJsonResult(
            $"{recipe.Name}/{queryResult.Name}",
            title,
            labels,
            missingLabels,
            labelWarning,
            evidencePaths,
            evidence,
            triage,
            BuildSearchIssueDraftBody(recipe, queryResult, evidencePaths, evidence, triage, options),
            new SearchIssueDraftSourceJsonResult(
                recipe.Name,
                queryResult.Name,
                queryResult.Query,
                queryResult.Description,
                queryResult.FalsePositiveGuidance,
                queryResult.RiskEvidence,
                queryResult.ExactSubstring,
                queryResult.Count,
                queryResult.ResultLimit,
                queryResult.OmittedCount,
                queryResult.SelectionReason,
                queryResult.SelectionOmittedCount,
                queryResult.MinimumOmittedResultCount,
                queryResult.Truncated,
                queryResult.NextCursor),
            new SuggestionIssueDraftDuplicatePreflightJsonResult(
                preflight.Checked,
                duplicateMatches.Count,
                duplicateMatches));
    }

    private static SearchIssueDraftJsonResult ToAdHocSearchIssueDraft(
        QueryCommandOptions options,
        SearchRecipeQueryResultJsonResult queryResult,
        IssueDuplicatePreflight preflight,
        bool sourceTotalCountAuthoritative,
        int? sourceFetchLimit)
    {
        var labels = BuildAdHocIssueDraftLabels(options);
        var title = BuildAdHocSearchIssueDraftTitle(options);
        var evidencePaths = queryResult.Results
            .Select(result => result.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
        var evidence = BuildSearchIssueDraftEvidence(queryResult, includeSnippets: options.SnippetLines > 0);
        var missingLabels = BuildMissingIssueDraftLabels(labels, preflight);
        var labelWarning = BuildIssueDraftLabelWarning(missingLabels, preflight);
        var duplicateProbeTriage = BuildSearchIssueDraftTriage(queryResult, preflight.Checked, 0);
        var duplicateProbeBody = BuildAdHocSearchIssueDraftBody(
            queryResult,
            evidencePaths,
            evidence,
            duplicateProbeTriage,
            options,
            sourceTotalCountAuthoritative,
            sourceFetchLimit);
        var duplicateMatches = preflight.FindMatches(
            title,
            labels,
            options.DuplicateThreshold,
            evidencePaths,
            duplicateProbeBody);
        var triage = BuildSearchIssueDraftTriage(queryResult, preflight.Checked, duplicateMatches.Count);
        return new SearchIssueDraftJsonResult(
            "search/ad-hoc",
            title,
            labels,
            missingLabels,
            labelWarning,
            evidencePaths,
            evidence,
            triage,
            BuildAdHocSearchIssueDraftBody(
                queryResult,
                evidencePaths,
                evidence,
                triage,
                options,
                sourceTotalCountAuthoritative,
                sourceFetchLimit),
            new SearchIssueDraftSourceJsonResult(
                null,
                null,
                queryResult.Query,
                queryResult.Description,
                queryResult.FalsePositiveGuidance,
                queryResult.RiskEvidence,
                queryResult.ExactSubstring,
                queryResult.Count,
                queryResult.ResultLimit,
                queryResult.OmittedCount,
                queryResult.SelectionReason,
                queryResult.SelectionOmittedCount,
                queryResult.MinimumOmittedResultCount,
                queryResult.Truncated,
                queryResult.NextCursor,
                sourceTotalCountAuthoritative ? queryResult.MinimumMatchedCount : null,
                queryResult.Count,
                options.Limit,
                options.TotalLimit,
                options.FirstPerFile,
                options.SampleSize,
                queryResult.MinimumMatchedCount,
                sourceTotalCountAuthoritative,
                sourceFetchLimit),
            new SuggestionIssueDraftDuplicatePreflightJsonResult(
                preflight.Checked,
                duplicateMatches.Count,
                duplicateMatches));
    }

    private static List<string> BuildMissingIssueDraftLabels(
        IReadOnlyList<string> labels,
        IssueDuplicatePreflight preflight)
    {
        if (!preflight.RepositoryLabelsChecked || labels.Count == 0)
            return [];

        var repositoryLabels = preflight.RepositoryLabels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return labels
            .Where(label => !repositoryLabels.Contains(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? BuildIssueDraftLabelWarning(
        IReadOnlyList<string> missingLabels,
        IssueDuplicatePreflight preflight)
    {
        if (missingLabels.Count == 0)
            return null;

        var source = string.IsNullOrWhiteSpace(preflight.Source)
            ? "repository label preflight"
            : preflight.Source;
        return $"Repository label validation against {source} found missing label(s): {string.Join(", ", missingLabels)}.";
    }

    private static List<SearchIssueDraftEvidenceJsonResult> BuildSearchIssueDraftEvidence(
        SearchRecipeQueryResultJsonResult queryResult,
        bool includeSnippets)
    {
        var evidence = new List<SearchIssueDraftEvidenceJsonResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in queryResult.Results)
        {
            if (string.IsNullOrWhiteSpace(result.Path))
                continue;

            var line = GetSearchIssueDraftEvidenceLine(result);
            var key = $"{result.Path}\0{line.ToString(CultureInfo.InvariantCulture)}";
            if (!seen.Add(key))
                continue;

            var snippet = includeSnippets
                ? BuildSearchIssueDraftEvidenceSnippet(result)
                : string.Empty;
            if (includeSnippets && string.IsNullOrWhiteSpace(snippet))
                continue;

            evidence.Add(new SearchIssueDraftEvidenceJsonResult(result.Path, line, snippet));
            if (evidence.Count >= MaxIssueDraftEvidenceItems)
                break;
        }

        return evidence;
    }

    private static int GetSearchIssueDraftEvidenceLine(CompactSearchResult result)
    {
        if (result.MatchLines.Count > 0)
            return result.MatchLines[0];
        if (result.FocusLine.HasValue)
            return result.FocusLine.Value;
        if (result.SnippetStartLine > 0)
            return result.SnippetStartLine;
        return Math.Max(1, result.ChunkStartLine);
    }

    private static string BuildSearchIssueDraftEvidenceSnippet(CompactSearchResult result)
    {
        var snippetLines = result.Snippet.Split('\n');
        var targetLines = result.MatchLines.Count > 0
            ? result.MatchLines.Take(2).ToHashSet()
            : result.FocusLine.HasValue
                ? new HashSet<int> { result.FocusLine.Value }
                : [];
        var lines = new List<string>();

        if (targetLines.Count > 0)
        {
            for (var i = 0; i < snippetLines.Length; i++)
            {
                var absoluteLine = result.SnippetStartLine + i;
                if (targetLines.Contains(absoluteLine))
                    AddEvidenceSnippetLine(lines, snippetLines[i]);
            }
        }

        if (lines.Count == 0)
        {
            foreach (var line in snippetLines)
            {
                AddEvidenceSnippetLine(lines, line);
                if (lines.Count > 0)
                    break;
            }
        }

        return BoundSearchIssueDraftEvidenceSnippet(string.Join('\n', lines));
    }

    private static void AddEvidenceSnippetLine(List<string> lines, string line)
    {
        var trimmed = line.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            lines.Add(trimmed);
    }

    private static string BoundSearchIssueDraftEvidenceSnippet(string snippet)
    {
        if (snippet.Length <= MaxIssueDraftEvidenceSnippetLength)
            return snippet;

        return snippet[..MaxIssueDraftEvidenceSnippetLength].TrimEnd() + "...";
    }

    private static string BuildSearchIssueDraftTitle(SearchAuditRecipe recipe, SearchRecipeQueryResultJsonResult queryResult)
        => $"Search audit recipe {recipe.Name}: {queryResult.Name}";

    private static IssueDraftTriageMetadataJsonResult BuildSearchIssueDraftTriage(
        SearchRecipeQueryResultJsonResult queryResult,
        bool duplicatePreflightChecked,
        int duplicateMatchCount)
        => new(
            queryResult.Severity,
            GetSearchRecipeConfidence(queryResult.Count),
            queryResult.Count,
            BuildSearchIssueDraftDuplicateGuidance(duplicatePreflightChecked, duplicateMatchCount));

    private static string GetSearchRecipeConfidence(int resultCount)
        => resultCount >= 3 ? "high" : resultCount >= 2 ? "medium" : "low";

    private static string BuildSearchIssueDraftDuplicateGuidance(bool duplicatePreflightChecked, int duplicateMatchCount)
    {
        if (!duplicatePreflightChecked)
            return "Duplicate preflight was not checked; search open issues before filing.";
        if (duplicateMatchCount > 0)
            return "Review duplicate_preflight.matches before filing; merge evidence into an existing issue when the same root cause is already tracked.";
        return "No duplicate candidates were found by preflight; still verify open issues before filing.";
    }

    private static string BuildAdHocSearchIssueDraftTitle(QueryCommandOptions options)
        => string.IsNullOrWhiteSpace(options.IssueTitle)
            ? $"Search issue draft: {options.Query}"
            : options.IssueTitle.Trim();

    private static List<string> BuildAdHocIssueDraftLabels(QueryCommandOptions options)
        => options.IssueLabels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string BuildSearchIssueDraftBody(
        SearchAuditRecipe recipe,
        SearchRecipeQueryResultJsonResult queryResult,
        IReadOnlyList<string> evidencePaths,
        IReadOnlyList<SearchIssueDraftEvidenceJsonResult> evidence,
        IssueDraftTriageMetadataJsonResult triage,
        QueryCommandOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        sb.AppendLine(queryResult.Description);
        sb.AppendLine();
        sb.AppendLine("## Recipe");
        sb.AppendLine(recipe.Name);
        sb.AppendLine();
        sb.AppendLine("## Search query");
        sb.AppendLine(queryResult.Query);
        sb.AppendLine();
        sb.AppendLine("## Evidence paths");
        if (evidencePaths.Count == 0)
        {
            sb.AppendLine("N/A");
        }
        else
        {
            foreach (var path in evidencePaths)
                sb.AppendLine($"- {path}");
        }
        sb.AppendLine();
        AppendSearchIssueDraftEvidence(sb, evidence);
        sb.AppendLine();
        AppendSearchIssueDraftTriageMetadata(sb, triage);
        sb.AppendLine();
        AppendSearchIssueDraftOmittedResults(sb, queryResult);
        sb.AppendLine();
        sb.AppendLine("## False-positive guidance");
        sb.AppendLine(queryResult.FalsePositiveGuidance);
        sb.AppendLine();
        if (queryResult.RiskEvidence.Count > 0)
        {
            sb.AppendLine("## Risk evidence");
            foreach (var riskEvidence in queryResult.RiskEvidence)
                sb.AppendLine($"- {riskEvidence}");
            sb.AppendLine();
        }

        if (queryResult.StringComparisonTaxonomy is not null)
        {
            AppendSearchIssueDraftStringComparisonTaxonomy(sb, queryResult.StringComparisonTaxonomy);
            sb.AppendLine();
        }

        if (queryResult.BroadCatchTaxonomy is not null)
        {
            AppendSearchIssueDraftBroadCatchTaxonomy(sb, queryResult.BroadCatchTaxonomy);
            sb.AppendLine();
        }
        sb.AppendLine("## Replay command");
        sb.AppendLine("```sh");
        sb.AppendLine(BuildSearchRecipeReplayCommand(recipe, options, queryResult.Name));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Search metadata");
        sb.AppendLine($"- draft_id: `{recipe.Name}/{queryResult.Name}`");
        sb.AppendLine($"- recipe_query: `{queryResult.Name}`");
        sb.AppendLine($"- result_count: `{queryResult.Count}`");
        sb.AppendLine($"- result_limit: `{queryResult.ResultLimit}`");
        sb.AppendLine($"- omitted_count: `{queryResult.OmittedCount}`");
        if (!string.IsNullOrWhiteSpace(queryResult.SelectionReason))
        {
            sb.AppendLine($"- selection_reason: `{queryResult.SelectionReason}`");
            sb.AppendLine($"- selection_omitted_count: `{queryResult.SelectionOmittedCount.GetValueOrDefault()}`");
        }
        sb.AppendLine($"- minimum_omitted_result_count: `{queryResult.MinimumOmittedResultCount}`");
        sb.AppendLine($"- exact_substring: `{queryResult.ExactSubstring.ToString().ToLowerInvariant()}`");
        return sb.ToString().TrimEnd();
    }

    private static void AppendSearchIssueDraftEvidence(
        StringBuilder sb,
        IReadOnlyList<SearchIssueDraftEvidenceJsonResult> evidence)
    {
        sb.AppendLine("## Representative evidence");
        if (evidence.Count == 0)
        {
            sb.AppendLine("N/A");
            return;
        }

        foreach (var item in evidence)
        {
            sb.AppendLine($"- `{item.Path}:{item.Line.ToString(CultureInfo.InvariantCulture)}`");
            if (string.IsNullOrWhiteSpace(item.Snippet))
                continue;

            sb.AppendLine("```text");
            sb.AppendLine(item.Snippet);
            sb.AppendLine("```");
        }
    }

    private static void AppendSearchIssueDraftBroadCatchTaxonomy(StringBuilder sb, SearchRecipeBroadCatchTaxonomyJsonResult taxonomy)
    {
        sb.AppendLine("## Broad-catch taxonomy");
        sb.AppendLine(taxonomy.TriageGuidance);
        sb.AppendLine();
        sb.AppendLine("### Boundary categories");
        foreach (var category in taxonomy.BoundaryCategories)
            sb.AppendLine($"- `{category.Name}`: {category.Description} Expected diagnostic behavior: {category.ExpectedDiagnosticBehavior}");
        sb.AppendLine();
        sb.AppendLine("### Diagnostic behavior categories");
        foreach (var behavior in taxonomy.DiagnosticBehaviors)
            sb.AppendLine($"- `{behavior.Name}`: {behavior.Description}");
    }

    private static void AppendSearchIssueDraftStringComparisonTaxonomy(StringBuilder sb, SearchRecipeStringComparisonTaxonomyJsonResult taxonomy)
    {
        sb.AppendLine("## String-comparison taxonomy");
        sb.AppendLine(taxonomy.TriageGuidance);
        sb.AppendLine();
        sb.AppendLine("### Domain categories");
        foreach (var category in taxonomy.DomainCategories)
            sb.AppendLine($"- `{category.Name}`: {category.Description} Review: {category.ReviewGuidance}");
    }

    private static void AppendSearchIssueDraftTriageMetadata(StringBuilder sb, IssueDraftTriageMetadataJsonResult triage)
    {
        sb.AppendLine("## Triage metadata");
        sb.AppendLine($"- severity: `{triage.Severity}`");
        sb.AppendLine($"- confidence: `{triage.Confidence}`");
        sb.AppendLine($"- evidence_count: `{triage.EvidenceCount}`");
        sb.AppendLine($"- duplicate_guidance: {triage.DuplicateGuidance}");
    }

    private static void AppendSearchIssueDraftOmittedResults(
        StringBuilder sb,
        SearchRecipeQueryResultJsonResult queryResult)
    {
        sb.AppendLine("## Omitted results");
        sb.AppendLine($"- result_limit: `{queryResult.ResultLimit}`");
        sb.AppendLine($"- omitted_count: `{queryResult.OmittedCount}`");
        sb.AppendLine($"- minimum_omitted_result_count: `{queryResult.MinimumOmittedResultCount}`");
        sb.AppendLine($"- truncated: `{queryResult.Truncated.ToString().ToLowerInvariant()}`");
        if (!string.IsNullOrWhiteSpace(queryResult.NextCursor))
            sb.AppendLine($"- next_cursor: `{queryResult.NextCursor}`");
    }

    private static string BuildSearchRecipeReplayCommand(SearchAuditRecipe recipe, QueryCommandOptions options, string? queryName = null)
    {
        var recipeSelector = string.IsNullOrWhiteSpace(queryName)
            ? recipe.Name
            : $"{recipe.Name}/{queryName}";
        var args = new List<string>
        {
            "cdidx",
            "search",
            "--recipe",
            recipeSelector,
            "--format",
            OutputFormatIssueDrafts,
            "--limit",
            options.Limit.ToString(CultureInfo.InvariantCulture),
        };

        if (options.DbPathExplicit)
            AddReplayValueOption(args, "--db", options.DbPath);
        if (options.SourceOnly)
            args.Add("--source-only");
        else if (options.AuditScopeExplicit)
            AddReplayValueOption(args, "--audit-scope", options.AuditScope);
        if (!string.IsNullOrWhiteSpace(options.Lang))
            AddReplayValueOption(args, "--lang", options.Lang);
        foreach (var pathPattern in options.PathPatterns)
            AddReplayValueOption(args, "--path", pathPattern);
        foreach (var excludePath in options.ExcludePaths)
            AddReplayValueOption(args, "--exclude-path", excludePath);
        if (options.ExcludeTests)
            args.Add("--exclude-tests");
        if (options.IncludeGenerated)
            args.Add("--include-generated");
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
        if (options.TokenBoundary)
            args.Add("--token-boundary");
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
        if (options.TotalLimit.HasValue)
            AddReplayValueOption(args, "--total-limit", options.TotalLimit.Value.ToString(CultureInfo.InvariantCulture));
        AddReplayValueOption(args, "--snippet-lines", options.SnippetLines.ToString(CultureInfo.InvariantCulture));
        AddReplayValueOption(args, "--snippet-focus", FormatSearchSnippetFocusMode(options.SnippetFocus));
        AddReplayValueOption(args, "--max-line-width", options.MaxLineWidth.ToString(CultureInfo.InvariantCulture));
        if (options.MaxJsonBytes.HasValue)
            AddReplayValueOption(args, "--max-json-bytes", options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(options.OpenIssuesPath))
            AddReplayValueOption(args, "--open-issues", options.OpenIssuesPath);
        if (!string.IsNullOrWhiteSpace(options.OpenIssuesRepository))
            AddReplayValueOption(args, "--repo", options.OpenIssuesRepository);
        if (options.IssueState != IssueDuplicatePreflight.DefaultIssueState)
            AddReplayValueOption(args, "--issue-state", options.IssueState);
        if (options.DuplicatePreflightTuningExplicit)
        {
            if (string.Equals(options.DuplicateConfidence, IssueDuplicatePreflight.CustomDuplicateConfidence, StringComparison.Ordinal))
                AddReplayValueOption(args, "--duplicate-threshold", options.DuplicateThreshold.ToString("0.###", CultureInfo.InvariantCulture));
            else
                AddReplayValueOption(args, "--duplicate-confidence", options.DuplicateConfidence);
        }
        if (queryName == null)
        {
            foreach (var includeQuery in options.IncludeRecipeQueries)
                AddReplayValueOption(args, "--include-query", includeQuery);
            foreach (var excludeQuery in options.ExcludeRecipeQueries)
                AddReplayValueOption(args, "--exclude-query", excludeQuery);
        }
        foreach (var label in options.IssueLabels)
            AddReplayValueOption(args, "--issue-label", label);

        return string.Join(" ", args.Select(QuoteReplayShellArg));
    }

    private static void AddSearchRecipeRowSelectionReplayOptions(List<string> args, QueryCommandOptions options)
    {
        if (options.FirstPerFile)
            args.Add("--first-per-file");
        if (options.SampleSize.HasValue)
            AddReplayValueOption(args, "--sample", options.SampleSize.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddReplayValueOption(List<string> args, string optionName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        args.Add(optionName);
        args.Add(value);
    }

    private static string BuildSearchGuardReplayOptionName(SearchGuardFilter guardFilter)
    {
        var role = guardFilter.Role == SearchGuardRole.Require ? "require" : "reject";
        var direction = guardFilter.Direction == SearchGuardDirection.Before ? "before" : "after";
        return $"--{role}-{direction}";
    }

    private static string? FormatSearchGuardFilterScope(SearchGuardFilter guardFilter)
        => guardFilter.Scope switch
        {
            SearchGuardScope.Window => "window",
            SearchGuardScope.SameLine => "same_line",
            _ => null
        };

    private static string FormatSearchSnippetFocusMode(SearchSnippetFocusMode mode)
        => mode.ToString().ToLowerInvariant();

    private static string FormatSearchCursor(SearchResult result)
        => string.Create(CultureInfo.InvariantCulture, $"{result.Score:R}:{result.ChunkId}:{result.NextOffset}");

    private const string ScopedOffsetCursorPrefix = "page:v1:";

    private readonly record struct ScopedOffsetCursor(
        string Scope,
        int Offset,
        string QueryFingerprint,
        string GenerationFingerprint);

    private readonly record struct PaginationCursorContext(
        string QueryFingerprint,
        string GenerationFingerprint,
        string? ResultStableAt);

    private static PaginationCursorContext BuildPaginationCursorContext(
        DbReader reader,
        string scope,
        IEnumerable<string?> queryComponents)
    {
        var generation = reader.GetPaginationGeneration();
        return new(
            BuildScopedCursorFingerprint(queryComponents.Prepend(scope)),
            BuildScopedCursorFingerprint([generation.Identity]),
            generation.StableAt);
    }

    private static string? ValidateScopedOffsetCursor(
        QueryCommandOptions options,
        string expectedScope,
        PaginationCursorContext context)
    {
        if (options.CursorValue == null
            || !options.CursorValue.StartsWith(ScopedOffsetCursorPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        if (!TryParseScopedOffsetCursor(options.CursorValue, out var cursor)
            || !string.Equals(cursor.Scope, expectedScope, StringComparison.Ordinal))
        {
            return "The pagination cursor does not belong to this command; restart required.";
        }
        if (!string.Equals(cursor.QueryFingerprint, context.QueryFingerprint, StringComparison.Ordinal))
            return "The pagination cursor does not match the current query scope, filters, or ordering; restart required.";
        if (!string.Equals(cursor.GenerationFingerprint, context.GenerationFingerprint, StringComparison.Ordinal))
            return "The pagination cursor is stale because the index generation changed; restart required.";
        return null;
    }

    private static string FormatScopedOffsetCursor(string scope, int offset, PaginationCursorContext context)
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{scope}\n{offset}\n{context.QueryFingerprint}\n{context.GenerationFingerprint}");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return ScopedOffsetCursorPrefix + encoded;
    }

    private static string BuildScopedCursorFingerprint(IEnumerable<string?> components)
    {
        var canonical = new StringBuilder();
        foreach (var component in components)
        {
            if (component == null)
            {
                canonical.Append("-1:");
                continue;
            }
            canonical
                .Append(component.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(component);
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static bool TryParseScopedOffsetCursor(string value, out ScopedOffsetCursor cursor)
    {
        cursor = default;
        if (!value.StartsWith(ScopedOffsetCursorPrefix, StringComparison.Ordinal))
            return false;

        var encoded = value[ScopedOffsetCursorPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var paddingLength = (4 - encoded.Length % 4) % 4;
        if (paddingLength > 0)
            encoded += new string('=', paddingLength);

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = payload.Split('\n');
        if (parts.Length != 4
            || parts[0] is not ("unused" or "outline")
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || offset < 0
            || parts[2].Length != 16
            || parts[3].Length != 16
            || !parts[2].All(Uri.IsHexDigit)
            || !parts[3].All(Uri.IsHexDigit))
        {
            return false;
        }

        cursor = new ScopedOffsetCursor(parts[0], offset, parts[2], parts[3]);
        return true;
    }

    private static string FormatUnusedCursor(int offset, PaginationCursorContext context)
        => FormatScopedOffsetCursor("unused", offset, context);

    private static string FormatOutlineCursor(int offset, PaginationCursorContext context)
        => FormatScopedOffsetCursor("outline", offset, context);

    private static bool TryParseSearchCursor(string value, out SearchCursor cursor)
    {
        cursor = default;
        var lastSeparator = value.LastIndexOf(':');
        if (lastSeparator <= 0 || lastSeparator == value.Length - 1)
            return false;

        var firstSeparator = value.LastIndexOf(':', lastSeparator - 1);
        if (firstSeparator <= 0 || firstSeparator == lastSeparator - 1)
            return false;

        if (!double.TryParse(value.AsSpan(0, firstSeparator), NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
            || !double.IsFinite(score))
            return false;
        if (!long.TryParse(value.AsSpan(firstSeparator + 1, lastSeparator - firstSeparator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var chunkId)
            || chunkId < 0)
            return false;
        if (!int.TryParse(value.AsSpan(lastSeparator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
            return false;

        cursor = new SearchCursor(score, chunkId, offset);
        return true;
    }

    private static bool TryParseUnusedCursor(string value, out int offset)
    {
        offset = 0;
        const string prefix = "unused:";
        if (value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return int.TryParse(value[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
                && offset >= 0;
        }
        if (TryParseScopedOffsetCursor(value, out var cursor)
            && string.Equals(cursor.Scope, "unused", StringComparison.Ordinal))
        {
            offset = cursor.Offset;
            return true;
        }
        return false;
    }

    private static bool TryParseOutlineCursor(string value, out int offset)
    {
        offset = 0;
        const string prefix = "outline:";
        if (value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return int.TryParse(value[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
                && offset >= 0;
        }
        if (TryParseScopedOffsetCursor(value, out var cursor)
            && string.Equals(cursor.Scope, "outline", StringComparison.Ordinal))
        {
            offset = cursor.Offset;
            return true;
        }
        return false;
    }

    private static string QuoteReplayShellArg(string arg)
    {
        if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or '='))
            return arg;
        return "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string BuildAdHocSearchIssueDraftBody(
        SearchRecipeQueryResultJsonResult queryResult,
        IReadOnlyList<string> evidencePaths,
        IReadOnlyList<SearchIssueDraftEvidenceJsonResult> evidence,
        IssueDraftTriageMetadataJsonResult triage,
        QueryCommandOptions options,
        bool sourceTotalCountAuthoritative,
        int? sourceFetchLimit)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        sb.AppendLine(queryResult.Description);
        sb.AppendLine();
        sb.AppendLine("## Search query");
        sb.AppendLine(queryResult.Query);
        sb.AppendLine();
        sb.AppendLine("## Evidence paths");
        if (evidencePaths.Count == 0)
        {
            sb.AppendLine("N/A");
        }
        else
        {
            foreach (var path in evidencePaths)
                sb.AppendLine($"- {path}");
        }
        sb.AppendLine();
        AppendSearchIssueDraftEvidence(sb, evidence);
        sb.AppendLine();
        AppendSearchIssueDraftTriageMetadata(sb, triage);
        sb.AppendLine();
        AppendSearchIssueDraftOmittedResults(sb, queryResult);
        sb.AppendLine();
        sb.AppendLine("## Review guidance");
        sb.AppendLine(queryResult.FalsePositiveGuidance);
        sb.AppendLine();
        sb.AppendLine("## Replay command");
        sb.AppendLine("```sh");
        sb.AppendLine(BuildAdHocSearchIssueDraftReplayCommand(options));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Search metadata");
        sb.AppendLine("- draft_id: `search/ad-hoc`");
        sb.AppendLine($"- result_count: `{queryResult.Count}`");
        sb.AppendLine($"- source_total_count: `{(sourceTotalCountAuthoritative ? queryResult.MinimumMatchedCount.ToString(CultureInfo.InvariantCulture) : "unknown")}`");
        sb.AppendLine($"- source_minimum_count: `{queryResult.MinimumMatchedCount}`");
        sb.AppendLine($"- source_total_count_authoritative: `{sourceTotalCountAuthoritative.ToString().ToLowerInvariant()}`");
        if (sourceFetchLimit.HasValue)
            sb.AppendLine($"- source_fetch_limit: `{sourceFetchLimit.Value.ToString(CultureInfo.InvariantCulture)}`");
        sb.AppendLine($"- returned_count: `{queryResult.Count}`");
        sb.AppendLine($"- limit_per_query: `{options.Limit}`");
        sb.AppendLine($"- total_limit: `{FormatNullableIssueDraftSelectionValue(options.TotalLimit)}`");
        sb.AppendLine($"- first_per_file: `{options.FirstPerFile.ToString().ToLowerInvariant()}`");
        sb.AppendLine($"- sample: `{FormatNullableIssueDraftSelectionValue(options.SampleSize)}`");
        sb.AppendLine($"- result_limit: `{queryResult.ResultLimit}`");
        sb.AppendLine($"- omitted_count: `{queryResult.OmittedCount}`");
        if (!string.IsNullOrWhiteSpace(queryResult.SelectionReason))
        {
            sb.AppendLine($"- selection_reason: `{queryResult.SelectionReason}`");
            sb.AppendLine($"- selection_omitted_count: `{queryResult.SelectionOmittedCount.GetValueOrDefault()}`");
        }
        sb.AppendLine($"- minimum_omitted_result_count: `{queryResult.MinimumOmittedResultCount}`");
        sb.AppendLine($"- exact_substring: `{queryResult.ExactSubstring.ToString().ToLowerInvariant()}`");
        return sb.ToString().TrimEnd();
    }

    private static string FormatNullableIssueDraftSelectionValue(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string BuildAdHocSearchIssueDraftReplayCommand(QueryCommandOptions options)
    {
        var args = new List<string>
        {
            "cdidx",
            "search",
            "--query",
            options.Query!,
            "--format",
            OutputFormatIssueDrafts,
            "--limit",
            options.Limit.ToString(CultureInfo.InvariantCulture),
        };

        if (options.DbPathExplicit)
            AddReplayValueOption(args, "--db", options.DbPath);
        if (options.SourceOnly)
            args.Add("--source-only");
        if (!string.IsNullOrWhiteSpace(options.Lang))
            AddReplayValueOption(args, "--lang", options.Lang);
        foreach (var pathPattern in options.PathPatterns)
            AddReplayValueOption(args, "--path", pathPattern);
        foreach (var excludePath in options.ExcludePaths)
            AddReplayValueOption(args, "--exclude-path", excludePath);
        if (options.ExcludeTests)
            args.Add("--exclude-tests");
        if (options.IncludeGenerated)
            args.Add("--include-generated");
        if (options.Since.HasValue)
            AddReplayValueOption(args, "--since", options.Since.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (options.NoDedup)
            args.Add("--no-dedup");
        if (options.NoVisibilityRank)
            args.Add("--no-visibility-rank");
        if (options.RawFts)
            args.Add("--fts");
        if (options.Exact)
            args.Add("--exact");
        if (options.ExactSubstring)
            args.Add("--exact-substring");
        if (options.TokenBoundary)
            args.Add("--token-boundary");
        if (options.Prefix)
            args.Add("--prefix");
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
        if (options.TotalLimit.HasValue)
            AddReplayValueOption(args, "--total-limit", options.TotalLimit.Value.ToString(CultureInfo.InvariantCulture));
        AddSearchRecipeRowSelectionReplayOptions(args, options);
        AddReplayValueOption(args, "--snippet-lines", options.SnippetLines.ToString(CultureInfo.InvariantCulture));
        AddReplayValueOption(args, "--snippet-focus", FormatSearchSnippetFocusMode(options.SnippetFocus));
        AddReplayValueOption(args, "--max-line-width", options.MaxLineWidth.ToString(CultureInfo.InvariantCulture));
        if (options.MaxJsonBytes.HasValue)
            AddReplayValueOption(args, "--max-json-bytes", options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(options.OpenIssuesPath))
            AddReplayValueOption(args, "--open-issues", options.OpenIssuesPath);
        if (!string.IsNullOrWhiteSpace(options.OpenIssuesRepository))
            AddReplayValueOption(args, "--repo", options.OpenIssuesRepository);
        if (options.IssueState != IssueDuplicatePreflight.DefaultIssueState)
            AddReplayValueOption(args, "--issue-state", options.IssueState);
        if (options.DuplicatePreflightTuningExplicit)
        {
            if (string.Equals(options.DuplicateConfidence, IssueDuplicatePreflight.CustomDuplicateConfidence, StringComparison.Ordinal))
                AddReplayValueOption(args, "--duplicate-threshold", options.DuplicateThreshold.ToString("0.###", CultureInfo.InvariantCulture));
            else
                AddReplayValueOption(args, "--duplicate-confidence", options.DuplicateConfidence);
        }
        foreach (var label in options.IssueLabels)
            AddReplayValueOption(args, "--issue-label", label);
        if (!string.IsNullOrWhiteSpace(options.IssueTitle))
            AddReplayValueOption(args, "--issue-title", options.IssueTitle);

        return string.Join(" ", args.Select(QuoteReplayShellArg));
    }

    private static SearchRecipeListItemJsonResult ToSearchRecipeListItem(SearchAuditRecipe recipe, IReadOnlyList<SearchAuditRecipeQuery>? queries = null) => new(
        recipe.Name,
        recipe.Description,
        recipe.RecommendedLabels,
        recipe.DefaultScope,
        [.. recipe.DefaultPathPatterns],
        [.. recipe.DefaultExcludePaths],
        SearchRecipeSupportedFormats,
        SearchRecipeFilterSupport,
        SearchRecipeLimitSemantics,
        (queries ?? recipe.Queries).Select(query => new SearchRecipeQueryListItemJsonResult(
            query.Name,
            query.Query,
            query.Description,
            query.RecommendedLabels,
            query.FalsePositiveGuidance,
            [.. query.RiskEvidence],
            ToSearchRecipeGuardFilterJsonResults(query.GuardFilters),
            query.Severity,
            [.. query.PathPatterns],
            [.. query.ExcludePaths],
            [.. query.MatchOrigins],
            [.. query.ExcludeOrigins],
            [.. query.ResultKinds],
            [.. query.Classifiers],
            query.StringComparisonTaxonomy,
            query.BroadCatchTaxonomy,
            query.NullableContractTaxonomy,
            query.ExactSubstring)).ToList());

    private static string FormatSearchRecipeStringComparisonDomains(SearchRecipeStringComparisonTaxonomyJsonResult taxonomy)
        => string.Join(", ", taxonomy.DomainCategories.Select(category => category.Name));

    private static SearchRecipeCompactListItemJsonResult ToSearchRecipeCompactListItem(SearchAuditRecipe recipe, IReadOnlyList<SearchAuditRecipeQuery> queries) => new(
        recipe.Name,
        recipe.Description,
        recipe.DefaultScope,
        queries.Count,
        recipe.RecommendedLabels,
        [.. recipe.DefaultPathPatterns],
        [.. recipe.DefaultExcludePaths]);

    private static SearchRecipeCompactListItemJsonResult ToSearchRecipeCompactListItem(SearchRecipeListItemJsonResult recipe, IReadOnlyList<SearchRecipeQueryListItemJsonResult> queries) => new(
        recipe.Name,
        recipe.Description,
        recipe.DefaultScope,
        queries.Count,
        recipe.RecommendedLabels,
        recipe.DefaultPathPatterns,
        recipe.DefaultExcludePaths);

    private static List<SearchRecipeGuardFilterJsonResult> ToSearchRecipeGuardFilterJsonResults(IReadOnlyList<SearchGuardFilter> guardFilters)
        => guardFilters
            .Select(filter => new SearchRecipeGuardFilterJsonResult(
                filter.Role == SearchGuardRole.Require ? "require" : "reject",
                filter.Direction == SearchGuardDirection.Before ? "before" : "after",
                filter.Query,
                BuildSearchGuardReplayOptionName(filter),
                FormatSearchGuardFilterScope(filter)))
            .ToList();

}
