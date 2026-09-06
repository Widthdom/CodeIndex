using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using CodeIndex.Semantics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal static Action? SearchQueryFreshnessWorkspaceCheckForTesting;

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
                options,
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
                    jsonOptions,
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
                jsonOptions,
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
                jsonOptions,
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

    internal static int WriteJsonObjectWithOptionalByteLimit(
        string json,
        QueryCommandOptions options,
        string outputDescription,
        string hint,
        JsonSerializerOptions jsonOptions,
        string? commandName = null)
    {
        commandName ??= options.InvocationContext.CommandName;
        json = AddActiveSqliteDiagnostics(json);
        if (options.MaxJsonBytes.HasValue)
        {
            var byteCount = Encoding.UTF8.GetByteCount(json)
                            + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (byteCount > options.MaxJsonBytes.Value)
            {
                var minimumRequiredBytes = ComputeRetryableMinimumJsonBytes(
                    json,
                    options.MaxJsonBytes.Value,
                    jsonOptions);
                var retryByIncreasingBudget = minimumRequiredBytes <= MaxSearchJsonByteLimit;
                var effectiveHint = retryByIncreasingBudget
                    ? hint
                    : $"{hint} The response minimum exceeds the maximum effective --max-json-bytes value of {MaxSearchJsonByteLimit.ToString(CultureInfo.InvariantCulture)}; reduce the response size before retrying.";
                return CommandErrorWriter.WriteResponseBudgetError(
                    json: true,
                    jsonOptions,
                    commandName,
                    $"{outputDescription} JSON output is {byteCount.ToString(CultureInfo.InvariantCulture)} bytes and exceeds --max-json-bytes {options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture)}.",
                    effectiveHint,
                    requestedBytes: options.RequestedMaxJsonBytes ?? options.MaxJsonBytes.Value,
                    effectiveBytes: options.MaxJsonBytes.Value,
                    minimumRequiredBytes: minimumRequiredBytes,
                    recommendedBytes: retryByIncreasingBudget ? minimumRequiredBytes : null,
                    usage: GetUsageLineOrThrow(commandName),
                    retryByIncreasingBudget: retryByIncreasingBudget,
                    maximumEffectiveBytes: MaxSearchJsonByteLimit);
            }
        }

        Console.WriteLine(json);
        return CommandExitCodes.Success;
    }

    private static long ComputeRetryableMinimumJsonBytes(
        string json,
        int requestedBytes,
        JsonSerializerOptions jsonOptions)
    {
        var minimumRequiredBytes = (long)Encoding.UTF8.GetByteCount(json)
                                   + Encoding.UTF8.GetByteCount(Environment.NewLine);
        var payload = JsonNode.Parse(json);
        if (payload is null)
            return minimumRequiredBytes;

        for (var iteration = 0; iteration < 8; iteration++)
        {
            RewriteEmbeddedJsonByteLimit(payload, requestedBytes, minimumRequiredBytes);
            requestedBytes = checked((int)Math.Min(minimumRequiredBytes, int.MaxValue));
            var candidateJson = payload.ToJsonString(EnsureJsonNodeSerializerOptions(jsonOptions));
            var candidateBytes = (long)Encoding.UTF8.GetByteCount(candidateJson)
                                 + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (candidateBytes <= minimumRequiredBytes)
                return minimumRequiredBytes;
            minimumRequiredBytes = candidateBytes;
        }

        return minimumRequiredBytes;
    }

    private static void RewriteEmbeddedJsonByteLimit(
        JsonNode node,
        int previousBytes,
        long nextBytes)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Key is "output_byte_limit" or "max_json_bytes"
                    && property.Value is JsonValue)
                {
                    obj[property.Key] = nextBytes;
                    continue;
                }

                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    obj[property.Key] = RewriteEmbeddedMaxJsonBytesArgument(
                        text,
                        previousBytes,
                        nextBytes);
                }
                else if (property.Value is not null)
                {
                    RewriteEmbeddedJsonByteLimit(property.Value, previousBytes, nextBytes);
                }
            }
            return;
        }

        if (node is not JsonArray array)
            return;
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                array[index] = RewriteEmbeddedMaxJsonBytesArgument(text, previousBytes, nextBytes);
            }
            else if (array[index] is not null)
            {
                RewriteEmbeddedJsonByteLimit(array[index]!, previousBytes, nextBytes);
            }
        }
    }

    private static string RewriteEmbeddedMaxJsonBytesArgument(
        string value,
        int previousBytes,
        long nextBytes)
    {
        var previousText = previousBytes.ToString(CultureInfo.InvariantCulture);
        var nextText = nextBytes.ToString(CultureInfo.InvariantCulture);
        return value
            .Replace($"--max-json-bytes {previousText}", $"--max-json-bytes {nextText}", StringComparison.Ordinal)
            .Replace($"--max-json-bytes={previousText}", $"--max-json-bytes={nextText}", StringComparison.Ordinal);
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
            jsonOptions,
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
        out SearchRecipeSelectionError? error)
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
                error = new(
                    "--recipe child selection must use recipe/query form.",
                    $"Use `{options.InvocationContext.RecipeSelectorSyntax}`, with exactly one non-empty recipe and query name.");
                return false;
            }
            if (options.IncludeRecipeQueries.Count > 0 || options.ExcludeRecipeQueries.Count > 0)
            {
                error = new(
                    "--recipe recipe/query cannot be combined with --include-query or --exclude-query.",
                    "Use either `--recipe <recipe>/<query>` or a recipe name with `--include-query` / `--exclude-query`.");
                return false;
            }

            recipeName = recipeSelector[..slash];
            directQueryName = recipeSelector[(slash + 1)..];
        }

        if (!SearchAuditRecipes.TryGet(recipeName, out var recipe))
        {
            var available = string.Join(", ", SearchAuditRecipes.All.Select(r => r.Name));
            var suggestions = ConsoleUi.FindClosestMatches(recipeName, SearchAuditRecipes.All.Select(candidate => candidate.Name));
            var suggestionText = suggestions.Count > 0
                ? $" Did you mean: {string.Join(", ", suggestions.Select(candidate => $"'{candidate}'"))}?"
                : string.Empty;
            var canReplaySuggestedRecipe = suggestions.Count > 0
                && directQueryName == null
                && options.IncludeRecipeQueries.Count == 0
                && options.ExcludeRecipeQueries.Count == 0;
            var hint = canReplaySuggestedRecipe
                ? $"Retry with `{BuildSearchRecipeSelectionReplayCommand(suggestions[0], options)}`."
                : suggestions.Count > 0
                    ? $"Correct the recipe name while retaining the requested query selectors, or run `{options.InvocationContext.RecipeDiscoveryCommand}` to inspect the available recipes."
                : $"Run `{options.InvocationContext.RecipeDiscoveryCommand}` to choose an available recipe.";
            error = new(
                $"unknown {options.InvocationContext.CommandName} recipe '{recipeName}'. Available recipes: {available}.{suggestionText}",
                hint);
            return false;
        }

        var queryBySelector = BuildRecipeQuerySelectorMap(recipe);
        var availableQueries = string.Join(", ", recipe.Queries.Select(query => query.Name));
        if (!TryValidateRecipeQuerySelectors(
                queryBySelector,
                availableQueries,
                recipe.Name,
                options.IncludeRecipeQueries,
                "--include-query",
                out var invalidSelector,
                out var selectorError))
        {
            error = BuildUnknownRecipeQueryError(
                recipe,
                invalidSelector!,
                selectorError!,
                options,
                SearchRecipeQuerySelectorMode.Include);
            return false;
        }
        if (!TryValidateRecipeQuerySelectors(
                queryBySelector,
                availableQueries,
                recipe.Name,
                options.ExcludeRecipeQueries,
                "--exclude-query",
                out invalidSelector,
                out selectorError))
        {
            error = BuildUnknownRecipeQueryError(
                recipe,
                invalidSelector!,
                selectorError!,
                options,
                SearchRecipeQuerySelectorMode.Exclude);
            return false;
        }
        if (directQueryName != null && !queryBySelector.ContainsKey(directQueryName))
        {
            error = BuildUnknownRecipeQueryError(
                recipe,
                directQueryName,
                $"unknown recipe query '{directQueryName}' for recipe '{recipe.Name}'. Available queries: {availableQueries}.",
                options,
                SearchRecipeQuerySelectorMode.Direct);
            return false;
        }

        var selected = new List<SearchAuditRecipeQuery>();
        if (directQueryName != null)
        {
            selected.Add(queryBySelector[directQueryName]);
        }
        else if (options.IncludeRecipeQueries.Count > 0)
        {
            foreach (var queryName in options.IncludeRecipeQueries)
            {
                var query = queryBySelector[queryName];
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
            var excludeSet = options.ExcludeRecipeQueries
                .Select(selector => queryBySelector[selector].Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = selected
                .Where(query => !excludeSet.Contains(query.Name))
                .ToList();
        }

        if (selected.Count == 0)
        {
            error = new(
                $"recipe query selection for '{recipe.Name}' is empty after applying --include-query/--exclude-query.",
                $"Retry with `{BuildSearchRecipeSelectionReplayCommand(recipe.Name, options)}` to run the complete active recipe.");
            return false;
        }

        selection = new SearchRecipeSelection(recipe, selected);
        return true;
    }

    private static bool TryValidateRecipeQuerySelectors(
        IReadOnlyDictionary<string, SearchAuditRecipeQuery> queryBySelector,
        string availableQueries,
        string recipeName,
        IReadOnlyList<string> selectors,
        string optionName,
        out string? invalidSelector,
        out string? error)
    {
        foreach (var selector in selectors)
        {
            if (!queryBySelector.ContainsKey(selector))
            {
                invalidSelector = selector;
                error = $"unknown recipe query '{selector}' for recipe '{recipeName}' in {optionName}. Available queries: {availableQueries}.";
                return false;
            }
        }

        invalidSelector = null;
        error = null;
        return true;
    }

    private static Dictionary<string, SearchAuditRecipeQuery> BuildRecipeQuerySelectorMap(SearchAuditRecipe recipe)
    {
        var queryBySelector = new Dictionary<string, SearchAuditRecipeQuery>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in recipe.Queries)
            queryBySelector.TryAdd(query.Name, query);
        foreach (var query in recipe.Queries)
        {
            foreach (var alias in query.Aliases.Concat(query.DeprecatedAliases))
                queryBySelector.TryAdd(alias, query);
        }
        return queryBySelector;
    }

    private static SearchRecipeSelectionError BuildUnknownRecipeQueryError(
        SearchAuditRecipe recipe,
        string rawSelector,
        string message,
        QueryCommandOptions options,
        SearchRecipeQuerySelectorMode selectorMode)
    {
        var suggestions = BuildSearchRecipeQuerySuggestions(recipe, rawSelector);
        var suggestionText = suggestions.Count > 0
            ? $" Did you mean: {string.Join(", ", suggestions.Select(candidate => $"'{candidate}'"))}?"
            : string.Empty;
        var suggestedQueryName = suggestions.FirstOrDefault();
        string hint;
        if (suggestedQueryName != null)
        {
            var replaySelector = selectorMode == SearchRecipeQuerySelectorMode.Direct
                ? $"{recipe.Name}/{suggestedQueryName}"
                : recipe.Name;
            hint = $"Retry with `{BuildSearchRecipeSelectionReplayCommand(
                replaySelector,
                options,
                recipe,
                selectorMode,
                rawSelector,
                suggestedQueryName)}`.";
        }
        else if (recipe.Queries.Count > 0)
        {
            hint = $"Choose one of the available queries listed above for recipe '{recipe.Name}'; no retry command was generated because no close match was found.";
        }
        else
        {
            hint = $"Recipe '{recipe.Name}' has no runnable queries; run `{options.InvocationContext.RecipeDiscoveryCommand}` to choose another recipe.";
        }
        return new(message + suggestionText, hint);
    }

    internal static IReadOnlyList<string> BuildSearchRecipeQuerySuggestions(
        SearchAuditRecipe recipe,
        string rawSelector)
    {
        var canonicalBySelector = BuildRecipeQuerySelectorMap(recipe)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Name,
                StringComparer.OrdinalIgnoreCase);
        return ConsoleUi.FindClosestMatches(rawSelector, canonicalBySelector.Keys, maxResults: 12)
            .Select(selector => canonicalBySelector[selector])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    private static string BuildSearchRecipeSelectionReplayCommand(
        string recipeSelector,
        QueryCommandOptions options,
        SearchAuditRecipe? recipe = null,
        SearchRecipeQuerySelectorMode selectorMode = SearchRecipeQuerySelectorMode.Direct,
        string? invalidSelector = null,
        string? suggestedQueryName = null)
    {
        var args = new List<string>();
        options.InvocationContext.AddRecipeCommandPrefix(args, recipeSelector);
        args.Add("--format");
        args.Add(OutputFormatCompact);
        AddReplayValueOption(args, "--limit", options.Limit.ToString(CultureInfo.InvariantCulture));
        AddSearchRecipeCompactReplayOptions(args, options, includeRecipeQuerySelectors: false);
        if (recipe != null && selectorMode != SearchRecipeQuerySelectorMode.Direct)
        {
            AddNormalizedRecipeQueryReplaySelectors(
                args,
                recipe,
                options,
                selectorMode,
                invalidSelector!,
                suggestedQueryName);
        }
        return string.Join(" ", args.Select(QuoteReplayShellArg));
    }

    private static void AddNormalizedRecipeQueryReplaySelectors(
        List<string> args,
        SearchAuditRecipe recipe,
        QueryCommandOptions options,
        SearchRecipeQuerySelectorMode invalidSelectorMode,
        string invalidSelector,
        string? suggestedQueryName)
    {
        var queryBySelector = BuildRecipeQuerySelectorMap(recipe);
        AddSelectors("--include-query", options.IncludeRecipeQueries, SearchRecipeQuerySelectorMode.Include);
        AddSelectors("--exclude-query", options.ExcludeRecipeQueries, SearchRecipeQuerySelectorMode.Exclude);

        void AddSelectors(
            string optionName,
            IReadOnlyList<string> selectors,
            SearchRecipeQuerySelectorMode selectorMode)
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var selector in selectors)
            {
                string? canonicalName;
                if (selectorMode == invalidSelectorMode
                    && string.Equals(selector, invalidSelector, StringComparison.OrdinalIgnoreCase))
                {
                    canonicalName = suggestedQueryName;
                }
                else
                {
                    canonicalName = queryBySelector.TryGetValue(selector, out var query)
                        ? query.Name
                        : selector;
                }

                if (canonicalName != null && emitted.Add(canonicalName))
                    AddReplayValueOption(args, optionName, canonicalName);
            }
        }
    }

    private enum SearchRecipeQuerySelectorMode
    {
        Direct,
        Include,
        Exclude,
    }

    private sealed record SearchRecipeSelectionError(
        string Message,
        string Hint);

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
        foreach (var alias in query.Aliases)
            yield return alias;
        foreach (var alias in query.DeprecatedAliases)
            yield return alias;
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
                selectionError!.Message,
                options,
                selectionError.Hint);
            return CommandExitCodes.UsageError;
        }
        var recipe = selection.Recipe;
        var scope = BuildSearchRecipeScope(recipe, options, selection.Queries);
        if (options.SearchCursor.HasValue && selection.Queries.Count != 1)
        {
            WriteUsageError(
                "--cursor requires exactly one selected recipe query.",
                options,
                "Use `--recipe recipe/query` or a single `--include-query` value with --cursor.");
            return CommandExitCodes.UsageError;
        }

        string? ndjsonTerminalLine = null;
        return WithDb(options, jsonOptions, reader =>
        {
            EnsureSearchRecipeCoverage(reader, scope, options);
            if (options.ResultsOnly || options.SearchFields != null || (options.Json && options.JsonOutputFormatExplicit && options.JsonOutputFormat == JsonOutputFormatNdjson))
            {
                var rowQueryResults = CollectSearchRecipeQueryResults(
                    reader,
                    selection.Queries,
                    scope,
                    options,
                    userExact,
                    freshnessContext: null,
                    includeAuditClassifications: options.SearchFields == null,
                    out _,
                    out var rowMinimumMatchedTotal,
                    out _,
                    out _);
                var stream = WriteRecipeSearchResultRows(
                    reader,
                    recipe.Name,
                    rowQueryResults,
                    rowMinimumMatchedTotal,
                    scope,
                    options,
                    GetCompactJsonOptions(jsonOptions));
                ndjsonTerminalLine = stream.TerminalLine;
                return stream.ExitCode;
            }

            if (options.OutputFormat == OutputFormatCompact)
            {
                var compactFreshnessContext = BuildSearchRecipeFreshnessContext(
                    reader,
                    recipe,
                    selection.Queries,
                    options);
                var compactQueryResults = CollectSearchRecipeCompactQueryResults(
                    reader,
                    selection.Queries,
                    scope,
                    options,
                    userExact,
                    compactFreshnessContext,
                    out var compactTotal,
                    out var compactFreshnessObservations,
                    out var compactHasFailures);
                var compactPayload = BuildSearchRecipeCompactRunPayload(
                    recipe,
                    selection.Queries,
                    scope,
                    options,
                    jsonOptions,
                    compactQueryResults,
                    compactTotal,
                    compactFreshnessContext,
                    compactFreshnessObservations);
                var compactJson = compactPayload.ToJsonString(GetJsonNodeSerializationOptions(jsonOptions));
                return CompleteSearchRecipeOutput(
                    WriteJsonObjectWithOptionalByteLimit(
                        compactJson,
                        options,
                        "recipe compact",
                        $"Reduce --limit or --total-limit, select one child query with {options.InvocationContext.RecipeCursorSelectorSyntax}, stream rows with --json=ndjson, or increase --max-json-bytes.",
                        jsonOptions),
                    compactHasFailures);
            }

            var freshnessContext = BuildSearchRecipeFreshnessContext(
                reader,
                recipe,
                selection.Queries,
                options);
            var queryResults = CollectSearchRecipeQueryResults(
                reader,
                selection.Queries,
                scope,
                options,
                userExact,
                freshnessContext,
                includeAuditClassifications: options.Json && options.OutputFormat != OutputFormatSarif,
                out var total,
                out _,
                out var freshnessObservations,
                out var hasFailures);

            if (options.OutputFormat == OutputFormatSarif)
            {
                var sarifExitCode = WriteSearchRecipeSarif(
                    recipe,
                    scope,
                    queryResults,
                    total,
                    options,
                    jsonOptions,
                    freshnessContext,
                    freshnessObservations);
                return CompleteSearchRecipeOutput(sarifExitCode, hasFailures);
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
                            BuildSearchRecipeRunSummary(
                                queryResults,
                                options.Limit,
                                options.TotalLimit,
                                total,
                                options.InvocationContext,
                                freshnessContext,
                                freshnessObservations),
                            queryResults),
                        CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeRunJsonResult);
                return CompleteSearchRecipeOutput(
                    WriteJsonObjectWithOptionalByteLimit(
                        json,
                        options,
                        "recipe search",
                        "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.",
                        jsonOptions),
                    hasFailures);
            }

            Console.WriteLine($"Recipe: {recipe.Name}");
            Console.WriteLine(recipe.Description);
            Console.WriteLine($"Scope: {scope.Name}");
            if (scope.PathPatterns.Count > 0)
                Console.WriteLine($"Paths: {string.Join(", ", scope.PathPatterns)}");
            if (scope.ExcludePaths.Count > 0)
                Console.WriteLine($"Excludes: {string.Join(", ", scope.ExcludePaths)}");
            Console.WriteLine($"Exclude tests: {scope.ExcludeTests.ToString().ToLowerInvariant()}");
            WriteSearchRecipeCoverageText(scope.Coverage);
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

            WriteSearchRecipeFreshnessText(BuildSearchRecipeQueryFreshness(
                freshnessContext,
                freshnessObservations));
            CommandErrorWriter.WriteStderr($"({total} recipe results across {selection.Queries.Count} queries)");
            return CompleteSearchRecipeOutput(CommandExitCodes.Success, hasFailures);
        }, _ =>
        {
            if (ndjsonTerminalLine != null && !options.ResultsOnly)
                Console.WriteLine(ndjsonTerminalLine);
        });
    }

    private static int WriteSearchRecipeSarif(
        SearchAuditRecipe recipe,
        SearchRecipeScopeJsonResult scope,
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        int total,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        SearchQueryFreshnessContext freshnessContext,
        IReadOnlyList<SearchQueryFreshnessObservation> freshnessObservations)
    {
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

        var completeProperties = BuildSearchRecipeSarifRunProperties(
            recipe,
            scope,
            queryResults,
            options,
            jsonOptions,
            freshnessContext,
            freshnessObservations,
            items,
            items.Count);
        if (!options.MaxJsonBytes.HasValue)
        {
            WriteSarif(items, jsonOptions, runProperties: completeProperties);
            return CommandExitCodes.Success;
        }

        var completeDocumentBytes = GetSarifDocumentUtf8LineByteCount(
            items,
            jsonOptions,
            runProperties: completeProperties);
        var byteLimit = options.MaxJsonBytes.Value;
        if (completeDocumentBytes <= byteLimit)
        {
            WriteSarifDocument(Console.Out, items, jsonOptions, "warning", completeProperties);
            Console.WriteLine();
            return CommandExitCodes.Success;
        }

        if (items.Count == 0)
        {
            WriteUsageError(
                $"audit SARIF output requires {completeDocumentBytes.ToString(CultureInfo.InvariantCulture)} UTF-8 bytes including the final newline, which exceeds --max-json-bytes {byteLimit.ToString(CultureInfo.InvariantCulture)}.",
                options,
                "Increase --max-json-bytes to at least the reported minimum; no partial SARIF was written.");
            return CommandExitCodes.UsageError;
        }

        JsonObject? boundedProperties = null;
        var emittedResultCount = -1;
        var low = 0;
        var high = items.Count - 1;
        while (low <= high)
        {
            var candidate = low + ((high - low) / 2);
            var firstOmittedResultBytes = GetSarifResultUtf8ByteCount(items[candidate], jsonOptions);
            var candidateProperties = BuildSearchRecipeSarifRunProperties(
                recipe,
                scope,
                queryResults,
                options,
                jsonOptions,
                freshnessContext,
                freshnessObservations,
                items,
                candidate,
                completeDocumentBytes,
                firstOmittedResultBytes);
            var candidateDocumentBytes = GetSarifDocumentUtf8LineByteCount(
                items,
                jsonOptions,
                runProperties: candidateProperties,
                resultCount: candidate);
            if (candidateDocumentBytes > byteLimit)
            {
                candidateProperties = BuildSearchRecipeSarifRunProperties(
                    recipe,
                    scope,
                    queryResults,
                    options,
                    jsonOptions,
                    freshnessContext,
                    freshnessObservations,
                    items,
                    candidate,
                    completeDocumentBytes,
                    firstOmittedResultBytes,
                    omitCoverage: true);
                candidateDocumentBytes = GetSarifDocumentUtf8LineByteCount(
                    items,
                    jsonOptions,
                    runProperties: candidateProperties,
                    resultCount: candidate);
            }
            if (candidateDocumentBytes <= byteLimit)
            {
                emittedResultCount = candidate;
                boundedProperties = candidateProperties;
                low = candidate + 1;
            }
            else
            {
                high = candidate - 1;
            }
        }

        if (boundedProperties == null)
        {
            var minimumBoundedDocumentBytes = GetMinimumBoundedSearchRecipeSarifBytes(
                recipe,
                scope,
                queryResults,
                options,
                jsonOptions,
                freshnessContext,
                freshnessObservations,
                items,
                completeDocumentBytes,
                byteLimit);
            WriteUsageError(
                $"minimum schema-valid bounded audit SARIF output requires {minimumBoundedDocumentBytes.ToString(CultureInfo.InvariantCulture)} UTF-8 bytes including the final newline, which exceeds --max-json-bytes {byteLimit.ToString(CultureInfo.InvariantCulture)}.",
                options,
                "Increase --max-json-bytes to at least the reported minimum; no partial SARIF was written.");
            return CommandExitCodes.UsageError;
        }

        Console.WriteLine(
            BuildSarifDocument(
                items,
                jsonOptions,
                runProperties: boundedProperties,
                resultCount: emittedResultCount));
        return emittedResultCount < items.Count && !options.AllowPartial
            ? CommandExitCodes.PartialResult
            : CommandExitCodes.Success;
    }

    private static JsonObject BuildSearchRecipeSarifRunProperties(
        SearchAuditRecipe recipe,
        SearchRecipeScopeJsonResult scope,
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        SearchQueryFreshnessContext freshnessContext,
        IReadOnlyList<SearchQueryFreshnessObservation> freshnessObservations,
        IReadOnlyList<SarifLocation> items,
        int emittedResultCount,
        int? minimumCompleteBytes = null,
        int? firstOmittedResultBytes = null,
        int? byteLimitOverride = null,
        bool omitCoverage = false)
    {
        var summary = BuildSearchRecipeRunSummary(
            queryResults,
            options.Limit,
            options.TotalLimit,
            items.Count,
            options.InvocationContext,
            freshnessContext,
            freshnessObservations);
        var bounded = minimumCompleteBytes.HasValue;
        var emittedByRule = items
            .Take(emittedResultCount)
            .GroupBy(item => item.RuleId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var querySummaries = new JsonArray();
        var cursorEligibleQueryNames = new HashSet<string>(StringComparer.Ordinal);
        var truncatedQueryCount = 0;
        foreach (var queryResult in queryResults)
        {
            var ruleId = $"{recipe.Name}/{queryResult.Name}";
            var emittedForQuery = bounded && emittedByRule.TryGetValue(ruleId, out var count)
                ? count
                : bounded
                    ? 0
                    : queryResult.Count;
            var omittedByByteBudget = Math.Max(0, queryResult.Count - emittedForQuery);
            var truncated = queryResult.Truncated || omittedByByteBudget > 0;
            if (truncated)
                truncatedQueryCount++;
            if (omittedByByteBudget == 0 && !string.IsNullOrWhiteSpace(queryResult.NextCursor))
                cursorEligibleQueryNames.Add(queryResult.Name);
            var querySummary = new JsonObject
            {
                ["name"] = queryResult.Name,
                ["result_count"] = emittedForQuery,
                ["result_limit"] = queryResult.ResultLimit,
                ["truncated"] = truncated,
                ["minimum_omitted_result_count"] = queryResult.MinimumOmittedResultCount + omittedByByteBudget,
                ["next_cursor"] = omittedByByteBudget == 0 ? queryResult.NextCursor : null,
            };
            if (bounded)
            {
                querySummary["source_result_count"] = queryResult.SourceTotal;
                querySummary["source_result_count_authoritative"] = queryResult.SourceTotalAuthoritative;
                querySummary["omitted_by_byte_budget"] = omittedByByteBudget;
                querySummary["replay_command"] = BuildSearchRecipeSarifReplayCommand(
                    ruleId,
                    options,
                    minimumCompleteBytes!.Value,
                    includeRecipeQuerySelectors: false);
            }
            querySummaries.Add(querySummary);
        }

        var omittedByByteBudgetTotal = Math.Max(0, items.Count - emittedResultCount);
        var runProperties = new JsonObject
        {
            ["format"] = "audit-recipe",
            ["recipe"] = recipe.Name,
            ["scope"] = BuildSearchRecipeScopeNodeForByteBudget(
                scope,
                jsonOptions,
                omitCoveragePathSamples: bounded,
                omitCoverage: omitCoverage),
            ["query_count"] = summary.QueryFreshness.Queries.Count,
            ["result_count"] = emittedResultCount,
            ["query_freshness"] = JsonSerializer.SerializeToNode(
                summary.QueryFreshness,
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeQueryFreshnessJsonResult),
            ["limit_per_query"] = options.Limit,
            ["total_limit"] = JsonValue.Create(options.TotalLimit),
            ["queries"] = querySummaries,
            ["truncation"] = new JsonObject
            {
                ["truncated"] = truncatedQueryCount > 0,
                ["truncated_query_count"] = truncatedQueryCount,
                ["minimum_omitted_result_count"] = summary.MinimumOmittedResultCount + omittedByByteBudgetTotal,
            },
        };
        if (!bounded)
            return runProperties;

        runProperties["source_result_count"] = queryResults.Sum(query => query.SourceTotal);
        runProperties["source_result_count_authoritative"] = queryResults.All(query => query.SourceTotalAuthoritative);
        runProperties["cursoring_available"] = cursorEligibleQueryNames.Count > 0;
        var recipeSelector = options.RecipeName ?? recipe.Name;
        runProperties["replay_command"] = BuildSearchRecipeSarifReplayCommand(
            recipeSelector,
            options,
            minimumCompleteBytes!.Value,
            includeRecipeQuerySelectors: true);
        runProperties["next_commands"] = BuildSearchRecipeSarifNextCommands(
            recipeSelector,
            recipe.Name,
            queryResults,
            cursorEligibleQueryNames,
            options,
            minimumCompleteBytes.Value);
        runProperties["byte_budget"] = new JsonObject
        {
            ["max_json_bytes"] = byteLimitOverride ?? options.MaxJsonBytes!.Value,
            ["max_supported_json_bytes"] = MaxSearchJsonByteLimit,
            ["measurement"] = "utf8_bytes_including_final_newline",
            ["strategy"] = "omit_whole_results",
            ["minimum_complete_bytes"] = minimumCompleteBytes.Value,
            ["complete_output_exceeds_max_json_bytes"] = minimumCompleteBytes.Value > MaxSearchJsonByteLimit,
            ["emitted_result_count"] = emittedResultCount,
            ["omitted_result_count"] = omittedByByteBudgetTotal,
            ["first_omitted_result_bytes"] = firstOmittedResultBytes,
            ["truncated"] = omittedByByteBudgetTotal > 0,
        };
        return runProperties;
    }

    private static JsonNode? BuildSearchRecipeScopeNodeForByteBudget(
        SearchRecipeScopeJsonResult scope,
        JsonSerializerOptions jsonOptions,
        bool omitCoveragePathSamples,
        bool omitCoverage = false)
    {
        static long? ReadLong(JsonNode? node)
            => node is JsonValue value && value.TryGetValue<long>(out var number)
                ? number
                : null;

        var scopeNode = JsonSerializer.SerializeToNode(
            scope,
            CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeScopeJsonResult);
        if (omitCoverage && scopeNode is JsonObject scopeObject)
        {
            scopeObject.Remove("coverage");
            scopeObject["coverage_omitted_reason"] = "response_byte_budget";
            return scopeObject;
        }
        if (!omitCoveragePathSamples
            || scopeNode?["coverage"] is not JsonObject coverage)
        {
            return scopeNode;
        }

        scopeNode.AsObject().Remove("recipe_default_path_patterns");
        scopeNode.AsObject().Remove("recipe_default_exclude_paths");
        scopeNode.AsObject()["metadata_omitted_reason"] = "response_byte_budget";
        if (coverage["human_review"] is JsonObject humanReview)
        {
            humanReview.Remove("reason");
            humanReview["reason_omitted"] = "response_byte_budget";
        }

        foreach (var name in new[] { "included", "excluded", "unindexed" })
        {
            if (coverage[name] is not JsonObject set)
                continue;

            var hadPaths = set["paths"] is JsonArray { Count: > 0 };
            var count = ReadLong(set["count"]);
            var lowerBound = ReadLong(set["count_lower_bound"]);
            var upperBound = ReadLong(set["count_upper_bound"]);
            var wasTruncated = set["paths_truncated"] is JsonValue truncatedNode
                && truncatedNode.TryGetValue<bool>(out var truncated)
                && truncated;
            var hasKnownFiles = count > 0 || lowerBound > 0 || upperBound > 0 || hadPaths;
            set["paths"] = new JsonArray();
            set["path_limit"] = 0;
            set["paths_truncated"] = wasTruncated || hasKnownFiles;
            set["path_sample_omitted_reason"] = "response_byte_budget";
            if (set["count_authoritative"] is JsonValue authoritativeNode
                && authoritativeNode.TryGetValue<bool>(out var authoritative)
                && authoritative
                && count.HasValue)
            {
                set["omitted_path_count"] = count.Value;
                set["omitted_path_count_authoritative"] = true;
            }
            else
            {
                set.Remove("omitted_path_count");
                set["omitted_path_count_authoritative"] = false;
            }
        }

        return scopeNode;
    }

    private static int GetMinimumBoundedSearchRecipeSarifBytes(
        SearchAuditRecipe recipe,
        SearchRecipeScopeJsonResult scope,
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        SearchQueryFreshnessContext freshnessContext,
        IReadOnlyList<SearchQueryFreshnessObservation> freshnessObservations,
        IReadOnlyList<SarifLocation> items,
        int minimumCompleteBytes,
        int requestedByteLimit)
    {
        var minimum = requestedByteLimit;
        var firstOmittedResultBytes = GetSarifResultUtf8ByteCount(items[0], jsonOptions);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var properties = BuildSearchRecipeSarifRunProperties(
                recipe,
                scope,
                queryResults,
                options,
                jsonOptions,
                freshnessContext,
                freshnessObservations,
                items,
                emittedResultCount: 0,
                minimumCompleteBytes,
                firstOmittedResultBytes,
                byteLimitOverride: minimum,
                omitCoverage: true);
            var required = GetSarifDocumentUtf8LineByteCount(
                items,
                jsonOptions,
                runProperties: properties,
                resultCount: 0);
            if (required <= minimum)
                return minimum;
            minimum = required;
        }
        return minimum;
    }

    private static JsonArray BuildSearchRecipeSarifNextCommands(
        string recipeSelector,
        string recipeName,
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        IReadOnlySet<string> cursorEligibleQueryNames,
        QueryCommandOptions options,
        int minimumCompleteBytes)
    {
        var commands = new JsonArray
        {
            BuildSearchRecipeSarifReplayCommand(
                recipeSelector,
                options,
                minimumCompleteBytes,
                includeRecipeQuerySelectors: true),
        };
        foreach (var query in queryResults
                     .Where(query => cursorEligibleQueryNames.Contains(query.Name))
                     .Take(3))
        {
            commands.Add(BuildSearchRecipeCompactReplayCommand(
                $"{recipeName}/{query.Name}",
                options,
                query.NextCursor,
                resultsOnly: false,
                includeRecipeQuerySelectors: false));
        }
        return commands;
    }

    private static string BuildSearchRecipeSarifReplayCommand(
        string recipeSelector,
        QueryCommandOptions options,
        int maxJsonBytes,
        bool includeRecipeQuerySelectors)
    {
        var args = new List<string>();
        options.InvocationContext.AddRecipeCommandPrefix(args, recipeSelector);
        args.Add("--format");
        args.Add(OutputFormatSarif);
        if (!string.IsNullOrWhiteSpace(options.CursorValue))
            AddReplayValueOption(args, "--cursor", options.CursorValue);
        AddReplayValueOption(args, "--limit", options.Limit.ToString(CultureInfo.InvariantCulture));
        AddSearchRecipeCompactReplayOptions(
            args,
            options,
            includeRecipeQuerySelectors,
            includeMaxJsonBytes: false);
        if (maxJsonBytes <= MaxSearchJsonByteLimit)
            AddReplayValueOption(args, "--max-json-bytes", maxJsonBytes.ToString(CultureInfo.InvariantCulture));
        return string.Join(" ", args.Select(QuoteReplayShellArg));
    }

    internal static string BuildSearchRecipeSarifReplayCommandForTests(
        string recipeSelector,
        QueryCommandOptions options,
        int maxJsonBytes)
        => BuildSearchRecipeSarifReplayCommand(
            recipeSelector,
            options,
            maxJsonBytes,
            includeRecipeQuerySelectors: true);

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
        int compactTotal,
        SearchQueryFreshnessContext freshnessContext,
        IReadOnlyList<SearchQueryFreshnessObservation> freshnessObservations)
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
            BuildSearchRecipeRunSummary(
                compactQueryResults,
                options.Limit,
                options.TotalLimit,
                compactTotal,
                options.InvocationContext,
                freshnessContext,
                freshnessObservations),
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
        var args = BuildSearchRecipeCompactReplayArguments(
            recipeSelector,
            options,
            cursor,
            resultsOnly,
            includeRecipeQuerySelectors);
        return ExcerptRecoveryCommandFormatter.RenderDisplayCommandForCurrentShell(args);
    }

    private static List<string> BuildSearchRecipeCompactReplayArguments(
        string recipeSelector,
        QueryCommandOptions options,
        string? cursor,
        bool resultsOnly,
        bool includeRecipeQuerySelectors)
    {
        var args = new List<string>();
        options.InvocationContext.AddRecipeCommandPrefix(
            args,
            recipeSelector,
            resultsOnly
                ? RecipeReplayOutputCapability.ResultsOnlyNdjson
                : RecipeReplayOutputCapability.Default);
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
        if (resultsOnly && !options.MaxJsonBytes.HasValue)
            AddReplayValueOption(args, "--max-json-bytes", "<bytes>");
        return args;
    }

    internal static (
        IReadOnlyList<string> Argv,
        string PosixSh,
        string PowerShell,
        string CurrentShell) BuildSearchRecipeCompactReplayCommandForTests(
            string recipeSelector,
            QueryCommandOptions options,
            string? cursor,
            bool resultsOnly,
            bool includeRecipeQuerySelectors)
    {
        var argv = BuildSearchRecipeCompactReplayArguments(
            recipeSelector,
            options,
            cursor,
            resultsOnly,
            includeRecipeQuerySelectors);
        return (
            argv,
            ExcerptRecoveryCommandFormatter.RenderDisplayCommand(argv, RecoveryCommandShell.PosixSh),
            ExcerptRecoveryCommandFormatter.RenderDisplayCommand(argv, RecoveryCommandShell.PowerShell),
            ExcerptRecoveryCommandFormatter.RenderDisplayCommandForCurrentShell(argv));
    }

    private static void AddSearchRecipeCompactReplayOptions(
        List<string> args,
        QueryCommandOptions options,
        bool includeRecipeQuerySelectors,
        bool includeMaxJsonBytes = true)
    {
        if (options.DbPathExplicit)
            AddReplayValueOption(args, "--db", options.DbPath);
        if (string.Equals(options.DataDirSource, DbPathResolver.DataDirSourceFlag, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(options.DataDir))
            AddReplayValueOption(args, "--data-dir", options.DataDir);
        if (options.SourceOnly)
            args.Add("--source-only");
        else if (options.AuditScopeExplicit)
            AddReplayValueOption(args, "--audit-scope", options.AuditScope);
        if (!string.IsNullOrWhiteSpace(options.Lang))
            AddReplayValueOption(args, "--lang", options.Lang);
        if (!string.IsNullOrWhiteSpace(options.SolutionFilter))
            AddReplayValueOption(args, "--solution", options.SolutionFilter);
        foreach (var projectFilter in options.ProjectFilters)
            AddReplayValueOption(args, "--project", projectFilter);
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
        if (includeMaxJsonBytes && options.MaxJsonBytes.HasValue)
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
                selectionError!.Message,
                options,
                selectionError.Hint);
            return CommandExitCodes.UsageError;
        }

        var recipe = selection.Recipe;
        var scope = BuildSearchRecipeScope(recipe, options, selection.Queries);
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
                    "Reduce --limit or increase --max-json-bytes.",
                    jsonOptions);
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
        SearchRecipeScopeJsonResult scope,
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
        var selectors = AggregateSearchRowSelectors(queryResults.SelectMany(query => query.Selectors));
        var hasSelectors = selectors.Count > 0;
        return WriteNdjsonStream(
            records,
            totalCount,
            options,
            ndjsonOptions,
            reader,
            options.InvocationContext.CommandName,
            limitTruncated,
            "Increase --limit or --total-limit, select one recipe query, or narrow the recipe scope.",
            totalCountAuthoritative: false,
            truncationReason: limitTruncated ? "limit" : null,
            selectionReason: selectionReason,
            selectionOmittedCount: selectionReason != null ? selectionOmittedCount : null,
            sourceTotal: hasSelectors ? queryResults.Sum(query => query.SourceTotal) : null,
            sourceTotalAuthoritative: hasSelectors
                ? queryResults.All(query => query.SourceTotalAuthoritative)
                : null,
            selectedTotal: hasSelectors ? queryResults.Sum(query => query.SelectedTotal) : null,
            selectorOmittedCount: hasSelectors ? queryResults.Sum(query => query.SelectorOmittedCount) : null,
            limitOmittedCount: hasSelectors ? queryResults.Sum(query => query.LimitOmittedCount) : null,
            selectors: hasSelectors ? selectors : null,
            terminalMetadata: new JsonObject
            {
                ["scope"] = BuildSearchRecipeScopeNodeForByteBudget(
                    scope,
                    ndjsonOptions,
                    omitCoveragePathSamples: false),
            },
            terminalMetadataFallback: new JsonObject
            {
                ["scope"] = BuildSearchRecipeScopeNodeForByteBudget(
                    scope,
                    ndjsonOptions,
                    omitCoveragePathSamples: true),
            },
            terminalMetadataMinimalFallback: new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["name"] = scope.Name,
                    ["coverage_omitted_reason"] = "response_byte_budget",
                },
            });
    }

    private static List<SearchRowSelectorJsonResult> AggregateSearchRowSelectors(
        IEnumerable<SearchRowSelectorJsonResult> selectors)
        => selectors
            .GroupBy(
                selector => (selector.Mode, selector.SampleSize, selector.SampleMode, selector.Seed))
            .Select(group => new SearchRowSelectorJsonResult(
                group.Key.Mode,
                group.All(selector => selector.Applied),
                group.Sum(selector => selector.InputTotal),
                group.Sum(selector => selector.OutputTotal),
                group.Sum(selector => selector.OmittedCount),
                group.Key.SampleSize,
                group.Key.SampleMode,
                group.Key.Seed))
            .ToList();

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
                selectionError!.Message,
                options,
                selectionError.Hint);
            return CommandExitCodes.UsageError;
        }
        var recipe = selection.Recipe;
        var scope = BuildSearchRecipeScope(recipe, options, selection.Queries);
        var preflightResult = IssueDuplicatePreflight.TryLoadAsync(
                options.OpenIssuesPath,
                options.OpenIssuesRepository,
                cancellationToken,
                options.IssueState)
            .GetAwaiter()
            .GetResult();
        if (!preflightResult.Loaded)
            return WriteIssueDuplicatePreflightFailure(
                preflightResult,
                options,
                jsonOptions,
                options.InvocationContext.CommandName);
        var preflight = preflightResult.Preflight;

        return WithDb(options, jsonOptions, reader =>
        {
            var freshnessContext = BuildSearchRecipeFreshnessContext(
                reader,
                recipe,
                selection.Queries,
                options);
            var queryResults = CollectSearchRecipeQueryResults(
                reader,
                selection.Queries,
                scope,
                options,
                userExact,
                freshnessContext,
                includeAuditClassifications: false,
                out var total,
                out _,
                out var freshnessObservations,
                out var hasFailures);
            if (options.SummaryOnly)
            {
                return WriteSearchRecipeIssueDraftSummary(
                    recipe,
                    selection.Queries,
                    scope,
                    total,
                    BuildSearchRecipeQueryFreshness(freshnessContext, freshnessObservations),
                    queryResults,
                    preflight,
                    options,
                    jsonOptions,
                    hasFailures);
            }

            var drafts = queryResults
                .Where(queryResult => queryResult.Count > 0)
                .Select(queryResult => ToSearchIssueDraft(recipe, queryResult, preflight, options))
                .ToList();
            var json = JsonSerializer.Serialize(
                new SearchIssueDraftExportJsonResult(
                    JsonOutputContract.ApiVersion,
                    ToSearchRecipeListItem(recipe, selection.Queries),
                    null,
                    "full",
                    scope,
                    selection.Queries.Count,
                    total,
                    BuildSearchRecipeQueryFreshness(freshnessContext, freshnessObservations),
                    drafts.Count,
                    new SuggestionIssueDraftPreflightSummaryJsonResult(
                        preflight.Checked,
                        preflight.Source,
                        preflight.OpenIssueCount,
                        options.DuplicateConfidence,
                        options.DuplicateThreshold),
                    drafts,
                    BuildSearchIssueDraftSelectionAccounting(recipe.Name, queryResults)),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchIssueDraftExportJsonResult);
            return CompleteSearchRecipeOutput(
                WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "issue-draft",
                    "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.",
                    jsonOptions,
                    options.InvocationContext.CommandName),
                hasFailures);
        });
    }

    private static int WriteSearchRecipeIssueDraftSummary(
        SearchAuditRecipe recipe,
        IReadOnlyList<SearchAuditRecipeQuery> selectedQueries,
        SearchRecipeScopeJsonResult scope,
        int resultCount,
        SearchRecipeQueryFreshnessJsonResult queryFreshness,
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        IssueDuplicatePreflight preflight,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool hasFailures)
    {
        const int evidencePathLimit = 5;
        var summaries = queryResults
            .Where(queryResult => queryResult.MinimumMatchedCount > 0)
            .Select(queryResult =>
            {
                var labels = queryResult.RecommendedLabels
                    .Concat(options.IssueLabels)
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Select(label => label.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var fileCount = queryResult.SummaryEvidencePathCount;
                var fileCountAuthoritative = queryResult.SummaryEvidencePathCountAuthoritative;
                var evidencePaths = queryResult.SummaryEvidencePaths
                    .Take(evidencePathLimit)
                    .ToList();
                var omittedEvidencePathCount = Math.Max(0, fileCount - evidencePaths.Count);
                return new SearchIssueDraftSummaryJsonResult(
                    $"{recipe.Name}/{queryResult.Name}",
                    queryResult.Name,
                    BuildSearchIssueDraftTitle(recipe, queryResult),
                    queryResult.Count,
                    fileCount,
                    fileCountAuthoritative,
                    fileCountAuthoritative ? null : fileCount,
                    queryResult.MinimumMatchedCount,
                    queryResult.MinimumOmittedResultCount,
                    queryResult.Truncated,
                    evidencePaths,
                    fileCount,
                    fileCountAuthoritative,
                    fileCountAuthoritative ? null : fileCount,
                    evidencePaths.Count,
                    omittedEvidencePathCount,
                    fileCountAuthoritative,
                    fileCountAuthoritative ? null : omittedEvidencePathCount,
                    omittedEvidencePathCount > 0 || !fileCountAuthoritative,
                    labels,
                    queryResult.Severity,
                    GetSearchRecipeConfidence(queryResult.MinimumMatchedCount),
                    queryResult.NextCursor,
                    BuildSearchRecipeReplayCommand(
                        recipe,
                        options,
                        queryResult.Name,
                        includeMaxJsonBytes: false));
            })
            .ToList();
        var duplicatePreflight = new SuggestionIssueDraftPreflightSummaryJsonResult(
            preflight.Checked,
            preflight.Source,
            preflight.OpenIssueCount,
            options.DuplicateConfidence,
            options.DuplicateThreshold);
        var recipeSummary = ToSearchRecipeCompactListItem(recipe, selectedQueries);
        var recoveryCommand = BuildSearchRecipeReplayCommand(
            recipe,
            options,
            summaryOnly: true,
            includeMaxJsonBytes: false,
            includeTotalLimit: false);
        var totalCount = summaries.Count;
        var totalCountAuthoritative = !hasFailures;
        string? envelopeJson = null;

        for (var returnedCount = totalCount; returnedCount >= 0; returnedCount--)
        {
            var omittedCount = totalCount - returnedCount;
            var payload = new SearchIssueDraftSummaryExportJsonResult(
                JsonOutputContract.ApiVersion,
                recipeSummary,
                "summary",
                scope,
                selectedQueries.Count,
                resultCount,
                queryFreshness,
                returnedCount,
                totalCount,
                totalCountAuthoritative,
                totalCountAuthoritative ? null : totalCount,
                returnedCount,
                omittedCount,
                omittedCount > 0,
                duplicatePreflight,
                summaries.Take(returnedCount).ToList(),
                recoveryCommand);
            var json = JsonSerializer.Serialize(
                payload,
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchIssueDraftSummaryExportJsonResult);
            json = AddActiveSqliteDiagnostics(json);
            envelopeJson = json;
            if (!options.MaxJsonBytes.HasValue
                || GetJsonDocumentByteCount(json) <= options.MaxJsonBytes.Value)
            {
                Console.WriteLine(json);
                return CompleteSearchRecipeOutput(CommandExitCodes.Success, hasFailures);
            }
        }

        return CompleteSearchRecipeOutput(
            WriteJsonObjectWithOptionalByteLimit(
                envelopeJson!,
                options,
                "issue-draft summary envelope",
                "Increase --max-json-bytes; the summary envelope cannot be reduced further.",
                jsonOptions,
                options.InvocationContext.CommandName),
            hasFailures);
    }

    private static int GetJsonDocumentByteCount(string json)
        => Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine);

    private static int RunSearchRecipeCount(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
    {
        if (!TryResolveSearchRecipeSelection(options, out var selection, out var selectionError))
        {
            WriteUsageError(
                selectionError!.Message,
                options,
                selectionError.Hint);
            return CommandExitCodes.UsageError;
        }

        var recipe = selection.Recipe;
        var scope = BuildSearchRecipeScope(recipe, options, selection.Queries);
        return WithDb(options, jsonOptions, reader =>
        {
            var freshnessContext = options.SummaryOnly
                ? BuildSearchRecipeFreshnessContext(reader, recipe, selection.Queries, options)
                : null;
            var queryCounts = CountSearchRecipeQueryResults(
                reader,
                selection.Queries,
                scope,
                options,
                userExact,
                freshnessContext,
                out var total,
                out var fileCount,
                out var freshnessObservations,
                out var hasFailures);

            if (options.Json)
            {
                if (options.SummaryOnly)
                {
                    var requiredFreshnessContext = freshnessContext!;
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
                            scope.Coverage!,
                            BuildSearchRecipeQueryFreshness(
                                requiredFreshnessContext,
                                freshnessObservations),
                            summaryQueries),
                        CliJsonSerializerContextFactory.Create(jsonOptions).SearchRecipeCountSummaryRunJsonResult);
                    return CompleteSearchRecipeOutput(
                        WriteJsonObjectWithOptionalByteLimit(
                            summaryJson,
                            options,
                            "recipe count summary",
                            "Use a larger --max-json-bytes value or narrow the recipe/query selection.",
                            jsonOptions),
                        hasFailures);
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
                    "Use `--summary-only` to omit recipe metadata from count output.",
                    jsonOptions);
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
            return WriteIssueDuplicatePreflightFailure(
                preflightResult,
                options,
                jsonOptions,
                options.InvocationContext.CommandName);
        var preflight = preflightResult.Preflight;

        return WithDb(options, jsonOptions, reader =>
        {
            var resultLimit = GetAdHocIssueDraftResultLimit(options);
            var sourceTotalCountAuthoritative = options.GuardFilters.Count == 0
                                                && !HasSearchOriginFilters(options);
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
            var outputSelection = ApplySearchOutputSelection(
                sourceRows,
                options,
                resultLimit,
                sourceTotalCountAuthoritative);
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
                rows.Select(row => row.Compact).ToList(),
                outputSelection.SourceTotal,
                outputSelection.SourceTotalAuthoritative,
                outputSelection.SourceTotalAuthoritative ? null : outputSelection.SourceTotal,
                outputSelection.SelectedTotal,
                outputSelection.Returned,
                outputSelection.SelectorOmittedCount,
                outputSelection.LimitOmittedCount,
                outputSelection.Selectors);
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
                    drafts,
                    BuildSearchIssueDraftSelectionAccounting(null, [queryResult])),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchIssueDraftExportJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "issue-draft",
                "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.",
                jsonOptions);
        });
    }

    private static List<SearchIssueDraftSelectionAccountingJsonResult>? BuildSearchIssueDraftSelectionAccounting(
        string? recipeName,
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults)
    {
        var accounting = queryResults
            .Where(query => query.Selectors.Count > 0)
            .Select(query => new SearchIssueDraftSelectionAccountingJsonResult(
                recipeName,
                recipeName is null ? null : query.Name,
                query.Query,
                query.SourceTotal,
                query.SourceTotalAuthoritative,
                query.SourceTotalLowerBound,
                query.SelectedTotal,
                query.Returned,
                query.SelectorOmittedCount,
                query.LimitOmittedCount,
                query.Selectors))
            .ToList();
        return accounting.Count == 0 ? null : accounting;
    }

    private static int GetAdHocIssueDraftResultLimit(QueryCommandOptions options)
        => options.TotalLimit.HasValue
            ? Math.Min(options.Limit, options.TotalLimit.Value)
            : options.Limit;

    private static void ApplySearchRecipeAuditClassifications(
        DbReader reader,
        SearchAuditRecipeQuery recipeQuery,
        IReadOnlyList<SearchAuditRecipeQuery> selectedQueries,
        List<SearchDisplayRow> rows)
    {
        var taskResultClassifier = recipeQuery.Classifiers
            .FirstOrDefault(classifier => string.Equals(classifier.Name, "task_result_intent", StringComparison.Ordinal));
        var jsonTrustBoundaryClassifier = recipeQuery.Classifiers
            .FirstOrDefault(classifier => string.Equals(classifier.Name, "json_trust_boundary", StringComparison.Ordinal));
        var parserGuardClassifier = recipeQuery.Classifiers
            .FirstOrDefault(classifier => string.Equals(classifier.Name, "parser_guard_evidence", StringComparison.Ordinal));
        if (taskResultClassifier == null
            && jsonTrustBoundaryClassifier == null
            && parserGuardClassifier == null)
            return;

        if (taskResultClassifier != null)
        {
            foreach (var row in rows)
                AddSearchRecipeAuditClassification(row, TryClassifyTaskResultIntent(taskResultClassifier, row));
        }

        var appliesJsonTrustBoundary = jsonTrustBoundaryClassifier != null
            && recipeQuery.JsonTrustDirection.HasValue;
        if (!appliesJsonTrustBoundary && parserGuardClassifier == null)
            return;

        var selectedJsonTrustQueries = appliesJsonTrustBoundary
            ? selectedQueries
                .Where(query => query.JsonTrustDirection.HasValue
                    && query.Classifiers.Any(classifier =>
                        string.Equals(classifier.Name, "json_trust_boundary", StringComparison.Ordinal)))
                .Select(query => query.Query)
                .Where(query => !string.IsNullOrWhiteSpace(query))
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];
        var jsonTrustLexicalContextCache = new JsonTrustLexicalContextCache();
        var parserGuardLexicalContextCache = new ParserGuardLexicalContextCache();
        foreach (var fileRows in rows.GroupBy(row => row.Result.Path, StringComparer.Ordinal))
        {
            var groupedRows = fileRows.ToList();
            var maximumJsonTrustRequiredLine = groupedRows
                .Select(row => appliesJsonTrustBoundary ? GetJsonTrustRequiredLine(row) : 0)
                .Where(line => line > 0 && line <= CSharpSemanticTokenClassifier.DefaultExcerptSourceLineLimit)
                .DefaultIfEmpty()
                .Max();
            if (maximumJsonTrustRequiredLine > 0)
            {
                _ = GetJsonTrustLexicalContext(
                    reader,
                    groupedRows[0],
                    maximumJsonTrustRequiredLine,
                    jsonTrustLexicalContextCache);
            }
            var maximumParserGuardRequiredLine = groupedRows
                .Select(row => parserGuardClassifier != null ? GetParserGuardRequiredLine(row) : 0)
                .Where(line => line > 0 && line <= CSharpSemanticTokenClassifier.DefaultExcerptSourceLineLimit)
                .DefaultIfEmpty()
                .Max();
            if (maximumParserGuardRequiredLine > 0)
            {
                _ = GetParserGuardLexicalContext(
                    reader,
                    groupedRows[0],
                    maximumParserGuardRequiredLine,
                    parserGuardLexicalContextCache);
            }
            foreach (var row in groupedRows)
            {
                if (parserGuardClassifier != null)
                {
                    AddSearchRecipeAuditClassification(
                        row,
                        ClassifyParserGuardEvidence(
                            parserGuardClassifier,
                            recipeQuery.Query,
                            row,
                            reader,
                            parserGuardLexicalContextCache));
                }
                if (appliesJsonTrustBoundary)
                {
                    AddSearchRecipeAuditClassification(
                        row,
                        ClassifyJsonTrustBoundary(
                            jsonTrustBoundaryClassifier!,
                            recipeQuery.JsonTrustDirection!.Value,
                            row,
                            reader,
                            jsonTrustLexicalContextCache,
                            selectedJsonTrustQueries));
                }
            }
        }
    }

    private static int GetJsonTrustRequiredLine(SearchDisplayRow row)
        => row.Compact.MatchLines
            .Where(line => line > 0)
            .DefaultIfEmpty(row.Compact.FocusLine.GetValueOrDefault(row.Result.StartLine))
            .Max();

    private static void AddSearchRecipeAuditClassification(
        SearchDisplayRow row,
        SearchAuditClassificationJsonResult? classification)
    {
        if (classification == null)
            return;

        row.Compact.AuditClassifications ??= [];
        row.Compact.AuditClassifications.Add(classification);
    }

    private static SearchAuditClassificationJsonResult ClassifyJsonTrustBoundary(
        SearchRecipeClassifierJsonResult classifier,
        SearchRecipeJsonTrustDirection expectedDirection,
        SearchDisplayRow row,
        DbReader reader,
        JsonTrustLexicalContextCache lexicalContextCache,
        IReadOnlyList<string> selectedJsonTrustQueries)
    {
        var matchLines = row.Compact.MatchLines
            .Where(line => line > 0)
            .Distinct()
            .OrderBy(line => line)
            .ToList();
        if (matchLines.Count == 0)
            matchLines.Add(row.Compact.FocusLine.GetValueOrDefault(row.Result.StartLine));

        var matchSites = row.Compact.MatchFacets
            .Where(facet => facet.Line > 0
                && facet.Column > 0
                && matchLines.Contains(facet.Line))
            .Select(facet => new JsonTrustMatchSite(facet.Line, facet.Column, facet.Length))
            .Distinct()
            .OrderBy(site => site.Line)
            .ThenBy(site => site.Column)
            .ToList();
        foreach (var line in matchLines.Where(line => matchSites.All(site => site.Line != line)))
            matchSites.Add(new JsonTrustMatchSite(line, null, null));
        matchSites = matchSites
            .OrderBy(site => site.Line)
            .ThenBy(site => site.Column)
            .ToList();

        var lexicalContext = GetJsonTrustLexicalContext(
            reader,
            row,
            matchSites.Max(site => site.Line),
            lexicalContextCache);
        if (lexicalContext != null)
        {
            matchSites = matchSites
                .Where(site => !IsJsonTrustDeclarationFacetBeforeLaterMatch(site, matchSites, lexicalContext))
                .ToList();
        }
        var consumedAnnotationLines = new HashSet<int>();
        var matchEvidence = new List<JsonTrustBoundaryEvidence>(matchSites.Count);
        foreach (var site in matchSites)
        {
            var candidate = GetJsonTrustBoundaryEvidence(
                expectedDirection,
                site.Line,
                site.Column,
                lexicalContext,
                selectedJsonTrustQueries);
            if (candidate.AnnotationLine is { } annotationLine
                && !consumedAnnotationLines.Add(annotationLine))
            {
                candidate = new JsonTrustBoundaryEvidence(
                    "unknown",
                    expectedDirection == SearchRecipeJsonTrustDirection.Read ? "read" : "write",
                    "unknown",
                    "review_required",
                    "annotation_not_bound_to_operation",
                    "not_adjacent",
                    annotationLine);
            }
            matchEvidence.Add(candidate);
        }
        var evidence = matchEvidence[0];
        var mixedBoundaries = matchEvidence
            .Skip(1)
            .Any(candidate => !HasEquivalentJsonTrustBoundary(evidence, candidate));
        var matchCategories = matchEvidence
            .Select(candidate => candidate.AnnotationStatus == "valid"
                ? ClassifyValidJsonTrustBoundary(candidate)
                : "ambiguous_trust")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToList();
        if (mixedBoundaries)
        {
            evidence = new JsonTrustBoundaryEvidence(
                "unknown",
                expectedDirection == SearchRecipeJsonTrustDirection.Read ? "read" : "write",
                "unknown",
                "review_required",
                "multiple_trust_boundaries",
                "mixed_boundaries",
                null);
        }

        var categoryName = mixedBoundaries
            ? "ambiguous_trust"
            : matchCategories[0];
        var categoryMetadata = classifier.Categories
            .First(category => string.Equals(category.Name, categoryName, StringComparison.Ordinal));
        var details = new List<string>
        {
            $"origin:{evidence.Origin}",
            $"direction:{evidence.Direction}",
            $"sensitivity:{evidence.Sensitivity}",
            $"trust:{evidence.Trust}",
            $"rationale:{evidence.Rationale}",
            $"annotation_status:{evidence.AnnotationStatus}",
        };
        if (evidence.AnnotationLine.HasValue)
            details.Add($"annotation_line:{evidence.AnnotationLine.Value.ToString(CultureInfo.InvariantCulture)}");
        if (mixedBoundaries)
        {
            details.Add($"match_line_count:{matchLines.Count.ToString(CultureInfo.InvariantCulture)}");
            if (matchSites.Count != matchLines.Count)
                details.Add($"match_site_count:{matchSites.Count.ToString(CultureInfo.InvariantCulture)}");
            details.Add($"boundary_categories:{string.Join(',', matchCategories)}");
        }
        return new SearchAuditClassificationJsonResult(
            classifier.Name,
            categoryMetadata.Name,
            categoryMetadata.Description,
            categoryMetadata.ReviewGuidance,
            details);
    }

    private static List<SearchDisplayRow> ApplySearchRecipeSemanticFilter(
        DbReader reader,
        QueryCommandOptions options,
        SearchAuditRecipeQuery recipeQuery,
        List<SearchDisplayRow> rows)
    {
        if (recipeQuery.SemanticFilter == SearchRecipeSemanticFilter.None)
            return rows;

        var classifierName = recipeQuery.SemanticFilter switch
        {
            SearchRecipeSemanticFilter.RegexStaticMember => "regex_operation_semantics",
            SearchRecipeSemanticFilter.ShellExecuteAssignment => "shell_execute_polarity",
            _ => null,
        };
        var classifier = classifierName == null
            ? null
            : recipeQuery.Classifiers.FirstOrDefault(candidate => string.Equals(candidate.Name, classifierName, StringComparison.Ordinal));
        if (classifier == null)
            return rows;

        var retained = new List<SearchDisplayRow>(rows.Count);
        var regexBindingPaths = recipeQuery.SemanticFilter == SearchRecipeSemanticFilter.RegexStaticMember
            ? BuildRegexBareReceiverContexts(reader, options, rows)
            : new Dictionary<string, RegexBareReceiverContext>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var evidence = recipeQuery.SemanticFilter switch
            {
                SearchRecipeSemanticFilter.RegexStaticMember => GetRegexOperationEvidence(
                    reader,
                    row,
                    regexBindingPaths),
                SearchRecipeSemanticFilter.ShellExecuteAssignment => GetShellExecuteAssignmentEvidence(row),
                _ => null,
            };
            if (evidence?.Suppress == true)
                continue;

            if (evidence != null)
            {
                var classification = BuildSearchRecipeSemanticClassification(classifier, evidence, row);
                if (classification != null)
                {
                    row.Compact.AuditClassifications ??= [];
                    row.Compact.AuditClassifications.Add(classification);
                }
            }

            retained.Add(row);
        }

        return retained;
    }

    private static SearchAuditClassificationJsonResult? BuildSearchRecipeSemanticClassification(
        SearchRecipeClassifierJsonResult classifier,
        SearchRecipeSemanticEvidence evidence,
        SearchDisplayRow row)
    {
        var categoryMetadata = classifier.Categories
            .FirstOrDefault(category => string.Equals(category.Name, evidence.Category, StringComparison.Ordinal));
        if (categoryMetadata == null)
            return null;

        var details = new List<string>
        {
            $"reason:{evidence.Reason}",
        };
        if (!string.IsNullOrWhiteSpace(evidence.Operation))
            details.Add($"operation:{evidence.Operation}");
        if (!string.IsNullOrWhiteSpace(evidence.Value))
            details.Add($"value:{evidence.Value}");
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

    private static bool IsJsonTrustDeclarationFacetBeforeLaterMatch(
        JsonTrustMatchSite site,
        IReadOnlyList<JsonTrustMatchSite> matchSites,
        JsonTrustLexicalContext lexicalContext)
    {
        var laterMatch = matchSites
            .Where(candidate => candidate.Column.HasValue
                && (candidate.Line > site.Line
                    || candidate.Line == site.Line && candidate.Column > site.Column))
            .OrderBy(candidate => candidate.Line)
            .ThenBy(candidate => candidate.Column)
            .FirstOrDefault();
        if (!site.Column.HasValue
            || !site.Length.HasValue
            || site.Line <= 0
            || site.Line > lexicalContext.MaskedLines.Length
            || laterMatch == default
            || laterMatch.Line > lexicalContext.MaskedLines.Length)
        {
            return false;
        }

        var declarationPrefix = new StringBuilder();
        for (var lineNumber = site.Line; lineNumber <= laterMatch.Line; lineNumber++)
        {
            var sourceLine = GetJsonTrustCodeBeforeLineComment(lexicalContext.MaskedLines[lineNumber - 1]);
            var prefixLength = lineNumber == laterMatch.Line
                ? Math.Clamp(laterMatch.Column!.Value - 1, 0, sourceLine.Length)
                : sourceLine.Length;
            declarationPrefix.Append(sourceLine.AsSpan(0, prefixLength)).Append(' ');
        }

        var line = declarationPrefix.ToString();
        var index = Math.Clamp(site.Column.Value - 1 + site.Length.Value, 0, line.Length);
        SkipJsonTrustDeclarationTypeSuffix(
            line,
            ref index,
            GetJsonTrustUnclosedGenericDepthBeforeSite(lexicalContext, site));

        if (index >= line.Length || !(char.IsLetter(line[index]) || line[index] is '_' or '@'))
            return false;

        index++;
        while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '_'))
            index++;
        SkipJsonTrustWhitespace(line, ref index);
        if (line.AsSpan(index).IndexOfAny(';', '{', '}') >= 0)
            return false;

        return index >= line.Length
            || line[index] is '=' or ';' or ',' or ')'
            || IsJsonTrustMethodDeclarationSuffix(line, index);
    }

    private static void SkipJsonTrustDeclarationTypeSuffix(
        string line,
        ref int index,
        int enclosingGenericDepth)
    {
        while (enclosingGenericDepth > 0 && index < line.Length)
        {
            switch (line[index])
            {
                case '<':
                    enclosingGenericDepth++;
                    break;
                case '>':
                    enclosingGenericDepth--;
                    break;
                case ';' or '=' or '{' or '}':
                    return;
            }

            index++;
        }

        while (true)
        {
            SkipJsonTrustWhitespace(line, ref index);
            if (index < line.Length && line[index] == '?')
            {
                index++;
                continue;
            }
            if (index + 1 < line.Length && line[index] == '[' && line[index + 1] == ']')
            {
                index += 2;
                continue;
            }
            return;
        }
    }

    private static int GetJsonTrustUnclosedGenericDepthBeforeSite(
        JsonTrustLexicalContext lexicalContext,
        JsonTrustMatchSite site)
    {
        var closingDepth = 0;
        var unclosedDepth = 0;
        for (var lineNumber = site.Line; lineNumber >= 1; lineNumber--)
        {
            var line = GetJsonTrustCodeBeforeLineComment(lexicalContext.MaskedLines[lineNumber - 1]);
            var startIndex = lineNumber == site.Line
                ? Math.Clamp(site.Column.GetValueOrDefault(1) - 2, -1, line.Length - 1)
                : line.Length - 1;
            for (var index = startIndex; index >= 0; index--)
            {
                switch (line[index])
                {
                    case '>':
                        closingDepth++;
                        break;
                    case '<' when closingDepth > 0:
                        closingDepth--;
                        break;
                    case '<':
                        unclosedDepth++;
                        break;
                    case ';' or '=' or '{' or '}':
                        return unclosedDepth;
                }
            }
        }

        return unclosedDepth;
    }

    private static bool IsJsonTrustMethodDeclarationSuffix(string line, int index)
    {
        if (index >= line.Length || line[index] == '(')
            return index < line.Length;
        if (line[index] != '<')
            return false;

        var depth = 0;
        for (; index < line.Length; index++)
        {
            if (line[index] == '<')
            {
                depth++;
                continue;
            }
            if (line[index] != '>')
                continue;

            depth--;
            if (depth != 0)
                continue;

            index++;
            SkipJsonTrustWhitespace(line, ref index);
            return index < line.Length && line[index] == '(';
        }

        return false;
    }

    private static void SkipJsonTrustWhitespace(string line, ref int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
    }

    private static string ClassifyValidJsonTrustBoundary(JsonTrustBoundaryEvidence evidence)
    {
        if (evidence.Trust == "review_required")
            return "ambiguous_trust";

        var externalOrigin = evidence.Origin is "public_api" or "network" or "file" or "external";
        if (evidence.Direction == "write" && externalOrigin)
            return "external_or_public_writer";
        if (evidence.Direction == "read" && externalOrigin && evidence.Trust == "untrusted")
            return "untrusted_parser";
        if (evidence.Direction == "write"
            && evidence.Origin == "private_local"
            && evidence.Trust == "controlled"
            && evidence.Sensitivity is "diagnostic" or "confidential")
        {
            return "controlled_private_writer";
        }

        return "ambiguous_trust";
    }

    private static bool HasEquivalentJsonTrustBoundary(
        JsonTrustBoundaryEvidence left,
        JsonTrustBoundaryEvidence right)
        => string.Equals(left.Origin, right.Origin, StringComparison.Ordinal)
            && string.Equals(left.Direction, right.Direction, StringComparison.Ordinal)
            && string.Equals(left.Sensitivity, right.Sensitivity, StringComparison.Ordinal)
            && string.Equals(left.Trust, right.Trust, StringComparison.Ordinal)
            && string.Equals(left.Rationale, right.Rationale, StringComparison.Ordinal)
            && string.Equals(left.AnnotationStatus, right.AnnotationStatus, StringComparison.Ordinal);

    private static JsonTrustBoundaryEvidence GetJsonTrustBoundaryEvidence(
        SearchRecipeJsonTrustDirection expectedDirection,
        int focusLine,
        int? focusColumn,
        JsonTrustLexicalContext? lexicalContext,
        IReadOnlyList<string> selectedJsonTrustQueries)
    {
        const string marker = "// cdidx-audit: json-trust ";
        var expectedDirectionText = expectedDirection == SearchRecipeJsonTrustDirection.Read ? "read" : "write";
        var nearestLine = -1;
        if (lexicalContext != null && focusLine > 0 && focusLine <= lexicalContext.SourceLines.Length)
        {
            var annotationIndex = Array.BinarySearch(lexicalContext.AnnotationLines, focusLine);
            if (annotationIndex < 0)
                annotationIndex = ~annotationIndex - 1;
            if (annotationIndex >= 0)
                nearestLine = lexicalContext.AnnotationLines[annotationIndex];
        }

        if (nearestLine < 0)
        {
            return new JsonTrustBoundaryEvidence(
                "unknown",
                expectedDirectionText,
                "unknown",
                "review_required",
                "missing_explicit_trust_annotation",
                "missing",
                null);
        }

        if (!lexicalContext!.MaskedLines[nearestLine - 1].TrimStart().StartsWith(marker, StringComparison.Ordinal))
        {
            return new JsonTrustBoundaryEvidence(
                "unknown",
                expectedDirectionText,
                "unknown",
                "review_required",
                "invalid_explicit_trust_annotation",
                "invalid",
                nearestLine);
        }

        if (lexicalContext.ConditionalCompilationLines[nearestLine - 1])
        {
            return new JsonTrustBoundaryEvidence(
                "unknown",
                expectedDirectionText,
                "unknown",
                "review_required",
                "conditional_compilation_annotation",
                "not_adjacent",
                nearestLine);
        }

        if (HasInterveningJsonTrustExecutableLine(
            lexicalContext,
            nearestLine,
            focusLine,
            focusColumn,
            selectedJsonTrustQueries))
        {
            return new JsonTrustBoundaryEvidence(
                "unknown",
                expectedDirectionText,
                "unknown",
                "review_required",
                "annotation_not_bound_to_operation",
                "not_adjacent",
                nearestLine);
        }

        var annotation = lexicalContext.SourceLines[nearestLine - 1].TrimStart();
        var payload = annotation[marker.Length..].Trim();
        if (!TryParseJsonTrustBoundaryAnnotation(payload, out var parsed))
        {
            return new JsonTrustBoundaryEvidence(
                "unknown",
                expectedDirectionText,
                "unknown",
                "review_required",
                "invalid_explicit_trust_annotation",
                "invalid",
                nearestLine);
        }

        if (!string.Equals(parsed.Direction, expectedDirectionText, StringComparison.Ordinal))
            return parsed with { AnnotationStatus = "direction_mismatch", AnnotationLine = nearestLine };

        return parsed with { AnnotationStatus = "valid", AnnotationLine = nearestLine };
    }

    private static bool HasInterveningJsonTrustExecutableLine(
        JsonTrustLexicalContext lexicalContext,
        int annotationLine,
        int operationLine,
        int? operationColumn,
        IReadOnlyList<string> selectedJsonTrustQueries)
    {
        var statementPrefix = new StringBuilder();
        for (var line = annotationLine + 1; line < operationLine; line++)
        {
            var maskedLine = GetJsonTrustCodeBeforeLineComment(lexicalContext.MaskedLines[line - 1]);
            if (string.IsNullOrWhiteSpace(maskedLine))
                continue;
            if (maskedLine.AsSpan().TrimStart().StartsWith("#", StringComparison.Ordinal))
                return true;

            statementPrefix.Append(maskedLine).Append(' ');
            if (HasPriorSelectedJsonTrustMatchOnLine(
                    lexicalContext,
                    line,
                    maskedLine.Length + 1,
                    selectedJsonTrustQueries))
            {
                return true;
            }
        }

        if (operationColumn.HasValue
            && operationLine > 0
            && operationLine <= lexicalContext.MaskedLines.Length)
        {
            var operationText = lexicalContext.MaskedLines[operationLine - 1];
            var prefixLength = Math.Clamp(operationColumn.Value - 1, 0, operationText.Length);
            statementPrefix.Append(operationText.AsSpan(0, prefixLength));
            if (HasPriorSelectedJsonTrustMatchOnLine(
                    lexicalContext,
                    operationLine,
                    operationColumn.Value,
                    selectedJsonTrustQueries))
                return true;
        }

        return HasPriorJsonTrustOperationOnLine(statementPrefix.ToString().AsSpan());
    }

    private static string GetJsonTrustCodeBeforeLineComment(string maskedLine)
    {
        var commentIndex = maskedLine.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? maskedLine[..commentIndex] : maskedLine;
    }

    private static bool HasPriorSelectedJsonTrustMatchOnLine(
        JsonTrustLexicalContext lexicalContext,
        int operationLine,
        int operationColumn,
        IReadOnlyList<string> selectedJsonTrustQueries)
    {
        if (operationLine <= 0
            || operationLine > lexicalContext.MaskedLines.Length
            || selectedJsonTrustQueries.Count == 0)
        {
            return false;
        }

        var line = lexicalContext.MaskedLines[operationLine - 1];
        var prefixLength = Math.Clamp(operationColumn - 1, 0, line.Length);
        var normalizedLine = CSharpVerbatimNameNormalizer.Normalize(line, out var rawIndexMap);
        var normalizedPrefixLength = Array.BinarySearch(rawIndexMap, prefixLength);
        if (normalizedPrefixLength < 0)
            normalizedPrefixLength = ~normalizedPrefixLength;
        var currentSite = new JsonTrustMatchSite(operationLine, operationColumn, null);
        foreach (var query in selectedJsonTrustQueries)
        {
            var normalizedQuery = CSharpVerbatimNameNormalizer.Normalize(query);
            var searchStart = 0;
            while (searchStart < normalizedPrefixLength)
            {
                var occurrence = normalizedLine.IndexOf(normalizedQuery, searchStart, StringComparison.Ordinal);
                if (occurrence < 0 || occurrence >= normalizedPrefixLength)
                    break;

                var normalizedEnd = occurrence + normalizedQuery.Length;
                if (normalizedEnd > normalizedPrefixLength)
                    return true;
                var rawStart = rawIndexMap[occurrence];
                var rawEnd = rawIndexMap[normalizedEnd - 1] + 1;
                var priorSite = new JsonTrustMatchSite(operationLine, rawStart + 1, rawEnd - rawStart);
                if (!IsJsonTrustDeclarationFacetBeforeLaterMatch(
                        priorSite,
                        [priorSite, currentSite],
                        lexicalContext))
                {
                    return true;
                }

                searchStart = occurrence + Math.Max(1, normalizedQuery.Length);
            }
        }

        return false;
    }

    private static bool HasPriorJsonTrustOperationOnLine(ReadOnlySpan<char> prefix)
    {
        var tokens = TokenizeJsonTrustCSharpPrefix(prefix);
        if (tokens.Count == 0)
            return false;

        var expressionStart = 0;
        if (TryGetJsonTrustExpressionBodiedMethodStart(tokens, out var expressionBodyStart))
            expressionStart = expressionBodyStart;

        var assignmentIndex = -1;
        for (var index = tokens.Count - 1; index >= expressionStart; index--)
        {
            if (tokens[index] == "=")
            {
                assignmentIndex = index;
                break;
            }
        }

        if (assignmentIndex >= 0
            && HasEvaluatedJsonTrustAssignmentTarget(tokens, expressionStart, assignmentIndex))
        {
            return true;
        }

        if (assignmentIndex >= 0)
            expressionStart = assignmentIndex + 1;
        if (HasEvaluatedJsonTrustInvocationReceiver(tokens, expressionStart))
            return true;

        for (var index = expressionStart; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token == "("
                && TrySkipJsonTrustCast(tokens, expressionStart, index, out var castCloseIndex))
            {
                index = castCloseIndex;
                continue;
            }

            if (token == "<"
                && TrySkipJsonTrustGenericArgumentList(tokens, index, out var genericCloseIndex))
            {
                index = genericCloseIndex;
                continue;
            }

            if (token == ")" && IsJsonTrustCastClosingParenthesis(tokens, expressionStart, index))
                continue;

            if (token is ";" or "," or "{" or "}" or ")" or "]" or "=>"
                or "==" or "!=" or "<=" or ">=" or "++" or "--"
                or "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^"
                or "&&" or "||" or "??" or "?" or "<" or ">" or "<<" or ">>"
                or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^="
                or "is" or "as" or "and" or "or")
            {
                return true;
            }

            if (IsJsonTrustNumericToken(token))
                return true;
        }

        return false;
    }

    private static bool TrySkipJsonTrustCast(
        IReadOnlyList<string> tokens,
        int expressionStart,
        int openIndex,
        out int closeIndex)
    {
        closeIndex = -1;
        if (openIndex < expressionStart
            || openIndex >= tokens.Count
            || tokens[openIndex] != "(")
        {
            return false;
        }

        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
        {
            if (tokens[index] == "(")
            {
                depth++;
                continue;
            }
            if (tokens[index] != ")")
                continue;

            depth--;
            if (depth != 0)
                continue;

            if (!IsJsonTrustCastClosingParenthesis(tokens, expressionStart, index))
                return false;

            closeIndex = index;
            return true;
        }

        return false;
    }

    private static bool HasEvaluatedJsonTrustInvocationReceiver(
        IReadOnlyList<string> tokens,
        int expressionStart)
    {
        var closingDepth = 0;
        for (var openIndex = tokens.Count - 1; openIndex >= expressionStart; openIndex--)
        {
            if (tokens[openIndex] == ")")
            {
                closingDepth++;
                continue;
            }
            if (tokens[openIndex] != "(")
                continue;
            if (closingDepth > 0)
            {
                closingDepth--;
                continue;
            }

            var methodNameIndex = openIndex - 1;
            if (methodNameIndex >= expressionStart && tokens[methodNameIndex] == ">")
            {
                var genericDepth = 0;
                for (; methodNameIndex >= expressionStart; methodNameIndex--)
                {
                    if (tokens[methodNameIndex] == ">")
                    {
                        genericDepth++;
                        continue;
                    }
                    if (tokens[methodNameIndex] != "<")
                        continue;

                    genericDepth--;
                    if (genericDepth == 0)
                    {
                        methodNameIndex--;
                        break;
                    }
                }
            }

            if (methodNameIndex < expressionStart + 2
                || !IsJsonTrustIdentifierToken(tokens[methodNameIndex])
                || tokens[methodNameIndex - 1] is not ("." or "?."))
            {
                continue;
            }

            // A direct call on a simple local/type receiver (source.Build(...)) does not
            // evaluate a property before its first argument. An additional member hop
            // (source.Factory.Build(...)) can execute a getter and therefore consumes the
            // trust annotation before the JSON operation.
            var receiverMemberIndex = methodNameIndex - 2;
            if (receiverMemberIndex >= expressionStart + 2
                && IsJsonTrustIdentifierToken(tokens[receiverMemberIndex])
                && tokens[receiverMemberIndex - 1] is "." or "?.")
            {
                return true;
            }

            if (receiverMemberIndex < expressionStart
                || !IsJsonTrustIdentifierToken(tokens[receiverMemberIndex]))
            {
                return true;
            }

            var receiver = tokens[receiverMemberIndex];
            if (!IsJsonTrustReceiverDeclaredBeforeExpression(tokens, expressionStart, receiver))
                return true;
        }

        return false;
    }

    private static bool IsJsonTrustReceiverDeclaredBeforeExpression(
        IReadOnlyList<string> tokens,
        int expressionStart,
        string receiver)
    {
        for (var index = 1; index < expressionStart; index++)
        {
            if (!string.Equals(tokens[index], receiver, StringComparison.Ordinal))
                continue;
            if (index + 1 >= expressionStart
                || tokens[index + 1] is not ("," or ")" or "="))
            {
                continue;
            }

            var precedingToken = tokens[index - 1];
            if (IsJsonTrustIdentifierToken(precedingToken)
                || precedingToken is ">" or "]" or "?" or "*")
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetJsonTrustExpressionBodiedMethodStart(
        IReadOnlyList<string> tokens,
        out int expressionStart)
    {
        expressionStart = -1;
        var arrowIndex = -1;
        for (var index = tokens.Count - 1; index >= 0; index--)
        {
            if (tokens[index] == "=>")
            {
                arrowIndex = index;
                break;
            }
        }
        if (arrowIndex < 2 || tokens[arrowIndex - 1] != ")")
            return false;

        var depth = 0;
        var openIndex = -1;
        for (var index = arrowIndex - 1; index >= 0; index--)
        {
            if (tokens[index] == ")")
            {
                depth++;
                continue;
            }
            if (tokens[index] != "(")
                continue;

            depth--;
            if (depth == 0)
            {
                openIndex = index;
                break;
            }
        }

        if (openIndex < 2 || !HasJsonTrustMethodNameBeforeParenthesis(tokens, openIndex))
            return false;

        expressionStart = arrowIndex + 1;
        return true;
    }

    private static bool IsJsonTrustIdentifierToken(string token)
        => token.Length > 0 && (char.IsLetter(token[0]) || token[0] is '_' or '@');

    private static bool HasJsonTrustMethodNameBeforeParenthesis(
        IReadOnlyList<string> tokens,
        int openIndex)
    {
        var nameIndex = openIndex - 1;
        if (IsJsonTrustIdentifierToken(tokens[nameIndex]))
            return true;
        if (tokens[nameIndex] != ">")
            return false;

        var depth = 0;
        for (var index = nameIndex; index >= 0; index--)
        {
            if (tokens[index] == ">")
            {
                depth++;
                continue;
            }
            if (tokens[index] != "<")
                continue;

            depth--;
            if (depth == 0)
                return index > 0 && IsJsonTrustIdentifierToken(tokens[index - 1]);
        }

        return false;
    }

    private static bool IsJsonTrustCastClosingParenthesis(
        IReadOnlyList<string> tokens,
        int expressionStart,
        int closeIndex)
    {
        var depth = 0;
        var openIndex = -1;
        for (var index = closeIndex; index >= expressionStart; index--)
        {
            if (tokens[index] == ")")
            {
                depth++;
                continue;
            }
            if (tokens[index] != "(")
                continue;

            depth--;
            if (depth == 0)
            {
                openIndex = index;
                break;
            }
        }
        if (openIndex < 0 || openIndex + 1 >= closeIndex)
            return false;

        if (openIndex > expressionStart
            && tokens[openIndex - 1] is not ("return" or "throw" or "await" or "(" or "," or ":"
                or "=" or "=>" or "!" or "~" or "+" or "-"))
        {
            return false;
        }

        var hasTypeIdentifier = false;
        var nestedParentheses = 0;
        for (var index = openIndex + 1; index < closeIndex; index++)
        {
            var token = tokens[index];
            if (token == "(")
            {
                nestedParentheses++;
                continue;
            }
            if (token == ")" && nestedParentheses > 0)
            {
                nestedParentheses--;
                continue;
            }
            if (token.Length > 0 && (char.IsLetter(token[0]) || token[0] is '_' or '@'))
            {
                hasTypeIdentifier = true;
                continue;
            }
            if (token is "." or "::" or "?" or "[" or "]" or "<" or ">" or "," or "*")
                continue;

            return false;
        }

        return hasTypeIdentifier && nestedParentheses == 0;
    }

    private static bool TrySkipJsonTrustGenericArgumentList(
        IReadOnlyList<string> tokens,
        int openIndex,
        out int closeIndex)
    {
        closeIndex = -1;
        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token == "<")
            {
                depth++;
                continue;
            }
            if (token != ">")
                continue;

            depth--;
            if (depth != 0)
                continue;

            if (index + 1 < tokens.Count
                && tokens[index + 1] is "(" or "." or "?." or "::")
            {
                closeIndex = index;
                return true;
            }
            return false;
        }

        return false;
    }

    private static bool HasEvaluatedJsonTrustAssignmentTarget(
        IReadOnlyList<string> tokens,
        int expressionStart,
        int assignmentIndex)
    {
        if (IsJsonTrustDeclarationAssignmentTarget(tokens, assignmentIndex))
            return false;

        var genericDepth = 0;
        for (var index = expressionStart; index < assignmentIndex; index++)
        {
            var token = tokens[index];
            if (token == "<")
            {
                genericDepth++;
                continue;
            }
            if (token == ">" && genericDepth > 0)
            {
                genericDepth--;
                continue;
            }
            if (token == "," && genericDepth > 0)
                continue;
            if (token == "]" && index > 0 && tokens[index - 1] == "[")
                continue;
            if (token == "]")
                return true;

            if (token is "." or "?.")
                return true;
            if (token is "::" or "[")
                continue;

            if (token is ";" or "," or "{" or "}" or ")" or "=>" or "?" or ":"
                or "==" or "!=" or "<=" or ">=" or "++" or "--"
                or "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^"
                or "&&" or "||" or "??" or "<<" or ">>"
                or "is" or "as" or "and" or "or")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsJsonTrustDeclarationAssignmentTarget(
        IReadOnlyList<string> tokens,
        int assignmentIndex)
    {
        if (assignmentIndex < 2 || !IsJsonTrustIdentifierToken(tokens[assignmentIndex - 1]))
            return false;

        var precedingToken = tokens[assignmentIndex - 2];
        return IsJsonTrustIdentifierToken(precedingToken)
            || precedingToken is ">" or "]" or "?" or "*" or ")";
    }

    private static List<string> TokenizeJsonTrustCSharpPrefix(ReadOnlySpan<char> prefix)
    {
        var tokens = new List<string>();
        for (var index = 0; index < prefix.Length;)
        {
            if (char.IsWhiteSpace(prefix[index]))
            {
                index++;
                continue;
            }

            if (char.IsLetter(prefix[index]) || prefix[index] is '_' or '@')
            {
                var start = index++;
                while (index < prefix.Length &&
                    (char.IsLetterOrDigit(prefix[index]) || prefix[index] == '_'))
                {
                    index++;
                }
                tokens.Add(prefix[start..index].ToString().TrimStart('@'));
                continue;
            }

            if (char.IsDigit(prefix[index]))
            {
                var start = index++;
                while (index < prefix.Length &&
                    (char.IsLetterOrDigit(prefix[index]) || prefix[index] is '_' or '.'))
                {
                    index++;
                }
                tokens.Add(prefix[start..index].ToString());
                continue;
            }

            if (index + 1 < prefix.Length)
            {
                var pair = prefix.Slice(index, 2);
                if (pair is "=>" or "::" or "?." or "??" or "==" or "!="
                    or "<=" or ">=" or "++" or "--" or "&&" or "||"
                    or "+=" or "-=" or "*=" or "/="
                    or "%=" or "&=" or "|=" or "^=")
                {
                    tokens.Add(pair.ToString());
                    index += 2;
                    continue;
                }
            }

            tokens.Add(prefix[index].ToString());
            index++;
        }

        return tokens;
    }

    private static bool IsJsonTrustNumericToken(string token)
        => token.Length > 0 && char.IsDigit(token[0]);

    private static JsonTrustLexicalContext? GetJsonTrustLexicalContext(
        DbReader reader,
        SearchDisplayRow row,
        int requiredLine,
        JsonTrustLexicalContextCache cache)
    {
        if (!string.Equals(row.Result.Lang, "csharp", StringComparison.OrdinalIgnoreCase))
            return null;

        if (requiredLine <= 0 || requiredLine > CSharpSemanticTokenClassifier.DefaultExcerptSourceLineLimit)
            return null;

        if (string.Equals(cache.Path, row.Result.Path, StringComparison.Ordinal))
        {
            if (cache.LoadedThroughLine >= requiredLine)
                return cache.Context;
            if (cache.SourceLimitReached)
                return null;
        }

        var indexedLines = reader.GetIndexedSourceLinesForSemanticTokens(
            row.Result.Path,
            requiredLine,
            CSharpSemanticTokenClassifier.DefaultExcerptSourceCharacterLimit);
        JsonTrustLexicalContext? context = null;
        if (indexedLines.Count > 0)
        {
            var sourceLines = indexedLines.Select(line => line ?? string.Empty).ToArray();
            var maskedLines = StructuralLineMasker.MaskLines("csharp", sourceLines);
            var annotationLines = sourceLines
                .Select((line, index) => (Line: line, Number: index + 1))
                .Where(candidate => candidate.Line.TrimStart().StartsWith(
                    "// cdidx-audit: json-trust ",
                    StringComparison.Ordinal))
                .Select(candidate => candidate.Number)
                .ToArray();
            var conditionalCompilationLines = GetJsonTrustConditionalCompilationLines(maskedLines);
            context = new JsonTrustLexicalContext(
                sourceLines,
                maskedLines,
                annotationLines,
                conditionalCompilationLines);
        }

        // Retain only one bounded source prefix so count-mode memory does not grow with file count.
        cache.Path = row.Result.Path;
        cache.LoadedThroughLine = indexedLines.Count;
        cache.SourceLimitReached = indexedLines.Count < requiredLine;
        cache.Context = context;
        return context;
    }

    private static bool[] GetJsonTrustConditionalCompilationLines(IReadOnlyList<string> maskedLines)
    {
        var conditionalLines = new bool[maskedLines.Count];
        var depth = 0;
        for (var index = 0; index < maskedLines.Count; index++)
        {
            var trimmed = maskedLines[index].AsSpan().TrimStart();
            if (IsJsonTrustConditionalCompilationDirective(trimmed, "if", allowExpressionStartWithoutWhitespace: true))
            {
                depth++;
                conditionalLines[index] = true;
                continue;
            }

            conditionalLines[index] = depth > 0;
            if (IsJsonTrustConditionalCompilationDirective(trimmed, "endif", allowExpressionStartWithoutWhitespace: false))
            {
                depth = Math.Max(0, depth - 1);
            }
        }

        return conditionalLines;
    }

    private static bool IsJsonTrustConditionalCompilationDirective(
        ReadOnlySpan<char> line,
        ReadOnlySpan<char> directive,
        bool allowExpressionStartWithoutWhitespace)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length || line[index] != '#')
            return false;

        index++;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (!line[index..].StartsWith(directive, StringComparison.Ordinal))
            return false;

        index += directive.Length;
        return index == line.Length
            || char.IsWhiteSpace(line[index])
            || (allowExpressionStartWithoutWhitespace && line[index] is '(' or '!');
    }

    private static bool TryParseJsonTrustBoundaryAnnotation(
        string payload,
        out JsonTrustBoundaryEvidence evidence)
    {
        evidence = null!;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in payload.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equalsIndex = token.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex == token.Length - 1)
                return false;
            if (!values.TryAdd(token[..equalsIndex], token[(equalsIndex + 1)..]))
                return false;
        }

        if (values.Count != 5
            || !values.TryGetValue("origin", out var origin)
            || !values.TryGetValue("direction", out var direction)
            || !values.TryGetValue("sensitivity", out var sensitivity)
            || !values.TryGetValue("trust", out var trust)
            || !values.TryGetValue("rationale", out var rationale)
            || origin is not ("private_local" or "public_api" or "network" or "file" or "external" or "unknown")
            || direction is not ("read" or "write")
            || sensitivity is not ("diagnostic" or "public" or "untrusted" or "confidential" or "unknown")
            || trust is not ("controlled" or "untrusted" or "review_required")
            || !IsValidJsonTrustBoundaryRationale(rationale))
        {
            return false;
        }

        evidence = new JsonTrustBoundaryEvidence(
            origin,
            direction,
            sensitivity,
            trust,
            rationale,
            string.Empty,
            null);
        return true;
    }

    private static bool IsValidJsonTrustBoundaryRationale(string value)
        => value.Length is > 0 and <= 80
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');
    private static SearchRecipeSemanticEvidence GetRegexOperationEvidence(
        DbReader reader,
        SearchDisplayRow row,
        Dictionary<string, RegexBareReceiverContext> bindingPaths)
    {
        var operations = new List<string>();
        int? firstLine = null;
        var foundRiskOperation = false;
        var foundUnresolvedOperation = false;
        var foundSafeOperation = false;

        foreach (var match in GetSemanticEvidenceMatches(row, "Regex."))
        {
            var searchFrom = match.MarkerIndex + "Regex.".Length;
            firstLine ??= match.Line;

            var receiver = ExtractRegexReceiver(match.Text, match.MarkerIndex);
            var member = ExtractIdentifier(match.Text, searchFrom);
            operations.Add(string.IsNullOrWhiteSpace(member) ? receiver : $"{receiver}.{member}");
            if (!IsProvenSystemRegexReceiver(
                    reader,
                    row.Result.Path,
                    receiver,
                    match.Line,
                    match.MarkerIndex,
                    bindingPaths)
                || string.IsNullOrWhiteSpace(member))
            {
                foundUnresolvedOperation = true;
                continue;
            }

            if (member is "Escape" or "Unescape")
            {
                foundSafeOperation = true;
                continue;
            }

            if (member is "IsMatch" or "Match" or "Matches" or "Replace" or "Split" or "EnumerateMatches" or "Count")
                foundRiskOperation = true;
            else
                foundUnresolvedOperation = true;
        }

        var operation = string.Join(",", operations.Distinct(StringComparer.Ordinal));
        if (foundRiskOperation)
            return new SearchRecipeSemanticEvidence(false, "regex_pattern_operation", "matched_pattern_operation", operation, null, firstLine);
        if (foundUnresolvedOperation || !foundSafeOperation)
            return new SearchRecipeSemanticEvidence(false, "regex_operation_unresolved", "receiver_or_member_not_proven_safe", operation, null, firstLine);
        return new SearchRecipeSemanticEvidence(true, "safe_escape_helper", "escape_helper_does_not_execute_pattern", operation, null, firstLine);
    }

    private static string ExtractRegexReceiver(string text, int regexIndex)
    {
        var start = regexIndex;
        while (start > 0 && IsQualifiedIdentifierCharacter(text[start - 1]))
            start--;
        return text[start..(regexIndex + "Regex".Length)].Trim('.');
    }

    private static bool IsProvenSystemRegexReceiver(
        DbReader reader,
        string path,
        string receiver,
        int line,
        int regexIndex,
        Dictionary<string, RegexBareReceiverContext> bindingPaths)
    {
        if (string.Equals(receiver, "System.Text.RegularExpressions.Regex", StringComparison.Ordinal)
            || string.Equals(receiver, "global::System.Text.RegularExpressions.Regex", StringComparison.Ordinal))
        {
            return true;
        }
        if (!string.Equals(receiver, "Regex", StringComparison.Ordinal))
            return false;

        if (!bindingPaths.TryGetValue(path, out var binding)
            || !binding.HasSystemNamespaceImport
            || binding.HasAliasDeclaration)
        {
            return false;
        }

        var resolution = reader.GetReferencePositionResolution(path, "Regex", line, regexIndex + 1, maxCandidates: 1);
        return resolution.IdentityAvailable
            && !resolution.CandidatesTruncated
            && resolution.Candidates.Count == 0;
    }

    private static Dictionary<string, RegexBareReceiverContext> BuildRegexBareReceiverContexts(
        DbReader reader,
        QueryCommandOptions options,
        IReadOnlyCollection<SearchDisplayRow> rows)
    {
        var paths = rows
            .Select(row => row.Result.Path)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var contexts = paths.ToDictionary(
            path => path,
            _ => new RegexBareReceiverContext(false, false),
            StringComparer.Ordinal);
        foreach (var pathBatch in paths.Chunk(100))
        {
            var usingResults = reader.Search(
                "using",
                int.MaxValue,
                options.Lang,
                rawQuery: false,
                pathPatterns: pathBatch,
                excludePathPatterns: null,
                excludeTests: false,
                deduplicate: false,
                since: options.Since,
                exact: true,
                prefix: false,
                visibilityRank: false);
            foreach (var result in usingResults)
            {
                if (!contexts.TryGetValue(result.Path, out var context))
                    continue;

                foreach (var match in GetCodeSemanticMatches(result, "using"))
                {
                    var directive = ParseRegexUsingDirective(match.ContinuationText, match.MarkerIndex);
                    context = new RegexBareReceiverContext(
                        context.HasSystemNamespaceImport || directive.HasSystemNamespaceImport,
                        context.HasAliasDeclaration || directive.HasAliasDeclaration);
                }

                contexts[result.Path] = context;
            }
        }

        return contexts;
    }

    private static RegexBareReceiverContext ParseRegexUsingDirective(string content, int usingIndex)
    {
        var cursor = usingIndex + "using".Length;
        if ((usingIndex > 0 && IsIdentifierCharacter(content[usingIndex - 1]))
            || (cursor < content.Length && IsIdentifierCharacter(content[cursor])))
        {
            return new RegexBareReceiverContext(false, false);
        }

        cursor = SkipCSharpTrivia(content, cursor);
        var firstIdentifier = ExtractIdentifier(content, cursor);
        if (string.Equals(firstIdentifier, "Regex", StringComparison.Ordinal))
        {
            cursor = SkipCSharpTrivia(content, cursor + firstIdentifier.Length);
            return new RegexBareReceiverContext(false, cursor < content.Length && content[cursor] == '=');
        }

        foreach (var identifier in new[] { "System", "Text", "RegularExpressions" })
        {
            var actual = ExtractIdentifier(content, cursor);
            if (!string.Equals(actual, identifier, StringComparison.Ordinal))
                return new RegexBareReceiverContext(false, false);
            cursor = SkipCSharpTrivia(content, cursor + actual.Length);
            if (!string.Equals(identifier, "RegularExpressions", StringComparison.Ordinal))
            {
                if (cursor >= content.Length || content[cursor] != '.')
                    return new RegexBareReceiverContext(false, false);
                cursor = SkipCSharpTrivia(content, cursor + 1);
            }
        }

        return new RegexBareReceiverContext(
            cursor < content.Length && content[cursor] == ';',
            false);
    }

    private static int SkipCSharpTrivia(string text, int start)
    {
        var cursor = start;
        while (cursor < text.Length)
        {
            if (char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
                continue;
            }
            if (cursor + 1 < text.Length && text[cursor] == '/' && text[cursor + 1] == '/')
            {
                cursor += 2;
                while (cursor < text.Length && text[cursor] is not '\r' and not '\n')
                    cursor++;
                continue;
            }
            if (cursor + 1 < text.Length && text[cursor] == '/' && text[cursor + 1] == '*')
            {
                var commentEnd = text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return text.Length;
                cursor = commentEnd + 2;
                continue;
            }

            break;
        }

        return cursor;
    }

    private static bool IsIdentifierCharacter(char value)
        => char.IsLetterOrDigit(value) || value == '_';

    private static bool IsQualifiedIdentifierCharacter(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '.' or ':';

    private static string ExtractIdentifier(string text, int start)
    {
        var end = start;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            end++;
        return text[start..end];
    }

    private static SearchRecipeSemanticEvidence GetShellExecuteAssignmentEvidence(SearchDisplayRow row)
    {
        var values = new List<string>();
        int? firstLine = null;
        var foundFalse = false;
        var foundTrue = false;
        var foundUnresolved = false;

        foreach (var match in GetSemanticEvidenceMatches(row, "UseShellExecute"))
        {
            var searchFrom = match.MarkerIndex + "UseShellExecute".Length;
            firstLine ??= match.Line;
            var value = ExtractAssignedBooleanLiteral(match.ContinuationText, searchFrom);
            values.Add(value ?? "unresolved");
            if (string.Equals(value, "false", StringComparison.Ordinal))
                foundFalse = true;
            else if (string.Equals(value, "true", StringComparison.Ordinal))
                foundTrue = true;
            else
                foundUnresolved = true;
        }

        var valueEvidence = string.Join(",", values.Distinct(StringComparer.Ordinal));
        if (foundTrue)
            return new SearchRecipeSemanticEvidence(false, "shell_explicitly_enabled", "literal_true_enables_shell", "UseShellExecute", valueEvidence, firstLine);
        if (foundUnresolved || !foundFalse)
            return new SearchRecipeSemanticEvidence(false, "shell_policy_unresolved", "assigned_value_not_literal_boolean", "UseShellExecute", valueEvidence, firstLine);
        return new SearchRecipeSemanticEvidence(true, "shell_explicitly_disabled", "literal_false_disables_shell", "UseShellExecute", valueEvidence, firstLine);
    }

    private static string? ExtractAssignedBooleanLiteral(string text, int start)
    {
        var cursor = start;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            cursor++;
        if (cursor >= text.Length || (text[cursor] != '=' && text[cursor] != ':'))
            return null;
        if (text[cursor] == '=' && cursor + 1 < text.Length && text[cursor + 1] is '=' or '>')
            return null;

        cursor++;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            cursor++;
        var value = ExtractIdentifier(text, cursor);
        if (value is not ("false" or "true"))
            return null;

        cursor += value.Length;
        cursor = SkipCSharpTrivia(text, cursor);
        if (cursor == text.Length || text[cursor] is ',' or ';' or '}' or ')' or ']')
            return value;
        return null;
    }

    private static IEnumerable<SearchRecipeSemanticMatch> GetSemanticEvidenceMatches(SearchDisplayRow row, string marker)
    {
        var contentLines = row.Result.Content.Split('\n', StringSplitOptions.None);
        var matches = new List<SearchRecipeSemanticMatch>();
        var seen = new HashSet<(int Line, int MarkerIndex)>();
        foreach (var facet in row.Compact.MatchFacets
                     .Where(facet => string.Equals(facet.Origin, SearchMatchClassifier.Code, StringComparison.Ordinal))
                     .OrderBy(facet => facet.Line)
                     .ThenBy(facet => facet.Column))
        {
            if (facet.Line < row.Result.StartLine || facet.Line - row.Result.StartLine >= contentLines.Length)
                continue;

            var contentLineIndex = facet.Line - row.Result.StartLine;
            var text = contentLines[contentLineIndex].TrimEnd('\r');
            var continuationText = string.Join('\n', contentLines.Skip(contentLineIndex));
            var facetStart = Math.Max(0, facet.Column - 1);
            var facetEnd = facetStart + Math.Max(1, facet.Length);
            for (var searchFrom = 0; searchFrom < text.Length;)
            {
                var markerIndex = text.IndexOf(marker, searchFrom, StringComparison.Ordinal);
                if (markerIndex < 0)
                    break;
                searchFrom = markerIndex + marker.Length;
                var markerEnd = markerIndex + marker.Length;
                if (markerIndex >= facetEnd || facetStart >= markerEnd || !seen.Add((facet.Line, markerIndex)))
                    continue;

                matches.Add(new SearchRecipeSemanticMatch(text, continuationText, facet.Line, markerIndex));
            }
        }

        return matches;
    }

    private static IEnumerable<SearchRecipeSemanticMatch> GetCodeSemanticMatches(SearchResult result, string marker)
    {
        var contentLines = result.Content.Split('\n', StringSplitOptions.None);
        for (var contentLineIndex = 0; contentLineIndex < contentLines.Length; contentLineIndex++)
        {
            var text = contentLines[contentLineIndex].TrimEnd('\r');
            var continuationText = string.Join('\n', contentLines.Skip(contentLineIndex));
            for (var searchFrom = 0; searchFrom < text.Length;)
            {
                var markerIndex = text.IndexOf(marker, searchFrom, StringComparison.Ordinal);
                if (markerIndex < 0)
                    break;
                searchFrom = markerIndex + marker.Length;
                var line = result.StartLine + contentLineIndex;
                var facet = SearchMatchClassifier.Classify(
                    result.Path,
                    result.Lang,
                    line,
                    text,
                    markerIndex + 1,
                    marker.Length,
                    result.EnclosingSymbolKind);
                if (string.Equals(facet.Origin, SearchMatchClassifier.Code, StringComparison.Ordinal))
                    yield return new SearchRecipeSemanticMatch(text, continuationText, line, markerIndex);
            }
        }
    }

    private sealed record SearchRecipeSemanticEvidence(
        bool Suppress,
        string Category,
        string Reason,
        string Operation,
        string? Value,
        int? Line);

    private sealed record RegexBareReceiverContext(bool HasSystemNamespaceImport, bool HasAliasDeclaration);

    private sealed record SearchRecipeSemanticMatch(string Text, string ContinuationText, int Line, int MarkerIndex);

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

    private readonly record struct JsonTrustMatchSite(int Line, int? Column, int? Length);

    private sealed record JsonTrustBoundaryEvidence(
        string Origin,
        string Direction,
        string Sensitivity,
        string Trust,
        string Rationale,
        string AnnotationStatus,
        int? AnnotationLine);

    private sealed record JsonTrustLexicalContext(
        string[] SourceLines,
        string[] MaskedLines,
        int[] AnnotationLines,
        bool[] ConditionalCompilationLines);

    private sealed class JsonTrustLexicalContextCache
    {
        public string? Path { get; set; }
        public int LoadedThroughLine { get; set; }
        public bool SourceLimitReached { get; set; }
        public JsonTrustLexicalContext? Context { get; set; }
    }

    private static SearchRecipeRunSummaryJsonResult BuildSearchRecipeRunSummary(
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        int limitPerQuery,
        int? totalLimit,
        int emittedResultCount,
        QueryCommandInvocationContext invocationContext,
        SearchQueryFreshnessContext freshnessContext,
        IReadOnlyList<SearchQueryFreshnessObservation> freshnessObservations)
        => new(
            limitPerQuery,
            totalLimit,
            emittedResultCount,
            queryResults.Count(query => query.Truncated),
            queryResults.Sum(query => query.MinimumOmittedResultCount),
            BuildSearchRecipeQueryFreshness(freshnessContext, freshnessObservations),
            queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor)),
            BuildSearchRecipeCursoringHint(
                queryResults.Any(query => query.Truncated),
                queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor)),
                invocationContext),
            queryResults.Sum(query => query.SourceTotal),
            queryResults.All(query => query.SourceTotalAuthoritative),
            queryResults.All(query => query.SourceTotalAuthoritative)
                ? null
                : queryResults.Sum(query => query.SourceTotal),
            queryResults.Sum(query => query.SelectedTotal),
            queryResults.Sum(query => query.Returned),
            queryResults.Sum(query => query.SelectorOmittedCount),
            queryResults.Sum(query => query.LimitOmittedCount));

    private static SearchRecipeRunSummaryJsonResult BuildSearchRecipeRunSummary(
        IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults,
        int limitPerQuery,
        int? totalLimit,
        int emittedResultCount,
        QueryCommandInvocationContext invocationContext,
        SearchQueryFreshnessContext freshnessContext,
        IReadOnlyList<SearchQueryFreshnessObservation> freshnessObservations)
        => new(
            limitPerQuery,
            totalLimit,
            emittedResultCount,
            queryResults.Count(query => query.Truncated),
            queryResults.Sum(query => query.MinimumOmittedResultCount),
            BuildSearchRecipeQueryFreshness(freshnessContext, freshnessObservations),
            queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor)),
            BuildSearchRecipeCursoringHint(
                queryResults.Any(query => query.Truncated),
                queryResults.Any(query => query.Truncated && !string.IsNullOrWhiteSpace(query.NextCursor)),
                invocationContext),
            queryResults.Sum(query => query.SourceTotal),
            queryResults.All(query => query.SourceTotalAuthoritative),
            queryResults.All(query => query.SourceTotalAuthoritative)
                ? null
                : queryResults.Sum(query => query.SourceTotal),
            queryResults.Sum(query => query.SelectedTotal),
            queryResults.Sum(query => query.Returned),
            queryResults.Sum(query => query.SelectorOmittedCount),
            queryResults.Sum(query => query.LimitOmittedCount));

    private static void WriteSearchRecipeFreshnessText(SearchRecipeQueryFreshnessJsonResult freshness)
    {
        Console.WriteLine(
            $"Query freshness: {freshness.State} "
            + $"(clean={freshness.CleanQueryCount}, matched={freshness.MatchedQueryCount}, "
            + $"clean zero-match={freshness.CleanZeroMatchQueryCount}, stale={freshness.StaleQueryCount}, "
            + $"invalid={freshness.InvalidQueryCount})");
        if (freshness.StaleQueryNames.Count > 0)
            Console.WriteLine($"Stale queries: {string.Join(", ", freshness.StaleQueryNames)}");
        if (freshness.InvalidQueryNames.Count > 0)
            Console.WriteLine($"Invalid queries: {string.Join(", ", freshness.InvalidQueryNames)}");
    }

    private static int CompleteSearchRecipeOutput(int writeExitCode, bool hasFailures)
    {
        if (writeExitCode != CommandExitCodes.Success || !hasFailures)
            return writeExitCode;

        CommandErrorWriter.WriteStderr(
            $"Error [{CommandErrorCodes.UsageError}]: one or more recipe queries failed; inspect query_freshness.invalid_query_names.");
        return CommandExitCodes.UsageError;
    }

    private static string BuildSearchRecipeCursoringHint(
        bool hasTruncatedQuery,
        bool cursoringAvailable,
        QueryCommandInvocationContext invocationContext)
        => cursoringAvailable
            ? $"When a query is truncated, rerun a single child query with {invocationContext.RecipeCursorSelectorSyntax} --cursor <next_cursor> to page the next result set."
            : hasTruncatedQuery
                ? "Continuation cursors are unavailable for the selected rows; increase --limit or --total-limit and rerun."
                : "No query is truncated, so no continuation cursor is needed.";

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(
        IReadOnlyList<SearchRecipeQueryResultJsonResult> queryResults,
        SearchQueryFreshnessContext context)
        => BuildSearchRecipeQueryFreshness(
            context,
            queryResults.Select(query => SuccessfulSearchQueryObservation(
                context,
                query.Name,
                query.MinimumMatchedCount)));

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(
        IReadOnlyList<SearchRecipeCompactQueryResultJsonResult> queryResults,
        SearchQueryFreshnessContext context)
        => BuildSearchRecipeQueryFreshness(
            context,
            queryResults.Select(query => SuccessfulSearchQueryObservation(
                context,
                query.Name,
                query.MinimumMatchedCount)));

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(
        IReadOnlyList<SearchRecipeCountQueryJsonResult> queryResults,
        SearchQueryFreshnessContext context)
        => BuildSearchRecipeQueryFreshness(
            context,
            queryResults.Select(query => SuccessfulSearchQueryObservation(
                context,
                query.Name,
                query.Count)));

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(
        IEnumerable<(string Name, int Count)> queryResults,
        SearchQueryFreshnessContext context)
        => BuildSearchRecipeQueryFreshness(
            context,
            queryResults.Select(query => SuccessfulSearchQueryObservation(
                context,
                query.Name,
                query.Count)));

    internal static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshnessForTests(
        SearchQueryFreshnessContext context,
        IEnumerable<SearchQueryFreshnessObservation> observations)
        => BuildSearchRecipeQueryFreshness(context, observations);

    private static SearchRecipeQueryFreshnessJsonResult BuildSearchRecipeQueryFreshness(
        SearchQueryFreshnessContext context,
        IEnumerable<SearchQueryFreshnessObservation> observations)
    {
        var observedByName = observations
            .GroupBy(observation => observation.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var states = new List<SearchRecipeQueryFreshnessStateJsonResult>();
        var recipeChanged = !string.Equals(
            context.ExpectedRecipeVersion,
            context.ExecutedRecipeVersion,
            StringComparison.Ordinal);

        foreach (var expected in context.ExpectedQueries)
        {
            if (!observedByName.Remove(expected.Name, out var observation))
            {
                states.Add(new(
                    expected.Name,
                    "invalid",
                    "unknown",
                    null,
                    "missing_query_result",
                    expected.DefinitionVersion));
                continue;
            }

            var resultState = observation.MatchCount switch
            {
                > 0 => "matched",
                0 => "zero_match",
                _ => "unknown",
            };
            if (!observation.ExecutionSucceeded)
            {
                states.Add(new(
                    expected.Name,
                    "invalid",
                    resultState,
                    observation.MatchCount,
                    string.IsNullOrWhiteSpace(observation.FailureReason)
                        ? "query_execution_failed"
                        : observation.FailureReason!,
                    observation.DefinitionVersion));
                continue;
            }

            if (recipeChanged)
            {
                states.Add(new(
                    expected.Name,
                    "stale",
                    resultState,
                    observation.MatchCount,
                    "recipe_definition_changed",
                    observation.DefinitionVersion));
                continue;
            }

            if (!string.Equals(expected.DefinitionVersion, observation.DefinitionVersion, StringComparison.Ordinal))
            {
                states.Add(new(
                    expected.Name,
                    "stale",
                    resultState,
                    observation.MatchCount,
                    "query_definition_changed",
                    observation.DefinitionVersion));
                continue;
            }

            if (string.Equals(context.IndexState, "stale", StringComparison.Ordinal))
            {
                states.Add(new(
                    expected.Name,
                    "stale",
                    resultState,
                    observation.MatchCount,
                    context.IndexReason ?? "index_stale",
                    observation.DefinitionVersion));
                continue;
            }

            states.Add(new(
                expected.Name,
                "clean",
                resultState,
                observation.MatchCount,
                "executed_current_definition",
                observation.DefinitionVersion));
        }

        foreach (var unexpected in observedByName.Values.OrderBy(observation => observation.Name, StringComparer.Ordinal))
        {
            states.Add(new(
                unexpected.Name,
                "invalid",
                unexpected.MatchCount switch
                {
                    > 0 => "matched",
                    0 => "zero_match",
                    _ => "unknown",
                },
                unexpected.MatchCount,
                "unexpected_query_result",
                unexpected.DefinitionVersion));
        }

        var distinctFreshnessStates = states
            .Select(query => query.FreshnessState)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var aggregateState = distinctFreshnessStates.Count switch
        {
            0 => "clean",
            1 => distinctFreshnessStates[0],
            _ => "mixed",
        };
        var staleQueryNames = states
            .Where(query => query.FreshnessState == "stale")
            .Select(query => query.Name)
            .ToList();
        var invalidQueryNames = states
            .Where(query => query.FreshnessState == "invalid")
            .Select(query => query.Name)
            .ToList();
        var cleanZeroMatchQueryNames = states
            .Where(query => query.FreshnessState == "clean" && query.ResultState == "zero_match")
            .Select(query => query.Name)
            .ToList();

        return new(
            states.Count(query => query.ResultState == "matched"),
            states.Count(query => query.ResultState == "zero_match"),
            staleQueryNames,
            aggregateState,
            states.Count(query => query.FreshnessState == "clean"),
            states.Count(query => query.ResultState == "matched"),
            cleanZeroMatchQueryNames.Count,
            cleanZeroMatchQueryNames,
            staleQueryNames.Count,
            invalidQueryNames.Count,
            invalidQueryNames,
            context.IndexState,
            context.IndexReason,
            context.ExecutedRecipeVersion,
            states);
    }

    private static SearchQueryFreshnessObservation SuccessfulSearchQueryObservation(
        SearchQueryFreshnessContext context,
        string name,
        int count)
    {
        var definitionVersion = context.ExpectedQueries
            .FirstOrDefault(query => string.Equals(query.Name, name, StringComparison.Ordinal))
            ?.DefinitionVersion
            ?? SearchQueryFreshnessUnknownDefinitionVersion;
        return new(name, count, definitionVersion, true, null);
    }

    private static SearchQueryFreshnessObservation FailedSearchQueryObservation(
        SearchQueryFreshnessContext context,
        string name,
        string failureReason)
    {
        var definitionVersion = context.ExpectedQueries
            .FirstOrDefault(query => string.Equals(query.Name, name, StringComparison.Ordinal))
            ?.DefinitionVersion
            ?? SearchQueryFreshnessUnknownDefinitionVersion;
        return new(name, null, definitionVersion, false, failureReason);
    }

    private static SearchQueryFreshnessContext BuildSearchRecipeFreshnessContext(
        DbReader reader,
        SearchAuditRecipe recipe,
        IReadOnlyList<SearchAuditRecipeQuery> selectedQueries,
        QueryCommandOptions options)
    {
        var indexState = ResolveSearchQueryIndexFreshness(reader, options, out var indexReason);
        return BuildSearchRecipeFreshnessContext(
            recipe,
            selectedQueries,
            indexState,
            indexReason);
    }

    private static SearchQueryFreshnessContext BuildSearchRecipeFreshnessContext(
        SearchAuditRecipe recipe,
        IReadOnlyList<SearchAuditRecipeQuery> selectedQueries,
        string indexState,
        string? indexReason)
    {
        var recipeVersion = BuildSearchDefinitionVersion(
            "audit-recipe-v1",
            recipe,
            CliJsonSerializerContext.Default.SearchAuditRecipe);
        return new(
            indexState,
            indexReason,
            recipeVersion,
            recipeVersion,
            selectedQueries
                .Select(query => new SearchQueryFreshnessExpectedQuery(
                    query.Name,
                    BuildSearchDefinitionVersion(
                        "audit-recipe-query-v1",
                        query,
                        CliJsonSerializerContext.Default.SearchAuditRecipeQuery)))
                .ToList());
    }

    private static SearchQueryFreshnessContext BuildNamedSearchFreshnessContext(
        DbReader reader,
        IReadOnlyList<SearchNamedQuery> queries,
        QueryCommandOptions options,
        bool userExact)
        => new(
            ResolveSearchQueryIndexFreshness(reader, options, out var indexReason),
            indexReason,
            null,
            null,
            queries
                .Select(query => new SearchQueryFreshnessExpectedQuery(
                    query.Name,
                    BuildSearchDefinitionVersion(
                        "named-query-v1",
                        query.Name,
                        query.Query,
                        options.RawFts.ToString(CultureInfo.InvariantCulture),
                        userExact.ToString(CultureInfo.InvariantCulture),
                        options.Prefix.ToString(CultureInfo.InvariantCulture),
                        options.TokenBoundary.ToString(CultureInfo.InvariantCulture))))
                .ToList());

    private static string ResolveSearchQueryIndexFreshness(
        DbReader reader,
        QueryCommandOptions options,
        out string? reason)
    {
        var health = reader.GetWorkspaceIndexHealth();
        if (health.IndexNewerThanReader)
        {
            reason = "index_newer_than_reader";
            return "stale";
        }
        if (!health.IndexComplete)
        {
            reason = "index_incomplete";
            return "stale";
        }

        var projectRoot = s_activeQueryProjectRoot;
        var indexedHead = reader.GetMetaString(DbContext.IndexedHeadShaMetaKey);
        indexedHead = string.IsNullOrWhiteSpace(indexedHead)
            ? reader.GetMetaString(DbContext.IndexedHeadCommitMetaKey)
            : indexedHead;
        var workspaceHead = string.IsNullOrWhiteSpace(projectRoot)
            ? null
            : GitHelper.TryGetHeadCommit(projectRoot);
        if (!string.IsNullOrWhiteSpace(indexedHead)
            && !string.IsNullOrWhiteSpace(workspaceHead)
            && !string.Equals(indexedHead, workspaceHead, StringComparison.Ordinal))
        {
            reason = "index_head_changed";
            return "stale";
        }

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            SearchQueryFreshnessWorkspaceCheckForTesting?.Invoke();
            var workspaceCheck = IndexFreshnessChecker.Check(
                reader,
                projectRoot,
                internalIndexDatabasePath: DbPathResolver.NormalizeDbPath(options.DbPath));
            if (!workspaceCheck.Checked)
            {
                reason = "index_workspace_unverified";
                return "stale";
            }
            if (!workspaceCheck.MatchesWorkspace)
            {
                reason = workspaceCheck.Reason == "head_changed"
                    ? "index_head_changed"
                    : "index_workspace_changed";
                return "stale";
            }
        }

        reason = null;
        return "current";
    }

    private static string BuildSearchDefinitionVersion<T>(
        string contract,
        T definition,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        var json = JsonSerializer.Serialize(definition, jsonTypeInfo);
        return BuildSearchDefinitionVersion(contract, json);
    }

    private static string BuildSearchDefinitionVersion(string contract, params string[] parts)
    {
        var identity = new StringBuilder(contract);
        foreach (var part in parts)
            identity.Append('\0').Append(part.Length).Append(':').Append(part);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))).ToLowerInvariant();
    }

    internal const string SearchQueryFreshnessUnknownDefinitionVersion = "unknown";

    internal sealed record SearchQueryFreshnessExpectedQuery(
        string Name,
        string DefinitionVersion);

    internal sealed record SearchQueryFreshnessObservation(
        string Name,
        int? MatchCount,
        string DefinitionVersion,
        bool ExecutionSucceeded,
        string? FailureReason);

    internal sealed record SearchQueryFreshnessContext(
        string IndexState,
        string? IndexReason,
        string? ExpectedRecipeVersion,
        string? ExecutedRecipeVersion,
        IReadOnlyList<SearchQueryFreshnessExpectedQuery> ExpectedQueries);

    private static SearchRecipeScopeJsonResult BuildSearchRecipeScope(
        SearchAuditRecipe recipe,
        QueryCommandOptions options,
        IReadOnlyList<SearchAuditRecipeQuery>? selectedQueries = null)
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
        else if (string.Equals(scopeName, SearchAuditRecipes.ProductionAndToolingAuditScope, StringComparison.OrdinalIgnoreCase))
        {
            if (pathPatterns.Count == 0)
                AddDistinct(pathPatterns, ProductionAndToolingScopeDefaults.IncludePaths);
            AddDistinct(excludePaths, ProductionAndToolingScopeDefaults.ExcludePaths);
            excludeTests = true;
        }

        return new SearchRecipeScopeJsonResult(
            scopeName,
            pathPatterns,
            excludePaths,
            excludeTests,
            [.. recipe.DefaultPathPatterns],
            [.. recipe.DefaultExcludePaths],
            options.ShowExcluded ? BuildSearchRecipeExcludedDiagnostics(recipe, options, scopeName, excludeTests) : null)
        {
            Coverage = CreateSearchRecipeCoverage(selectedQueries ?? recipe.Queries),
        };
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
        var productionAndToolingScope = string.Equals(
            scopeName,
            SearchAuditRecipes.ProductionAndToolingAuditScope,
            StringComparison.OrdinalIgnoreCase);
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
        diagnostics.Add(new SearchRecipeExcludedDiagnosticJsonResult(
            "production_and_tooling_exclude_paths",
            productionAndToolingScope && ProductionAndToolingScopeDefaults.ExcludePaths.Count > 0,
            [.. ProductionAndToolingScopeDefaults.ExcludePaths],
            "Production-and-tooling exclusions suppress documentation and recipe definitions while retaining executable automation regardless of directory name."));
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
        SearchQueryFreshnessContext? freshnessContext,
        out int total,
        out int fileCount,
        out List<SearchQueryFreshnessObservation> freshnessObservations,
        out bool hasFailures)
    {
        var queryCounts = new List<SearchNamedBatchCountSummaryQueryJsonResult>();
        freshnessObservations = [];
        var paths = new HashSet<string>(StringComparer.Ordinal);
        total = 0;
        hasFailures = false;
        foreach (var namedQuery in options.NamedSearchQueries)
        {
            try
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
                if (freshnessContext != null)
                {
                    freshnessObservations.Add(SuccessfulSearchQueryObservation(
                        freshnessContext,
                        namedQuery.Name,
                        count));
                }
            }
            catch (Exception ex) when (
                freshnessContext != null
                && TryClassifySearchQueryExecutionFailure(ex, out _))
            {
                TryClassifySearchQueryExecutionFailure(ex, out var failureReason);
                hasFailures = true;
                queryCounts.Add(new SearchNamedBatchCountSummaryQueryJsonResult(
                    namedQuery.Name,
                    namedQuery.Query,
                    0,
                    0));
                var definitionVersion = freshnessContext.ExpectedQueries
                    .First(query => string.Equals(query.Name, namedQuery.Name, StringComparison.Ordinal))
                    .DefinitionVersion;
                freshnessObservations.Add(new(
                    namedQuery.Name,
                    null,
                    definitionVersion,
                    false,
                    failureReason));
            }
        }

        fileCount = paths.Count;
        return queryCounts;
    }

    private static bool TryClassifySearchQueryExecutionFailure(Exception exception, out string reason)
    {
        reason = exception switch
        {
            FtsQuerySyntaxException => "query_syntax_invalid",
            SearchGuardCandidateLimitException => "query_guard_limit_exceeded",
            SearchQueryLimitException => "query_limit_exceeded",
            _ => string.Empty,
        };
        return reason.Length > 0;
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
                queryResult.NextCursor,
                queryResult.SourceTotal,
                queryResult.SourceTotalAuthoritative,
                queryResult.SourceTotalLowerBound,
                queryResult.SelectedTotal,
                queryResult.Returned,
                queryResult.SelectorOmittedCount,
                queryResult.LimitOmittedCount,
                queryResult.Selectors),
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
                queryResult.SourceTotal,
                queryResult.SourceTotalAuthoritative,
                queryResult.SourceTotalLowerBound,
                queryResult.SelectedTotal,
                queryResult.Returned,
                queryResult.SelectorOmittedCount,
                queryResult.LimitOmittedCount,
                queryResult.Selectors,
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

    private static string BuildSearchRecipeReplayCommand(
        SearchAuditRecipe recipe,
        QueryCommandOptions options,
        string? queryName = null,
        bool summaryOnly = false,
        bool includeMaxJsonBytes = true,
        bool includeTotalLimit = true)
    {
        var recipeSelector = string.IsNullOrWhiteSpace(queryName)
            ? recipe.Name
            : $"{recipe.Name}/{queryName}";
        var args = new List<string>();
        options.InvocationContext.AddRecipeCommandPrefix(args, recipeSelector);
        args.Add("--format");
        args.Add(OutputFormatIssueDrafts);
        if (summaryOnly)
            args.Add("--summary-only");
        args.Add("--limit");
        args.Add(options.Limit.ToString(CultureInfo.InvariantCulture));

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
        if (includeTotalLimit && options.TotalLimit.HasValue)
            AddReplayValueOption(args, "--total-limit", options.TotalLimit.Value.ToString(CultureInfo.InvariantCulture));
        AddReplayValueOption(args, "--snippet-lines", options.SnippetLines.ToString(CultureInfo.InvariantCulture));
        AddReplayValueOption(args, "--snippet-focus", FormatSearchSnippetFocusMode(options.SnippetFocus));
        AddReplayValueOption(args, "--max-line-width", options.MaxLineWidth.ToString(CultureInfo.InvariantCulture));
        if (includeMaxJsonBytes && options.MaxJsonBytes.HasValue)
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
        => BuildAdHocSearchReplayCommand(options, OutputFormatIssueDrafts);

    private static string BuildAdHocSearchReplayCommand(QueryCommandOptions options, string outputFormat)
    {
        var args = new List<string>
        {
            "cdidx",
            "search",
            "--query",
            options.Query!,
        };
        AddReplayValueOption(args, "--format", outputFormat);
        AddReplayValueOption(args, "--limit", options.Limit.ToString(CultureInfo.InvariantCulture));

        if (options.DbPathExplicit)
            AddReplayValueOption(args, "--db", options.DbPath);
        if (!string.IsNullOrWhiteSpace(options.DataDir))
            AddReplayValueOption(args, "--data-dir", options.DataDir);
        if (options.SourceOnly)
            args.Add("--source-only");
        else if (options.AuditScopeExplicit)
            AddReplayValueOption(args, "--audit-scope", options.AuditScope);
        if (!string.IsNullOrWhiteSpace(options.Lang))
            AddReplayValueOption(args, "--lang", options.Lang);
        if (!string.IsNullOrWhiteSpace(options.SolutionFilter))
            AddReplayValueOption(args, "--solution", options.SolutionFilter);
        foreach (var projectFilter in options.ProjectFilters)
            AddReplayValueOption(args, "--project", projectFilter);
        foreach (var pathPattern in options.PathPatterns)
            AddReplayValueOption(args, "--path", pathPattern);
        foreach (var excludePath in options.ExcludePaths)
            AddReplayValueOption(args, "--exclude-path", excludePath);
        if (options.ExcludeTests)
            args.Add("--exclude-tests");
        if (options.IncludeGenerated)
            args.Add("--include-generated");
        if (options.ExcludeComments)
            args.Add("--exclude-comments");
        if (options.ExcludeStrings)
            args.Add("--exclude-strings");
        if (options.ExcludeFixtures)
            args.Add("--exclude-fixtures");
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
        if (options.Prefix)
            args.Add("--prefix");
        if (options.TokenBoundary)
            args.Add("--token-boundary");
        foreach (var guardFilter in options.GuardFilters)
            AddReplayValueOption(args, BuildSearchGuardReplayOptionName(guardFilter), guardFilter.Query);
        if (options.GuardFilters.Count > 0 && options.GuardWindow != DbReader.DefaultSearchGuardWindow)
            AddReplayValueOption(args, "--guard-window", options.GuardWindow.ToString(CultureInfo.InvariantCulture));
        if (options.GuardFilters.Count > 0 && options.GuardScope != SearchGuardScope.Window)
            AddReplayValueOption(args, "--guard-scope", FormatSearchGuardScope(options.GuardScope));
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
        if (string.Equals(outputFormat, OutputFormatIssueDrafts, StringComparison.Ordinal))
        {
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
        }

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
            [.. query.Aliases],
            [.. query.DeprecatedAliases],
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
                filter.Scope switch
                {
                    SearchGuardScope.Window => "window",
                    SearchGuardScope.SameLine => "same_line",
                    SearchGuardScope.Container => "container",
                    _ => null,
                },
                filter.EvidenceKind switch
                {
                    SearchGuardEvidenceKind.CSharpBoundedFileRead => "csharp_bounded_file_read",
                    SearchGuardEvidenceKind.CSharpEnumerationOptions => "csharp_enumeration_options",
                    _ => null,
                }))
            .ToList();

}
