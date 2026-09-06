using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{

    private JsonNode ExecuteSearch(JsonNode? id, JsonNode? args)
    {
        var listRecipes = args?["listRecipes"]?.GetValue<bool>() ?? false;
        if (listRecipes)
            return ExecuteSearchRecipeList(id);

        var recipeNode = args?["recipe"];
        if (recipeNode is not null)
        {
            var recipeName = recipeNode.GetValue<string>();
            if (string.IsNullOrWhiteSpace(recipeName))
                return CreateToolErrorResponse(id, "'recipe' must be a non-empty search recipe name.");
            return ExecuteSearchRecipe(id, args, recipeName.Trim());
        }

        if (args?["auditScope"] is not null)
            return CreateToolErrorResponse(id, "'auditScope' is only supported with recipe execution.");

        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());

        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var snippetLines = ReadSnippetLines(args, SearchSnippetFormatter.DefaultSnippetLines, adjustments);
        var snippetFocusText = args?["snippetFocus"]?.GetValue<string>() ?? "quality";
        if (!QueryCommandRunner.TryParseSnippetFocusMode(snippetFocusText, out var snippetFocus))
            return CreateToolErrorResponse(id, "snippetFocus must be one of quality, leftmost, proximity");
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var rawQuery = args?["rawQuery"]?.GetValue<bool>() ?? false;
        SearchCursor? cursor = null;
        var cursorValue = args?["cursor"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(cursorValue))
        {
            if (!TryParseSearchCursor(cursorValue, out var parsedCursor))
                return CreateToolErrorResponse(id, "'cursor' must be a search pagination cursor returned as `next_cursor` by a previous search response.");
            cursor = parsedCursor;
        }
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
        var deduplicate = !(args?["noDedup"]?.GetValue<bool>() ?? false);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        if (!TryResolveSearchExactArgument(args, out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var tokenBoundary = args?["tokenBoundary"]?.GetValue<bool>() ?? false;
        var exactSearch = exact || tokenBoundary;
        var prefix = args?["prefix"]?.GetValue<bool>() ?? false;
        if (tokenBoundary && rawQuery)
            return CreateToolErrorResponse(id, "'tokenBoundary' cannot be combined with 'rawQuery'.");
        if (prefix && exactSearch)
            return CreateToolErrorResponse(id, "'prefix' cannot be combined with 'exact' / 'exactSubstring' / 'tokenBoundary' (exact uses instr(), not FTS5 prefix phrases).");
        if (TryReadSearchGuardFilters(id, args, out var guardFilters) is JsonNode guardError)
            return guardError;
        if (TryReadSearchGuardScope(id, args, out var guardScope) is JsonNode guardScopeError)
            return guardScopeError;
        var guardWindow = ReadOptionalIntArgument(args, "guardWindow") ?? DbReader.DefaultSearchGuardWindow;
        if (guardWindow < 0 || guardWindow > DbReader.MaxSearchGuardWindow)
            return CreateToolErrorResponse(id, $"'guardWindow' must be between 0 and {DbReader.MaxSearchGuardWindow}; got {guardWindow}.");
        var suggestExactSubstring = SearchQueryAdvisor.ShouldSuggestExactSubstring(query, rawQuery, exactSearch, prefix);

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                List<SearchResult> countResults;
                try
                {
                    countResults = reader.Search(query, MaxLimit, lang, rawQuery, pathPatterns, excludePaths, excludeTests, deduplicate, since, exactSearch, prefix, guardFilters: guardFilters, guardWindow: guardWindow, guardScope: guardScope, tokenBoundary: tokenBoundary);
                }
                catch (SearchQueryLimitException)
                {
                    return CreateToolErrorResponse(id, FormatLiteralSearchQueryLimitError());
                }
                catch (SearchGuardCandidateLimitException ex)
                {
                    return CreateToolErrorResponse(id, FormatSearchGuardCandidateLimitError(ex));
                }
                var truncatedCount = countResults.Count >= MaxLimit;
                var payload = BuildCountOnlyPayload(countResults.Count, truncatedCount ? null : countResults.Count, truncatedCount, countResults, result => result.Path);
                payload["query"] = query;
                payload["rawQuery"] = rawQuery;
                if (tokenBoundary)
                    payload["tokenBoundary"] = true;
                payload["snippetFocus"] = snippetFocusText.Trim().ToLowerInvariant();
                payload["path"] = PathEcho(pathPatterns);
                payload["excludeTests"] = excludeTests;
                AddSearchStabilityMetadata(payload, reader, cursor, []);
                if (suggestExactSubstring)
                    AddExactSubstringRecoveryHint(payload, query);
                if (countResults.Count == 0)
                    AddFtsQueryDiagnostics(payload, DbReader.AnalyzeFtsQuery(query, rawQuery, prefix, lang));
                adjustments.ApplyTo(payload);
                return CreateToolResult(id, $"Counted {countResults.Count} search result(s).", payload);
            }

            List<SearchResult> results;
            try
            {
                results = reader.Search(query, FetchLimitForEnvelope(limit), lang, rawQuery, pathPatterns, excludePaths, excludeTests, deduplicate, since, exactSearch, prefix, cursor: cursor, guardFilters: guardFilters, guardWindow: guardWindow, guardScope: guardScope, tokenBoundary: tokenBoundary);
            }
            catch (SearchQueryLimitException)
            {
                return CreateToolErrorResponse(id, FormatLiteralSearchQueryLimitError());
            }
            catch (SearchGuardCandidateLimitException ex)
            {
                return CreateToolErrorResponse(id, FormatSearchGuardCandidateLimitError(ex));
            }
            var ftsDiagnostics = DbReader.AnalyzeFtsQuery(query, rawQuery, prefix, lang);
            var truncated = TrimToRequestedLimit(results, limit);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["rawQuery"] = rawQuery,
                    ["tokenBoundary"] = tokenBoundary,
                    ["snippetLines"] = snippetLines,
                    ["snippetFocus"] = snippetFocusText.Trim().ToLowerInvariant(),
                    ["maxLineWidth"] = maxLineWidth,
                    ["path"] = PathEcho(pathPatterns),
                    ["excludeTests"] = excludeTests,
                    ["results"] = new JsonArray()
                };
                AddSearchStabilityMetadata(payload, reader, cursor, results);
                AddFtsQueryDiagnostics(payload, ftsDiagnostics);
                AddResultEnvelope(payload, 0, 0, truncated: false);
                if (suggestExactSubstring)
                {
                    AddExactSubstringRecoveryHint(payload, query);
                }
                else
                {
                    AddRecoveryHint(
                        payload,
                        "no_results",
                        "search returned no rows; try removing lang/path filters, using prefix for token-prefix matches, or using exactSubstring for literal punctuation or emoji.",
                        "search",
                        new JsonObject { ["query"] = query, ["limit"] = 5 });
                }
                AddFreshnessHint(payload, reader);
                adjustments.ApplyTo(payload);
                return CreateToolResult(id, "No results found.", payload);
            }

            var queryContext = SearchSnippetFormatter.PrepareQueryContext(query);
            var compactResults = SearchSnippetFormatter
                .ToCompactResults(results, queryContext, snippetLines, exactSearch, maxLineWidth, lang, snippetFocus, exposeLiteralHighlights: exactSearch)
                .ToList();
            foreach (var compact in compactResults)
                SearchSnippetFormatter.ApplyOutputMetadata(compact, snippetLines, maxLineWidth, exactSearch, rawQuery);
            var structured = new JsonObject
            {
                ["query"] = query,
                ["rawQuery"] = rawQuery,
                ["tokenBoundary"] = tokenBoundary,
                ["cursor"] = cursorValue,
                ["snippetLines"] = snippetLines,
                ["snippetFocus"] = snippetFocusText.Trim().ToLowerInvariant(),
                ["maxLineWidth"] = maxLineWidth,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["results"] = ToJsonArray(compactResults)
            };
            AddSearchStabilityMetadata(structured, reader, cursor, results, truncated);
            AddResultEnvelope(structured, results.Count, truncated ? null : results.Count, truncated);
            if (format == "compact")
                ApplyCompactResults(
                    structured,
                    compactResults,
                    result => result.Path,
                    result => result.MatchLines.Count > 0 ? result.MatchLines[0] : result.ChunkStartLine);
            var topResult = results[0];
            AddNextStepSuggestion(
                structured,
                "excerpt",
                BuildExcerptArgs(topResult.Path, topResult.StartLine, topResult.EndLine),
                "Use excerpt on the top hit before editing; for symbol changes, follow with definition or references to confirm declarations and usage sites.");
            if (suggestExactSubstring)
                AddExactSubstringRecoveryHint(structured, query);
            adjustments.ApplyTo(structured);
            // Include top file paths in summary for quick AI orientation
            // AIが素早く位置把握できるよう、サマリにトップファイルパスを含める
            var topPaths = results.Select(r => r.Path).Distinct().Take(3);
            var summary = $"Found {results.Count} search result(s) in {string.Join(", ", topPaths)}.";
            return CreateToolResult(id, summary, structured);
        });
    }

    private JsonNode ExecuteSearchRecipeList(JsonNode? id)
    {
        var registry = SearchAuditRecipes.Load();
        var payload = new JsonObject
        {
            ["count"] = registry.Recipes.Count,
            ["recipes"] = ToSearchRecipeArray(registry.Recipes)
        };
        AddSearchRecipeSourceDiagnostics(payload, registry.Diagnostics);
        return CreateToolResult(id, $"Found {registry.Recipes.Count} search recipe(s).", payload);
    }

    private JsonNode ExecuteSearchRecipe(JsonNode? id, JsonNode? args, string recipeName)
    {
        var registry = SearchAuditRecipes.Load();
        var recipe = registry.Recipes.FirstOrDefault(r => string.Equals(r.Name, recipeName, StringComparison.OrdinalIgnoreCase));
        if (recipe is null)
        {
            var available = string.Join(", ", registry.Recipes.Select(r => r.Name));
            return CreateToolErrorResponse(id, $"unknown search recipe '{recipeName}'. Available recipes: {available}.");
        }

        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var snippetLines = ReadSnippetLines(args, SearchSnippetFormatter.DefaultSnippetLines, adjustments);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        List<string> requestedPathPatterns = pathPatterns is null ? [] : [.. pathPatterns];
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveMcpRecipeAuditScope(args, recipe, ref pathPatterns, excludePaths, ref excludeTests, out var auditScope, out var auditScopeError))
            return CreateToolErrorResponse(id, auditScopeError!);
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
        var deduplicate = !(args?["noDedup"]?.GetValue<bool>() ?? false);
        var includeGenerated = args?["includeGenerated"]?.GetValue<bool>() ?? false;
        if (args?["tokenBoundary"]?.GetValue<bool>() ?? false)
            return CreateToolErrorResponse(id, "'tokenBoundary' is only supported for ad hoc search, not recipe execution.");
        if (!TryResolveSearchExactArgument(args, out var userExact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var hasExactOverride = args?["exact"] is not null || args?["exactSubstring"] is not null;
        if (args?["prefix"]?.GetValue<bool>() ?? false)
            return CreateToolErrorResponse(id, "'prefix' cannot be combined with recipe execution.");
        if (args?["cursor"] is not null)
            return CreateToolErrorResponse(id, "'cursor' is not supported for recipe execution.");
        if (TryReadSearchGuardFilters(id, args, out var guardFilters) is JsonNode guardError)
            return guardError;
        if (TryReadSearchGuardScope(id, args, out var guardScope) is JsonNode guardScopeError)
            return guardScopeError;
        var guardWindow = ReadOptionalIntArgument(args, "guardWindow") ?? DbReader.DefaultSearchGuardWindow;
        if (guardWindow < 0 || guardWindow > DbReader.MaxSearchGuardWindow)
            return CreateToolErrorResponse(id, $"'guardWindow' must be between 0 and {DbReader.MaxSearchGuardWindow}; got {guardWindow}.");

        var scope = new SearchRecipeScopeJsonResult(
            auditScope,
            pathPatterns is null ? [] : [.. pathPatterns],
            [.. excludePaths],
            excludeTests,
            [.. recipe.DefaultPathPatterns],
            [.. recipe.DefaultExcludePaths],
            null)
        {
            Coverage = QueryCommandRunner.CreateSearchRecipeCoverage(recipe.Queries),
        };

        return WithDbReader(id, args, reader =>
        {
            QueryCommandRunner.EnsureSearchRecipeCoverage(reader, scope, lang, since, includeGenerated);
            var queryResults = new JsonArray();
            var total = 0;
            foreach (var recipeQuery in recipe.Queries)
            {
                var exact = hasExactOverride ? userExact : recipeQuery.ExactSubstring;
                ResolveMcpRecipeQueryScope(
                    recipeQuery,
                    auditScope,
                    pathPatterns,
                    excludePaths,
                    out var queryPathPatterns,
                    out var queryExcludePaths);
                var requiredPathPatterns = GetMcpSearchRecipeRequiredPathPatterns(requestedPathPatterns, recipeQuery);
                List<SearchResult> results;
                try
                {
                    results = reader.Search(
                        recipeQuery.Query,
                        FetchLimitForSearchRecipeEnvelope(limit),
                        lang,
                        false,
                        queryPathPatterns,
                        queryExcludePaths,
                        excludeTests,
                        deduplicate,
                        since,
                        exact,
                        false,
                        guardFilters: guardFilters,
                        guardWindow: guardWindow,
                        guardScope: guardScope,
                        requiredPathPatterns: requiredPathPatterns,
                        resultRanking: recipeQuery.ResultRanking);
                }
                catch (SearchQueryLimitException)
                {
                    return CreateToolErrorResponse(id, FormatLiteralSearchQueryLimitError());
                }
                catch (SearchGuardCandidateLimitException ex)
                {
                    return CreateToolErrorResponse(id, FormatSearchRecipeGuardCandidateLimitError(recipe.Name, recipeQuery.Name, ex));
                }

                var queryContext = SearchSnippetFormatter.PrepareQueryContext(recipeQuery.Query);
                var compactResults = SearchSnippetFormatter
                    .ToCompactResults(results, queryContext, snippetLines, exact, maxLineWidth, exposeLiteralHighlights: exact)
                    .Where(result => MatchesRecipeFacetMetadata(result, recipeQuery))
                    .ToList();
                var truncated = TrimToRequestedLimit(compactResults, limit);
                foreach (var compact in compactResults)
                    SearchSnippetFormatter.ApplyOutputMetadata(compact, snippetLines, maxLineWidth, exact, rawFts: false);
                QueryCommandRunner.MarkSearchRecipeQueryExecuted(scope, recipeQuery.Name);
                total += compactResults.Count;
                queryResults.Add(new JsonObject
                {
                    ["name"] = recipeQuery.Name,
                    ["query"] = recipeQuery.Query,
                    ["description"] = recipeQuery.Description,
                    ["recommended_labels"] = ToJsonArray(recipeQuery.RecommendedLabels),
                    ["false_positive_guidance"] = recipeQuery.FalsePositiveGuidance,
                    ["exact_substring"] = exact,
                    ["match_origins"] = ToJsonArray(recipeQuery.MatchOrigins),
                    ["exclude_origins"] = ToJsonArray(recipeQuery.ExcludeOrigins),
                    ["result_kinds"] = ToJsonArray(recipeQuery.ResultKinds),
                    ["count"] = compactResults.Count,
                    ["top_files"] = BuildTopFileHistogram(compactResults, result => result.Path),
                    ["truncated"] = truncated,
                    ["results"] = ToJsonArray(compactResults)
                });
            }

            var payload = new JsonObject
            {
                ["recipe"] = ToSearchRecipeJson(recipe),
                ["query_count"] = recipe.Queries.Count,
                ["result_count"] = total,
                ["limit_per_query"] = limit,
                ["snippetLines"] = snippetLines,
                ["maxLineWidth"] = maxLineWidth,
                ["lang"] = lang,
                ["audit_scope"] = auditScope,
                ["scope"] = JsonSerializer.SerializeToNode(
                    scope,
                    CliJsonSerializerContext.Default.SearchRecipeScopeJsonResult),
                ["path"] = PathEcho(pathPatterns),
                ["excludePaths"] = PathEcho(excludePaths),
                ["excludeTests"] = excludeTests,
                ["queries"] = queryResults
            };
            AddFreshnessHint(payload, reader);
            AddSearchRecipeSourceDiagnostics(payload, registry.Diagnostics);
            adjustments.ApplyTo(payload);
            var summary = total == 0
                ? $"Recipe '{recipe.Name}' returned no search results."
                : $"Recipe '{recipe.Name}' returned {total} search result(s) across {recipe.Queries.Count} query(ies).";
            return CreateToolResult(id, summary, payload);
        });
    }

    private static bool TryResolveMcpRecipeAuditScope(
        JsonNode? args,
        SearchAuditRecipe recipe,
        ref List<string>? pathPatterns,
        List<string> excludePaths,
        ref bool excludeTests,
        out string auditScope,
        out string? error)
    {
        var requestedScope = args?["auditScope"]?.GetValue<string>();
        auditScope = string.IsNullOrWhiteSpace(requestedScope)
            ? recipe.DefaultScope
            : requestedScope.Trim();
        error = null;

        if (!QueryCommandRunner.TryNormalizeSearchAuditScope(auditScope, out auditScope))
        {
            error = "'auditScope' must be 'source', 'production-and-tooling', or 'all'.";
            return false;
        }

        if (string.Equals(auditScope, SearchAuditRecipes.DefaultAuditScope, StringComparison.Ordinal))
        {
            if ((pathPatterns is null || pathPatterns.Count == 0) && recipe.DefaultPathPatterns.Count > 0)
                pathPatterns = [.. recipe.DefaultPathPatterns];
            AddDistinct(excludePaths, recipe.DefaultExcludePaths);
            excludeTests = true;
        }
        else if (string.Equals(auditScope, SearchAuditRecipes.ProductionAndToolingAuditScope, StringComparison.Ordinal))
        {
            if ((pathPatterns is null || pathPatterns.Count == 0) && ProductionAndToolingScopeDefaults.IncludePaths.Count > 0)
                pathPatterns = [.. ProductionAndToolingScopeDefaults.IncludePaths];
            AddDistinct(excludePaths, ProductionAndToolingScopeDefaults.ExcludePaths);
            excludeTests = true;
        }

        return true;
    }

    private static void ResolveMcpRecipeQueryScope(
        SearchAuditRecipeQuery query,
        string auditScope,
        List<string>? recipePathPatterns,
        List<string> recipeExcludePaths,
        out List<string>? queryPathPatterns,
        out List<string> queryExcludePaths)
    {
        var productionAndTooling = string.Equals(
            auditScope,
            SearchAuditRecipes.ProductionAndToolingAuditScope,
            StringComparison.Ordinal);
        var queryUsesSourceDefaultPaths = productionAndTooling
            && query.PathPatterns.SequenceEqual(SourceScopeDefaults.IncludePaths, StringComparer.Ordinal);
        queryPathPatterns = query.PathPatterns.Count > 0 && !queryUsesSourceDefaultPaths
            ? [.. query.PathPatterns]
            : recipePathPatterns is null ? null : [.. recipePathPatterns];
        queryExcludePaths = [.. recipeExcludePaths];
        var queryUsesSourceDefaultExcludes = productionAndTooling
            && query.ExcludePaths.SequenceEqual(SourceScopeDefaults.ExcludePaths, StringComparer.Ordinal);
        if (!queryUsesSourceDefaultExcludes)
            AddDistinct(queryExcludePaths, query.ExcludePaths);
    }

    private static IReadOnlyList<string>? GetMcpSearchRecipeRequiredPathPatterns(
        IReadOnlyList<string> requestedPathPatterns,
        SearchAuditRecipeQuery query)
        => requestedPathPatterns.Count > 0 && query.PathPatterns.Count > 0
            ? requestedPathPatterns
            : null;

    private static void AddDistinct(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (!target.Contains(value, StringComparer.Ordinal))
                target.Add(value);
        }
    }

    private JsonArray ToSearchRecipeArray(IEnumerable<SearchAuditRecipe> recipes)
        => new(recipes.Select(recipe => ToSearchRecipeJson(recipe)).ToArray<JsonNode?>());

    private JsonObject ToSearchRecipeJson(SearchAuditRecipe recipe)
        => new()
        {
            ["name"] = recipe.Name,
            ["description"] = recipe.Description,
            ["recommended_labels"] = ToJsonArray(recipe.RecommendedLabels),
            ["default_scope"] = recipe.DefaultScope,
            ["default_path_patterns"] = ToJsonArray(recipe.DefaultPathPatterns),
            ["default_exclude_paths"] = ToJsonArray(recipe.DefaultExcludePaths),
            ["queries"] = new JsonArray(recipe.Queries.Select(query => new JsonObject
            {
                ["name"] = query.Name,
                ["query"] = query.Query,
                ["description"] = query.Description,
                ["recommended_labels"] = ToJsonArray(query.RecommendedLabels),
                ["false_positive_guidance"] = query.FalsePositiveGuidance,
                ["exact_substring"] = query.ExactSubstring
            }).ToArray<JsonNode?>())
        };

    private static void AddSearchRecipeSourceDiagnostics(JsonObject payload, IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;
        payload["recipe_source_diagnostics"] = new JsonArray(diagnostics.Select(diagnostic => JsonValue.Create(diagnostic)).ToArray<JsonNode?>());
    }

}
