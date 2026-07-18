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

        return WithDbReader(id, args, reader =>
        {
            var queryResults = new JsonArray();
            var total = 0;
            foreach (var recipeQuery in recipe.Queries)
            {
                var exact = hasExactOverride ? userExact : recipeQuery.ExactSubstring;
                ResolveMcpRecipeQueryScope(
                    recipeQuery,
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

        if (!string.Equals(auditScope, SearchAuditRecipes.DefaultAuditScope, StringComparison.Ordinal)
            && !string.Equals(auditScope, SearchAuditRecipes.AllAuditScope, StringComparison.Ordinal))
        {
            error = "'auditScope' must be either 'source' or 'all'.";
            return false;
        }

        if (string.Equals(auditScope, SearchAuditRecipes.DefaultAuditScope, StringComparison.Ordinal))
        {
            if ((pathPatterns is null || pathPatterns.Count == 0) && recipe.DefaultPathPatterns.Count > 0)
                pathPatterns = [.. recipe.DefaultPathPatterns];
            AddDistinct(excludePaths, recipe.DefaultExcludePaths);
            excludeTests = true;
        }

        return true;
    }

    private static void ResolveMcpRecipeQueryScope(
        SearchAuditRecipeQuery query,
        List<string>? recipePathPatterns,
        List<string> recipeExcludePaths,
        out List<string>? queryPathPatterns,
        out List<string> queryExcludePaths)
    {
        queryPathPatterns = query.PathPatterns.Count > 0
            ? [.. query.PathPatterns]
            : recipePathPatterns is null ? null : [.. recipePathPatterns];
        queryExcludePaths = [.. recipeExcludePaths];
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

    private JsonNode ExecuteSymbols(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (query != null && query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());

        // Validate the raw `names` node before normalization so we can distinguish "property absent"
        // from "property present but malformed/empty". ReadStringList alone silently drops both
        // non-array shapes and blank entries, which would let invalid input fall through as an
        // unfiltered full symbol dump.
        // 生の `names` ノードを先に検証し、「未指定」と「指定ありだが不正/空」を区別する。
        // ReadStringList は非配列や空文字列を暗黙に無視するため、不正入力が無条件の全件検索に落ちるのを防ぐ。
        var namesNode = args?["names"];
        var namesProvided = namesNode is not null;
        if (namesProvided && namesNode is not JsonArray)
            return CreateToolErrorResponse(id, "'names' must be an array of strings.");
        var names = ReadStringList(args, "names");
        foreach (var n in names)
        {
            if (n.Length > QueryLimits.MaxQueryLength)
                return CreateToolErrorResponse(id, $"names entry too long (max {QueryLimits.MaxQueryLength} characters)");
        }
        if (namesProvided && names.Count == 0)
            return CreateToolErrorResponse(id, "'names' is present but contains no usable entries (all were empty or whitespace).");
        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
        if (!TryResolveNameExactArgument(args, "symbols", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var visibilityFilters = ReadStringOrCommaSeparatedList(args, "visibility");
        var excludeVisibilityFilters = ReadStringOrCommaSeparatedList(args, "excludeVisibility");
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";

        // Merge query + names into a de-duplicated OR list. `|` is treated as a literal name character
        // so operator symbols (e.g. `operator |`) stay searchable; multi-name must use repeated `names[]`.
        // query と names を結合して重複排除。`|` は名前文字として扱い、`operator |` などを検索可能にする。
        var rawInputs = new List<string>();
        if (query != null)
            rawInputs.Add(query);
        rawInputs.AddRange(names);
        var hadExplicitNameInput = rawInputs.Count > 0;
        var queriesForSearch = rawInputs.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        if (hadExplicitNameInput && queriesForSearch.Count == 0)
            return CreateToolErrorResponse(id, "Symbol name list is empty after normalization. Check for empty 'names' entries or bare '|' separators.");
        if (queriesForSearch.Count > QueryCommandRunner.MaxSymbolQueryNames)
            return CreateToolErrorResponse(id, $"Too many symbol names ({queriesForSearch.Count}); maximum is {QueryCommandRunner.MaxSymbolQueryNames}. Split the request into smaller batches.");
        IReadOnlyList<string>? effectiveQueries = queriesForSearch.Count == 0 ? null : queriesForSearch;

        return WithDbReader(id, args, reader =>
        {
            JsonNode? namesEcho = effectiveQueries == null ? null : JsonSerializer.SerializeToNode(effectiveQueries, _jsonOptions);
            var hasExactPredicate = exact && effectiveQueries is { Count: > 0 };
            var exactSignal = reader.GetSymbolsExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, since);
            if (countOnly)
            {
                var countSummary = reader.CountSearchSymbolsTotal(effectiveQueries, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
                var histogramResults = countSummary.Count > 0
                    ? reader.SearchSymbols(effectiveQueries, Math.Min(countSummary.Count, MaxLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters)
                    : [];
                var payload = BuildCountOnlyPayload(countSummary.Count, countSummary.Count, truncated: false, histogramResults, result => result.Path);
                payload["query"] = query;
                payload["names"] = namesEcho;
                payload["kind"] = kind;
                payload["lang"] = lang;
                payload["path"] = PathEcho(pathPatterns);
                payload["excludeTests"] = excludeTests;
                AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
                if (hasExactPredicate)
                    AddExactGraphSignal(payload, exactSignal);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countSummary.Count, "symbol")}.", payload);
            }

            var results = reader.SearchSymbols(effectiveQueries, limit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
            var multiNameExactHint = effectiveQueries != null && effectiveQueries.Count > 1;
            var exactZeroHint = multiNameExactHint
                ? QueryCommandRunner.BuildExactZeroHint(
                    exact,
                    () => reader.AnySearchSymbols(effectiveQueries, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                    () => reader.SearchSymbols(effectiveQueries, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                    r => r.Name)
                : QueryCommandRunner.BuildExactZeroHint(
                    exact && effectiveQueries != null && effectiveQueries.Count > 0,
                    () => reader.CountSearchSymbols(effectiveQueries, QueryCommandRunner.ExactZeroHintProbeLimit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters) > 0,
                    () => reader.CountSearchSymbols(effectiveQueries, limit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                    () => reader.SearchSymbols(effectiveQueries, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                    r => r.Name);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["names"] = namesEcho,
                    ["kind"] = kind,
                    ["lang"] = lang,
                    ["path"] = PathEcho(pathPatterns),
                    ["excludeTests"] = excludeTests,
                    ["count"] = 0,
                    ["results"] = new JsonArray()
                };
                AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
                if (hasExactPredicate)
                    AddExactGraphSignal(payload, exactSignal);
                AddExactZeroHint(payload, exactZeroHint);
                AddFreshnessHint(payload, reader);
                adjustments.ApplyTo(payload);
                return CreateToolResult(id, "No symbols found.", payload);
            }

            var structured = new JsonObject
            {
                ["query"] = query,
                ["names"] = namesEcho,
                ["kind"] = kind,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["count"] = results.Count,
                ["results"] = ToJsonArray(results)
            };
            AddVisibilityFilterEcho(structured, visibilityFilters, excludeVisibilityFilters);
            if (format == "compact")
            {
                structured["results"] = BuildCompactSymbolRows(results);
                structured["format"] = "compact";
            }
            if (hasExactPredicate)
                AddExactGraphSignal(structured, exactSignal);
            var topSymbol = results[0];
            AddNextStepSuggestion(
                structured,
                "definition",
                new JsonObject { ["query"] = topSymbol.Name, ["limit"] = 5, ["exactName"] = true },
                "Use definition to confirm the declaration for the best symbol candidate; then use references, callers, or callees depending on the change.");
            adjustments.ApplyTo(structured);
            return CreateToolResult(id, ConsoleUi.FoundSummary(results.Count, "symbol"), structured);
        });
    }

    private JsonNode ExecuteDefinition(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        if (!TryReadLspCompatibleArgument(args, out var lspCompatible, out var lspCompatibleError))
            return CreateToolErrorResponse(id, lspCompatibleError!);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
        if (!TryResolveNameExactArgument(args, "definition", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var visibilityFilters = ReadStringOrCommaSeparatedList(args, "visibility");
        var excludeVisibilityFilters = ReadStringOrCommaSeparatedList(args, "excludeVisibility");
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);

        return WithDbReader(id, args, reader =>
        {
            var results = reader.GetDefinitions(query, FetchLimitForEnvelope(limit), kind, lang, includeBody, pathPatterns, excludePaths, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
            var truncated = TrimToRequestedLimit(results, limit);
            if (format == "count")
            {
                var total = truncated
                    ? reader.CountDefinitionsTotal(query, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters).Count
                    : results.Count;
                var countPayload = BuildCountOnlyPayload(total, total, truncated: false, results, result => result.Path);
                countPayload["query"] = query;
                countPayload["kind"] = kind;
                countPayload["lang"] = lang;
                countPayload["path"] = PathEcho(pathPatterns);
                countPayload["excludeTests"] = excludeTests;
                AddVisibilityFilterEcho(countPayload, visibilityFilters, excludeVisibilityFilters);
                adjustments.ApplyTo(countPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(total, "definition")}.", countPayload);
            }
            if (lspCompatible)
                QueryCommandRunner.AttachLspLocations(results);
            ApplyExcerptRecoveryDbPath(results);
            var exactSignal = reader.GetDefinitionExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, since);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact,
                () => reader.CountSearchSymbols(query, QueryCommandRunner.ExactZeroHintProbeLimit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters) > 0,
                () => reader.CountSearchSymbols(query, limit, kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                () => reader.SearchSymbols(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), kind, lang, pathPatterns, excludePaths, excludeTests, since, exact: false, visibilityFilters: visibilityFilters, excludeVisibilityFilters: excludeVisibilityFilters),
                r => r.Name);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["includeBody"] = includeBody,
                ["lspCompatible"] = lspCompatible,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["results"] = ToJsonArray(results)
            };
            AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
            AddResultEnvelope(payload, results.Count, truncated ? null : results.Count, truncated);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.StartLine);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "definition", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            else
            {
                AddNextStepSuggestion(
                    payload,
                    "references",
                    new JsonObject { ["query"] = results[0].Name, ["limit"] = 5, ["exactName"] = true },
                    "Use references to inspect usage sites before changing this definition; then use excerpt for the relevant definition or reference ranges.");
            }
            adjustments.ApplyTo(payload);
            return CreateToolResult(id,
                ConsoleUi.FoundSummary(results.Count, "definition"),
                payload);
        });
    }

    private void ApplyExcerptRecoveryDbPath(IEnumerable<DefinitionResult> results)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, _dbPath);
    }

    private void ApplyExcerptRecoveryDbPath(IEnumerable<ReferenceResult> results)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, _dbPath);
    }

    private void ApplyExcerptRecoveryDbPath(IEnumerable<CallerResult> results)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, _dbPath);
    }

    private void ApplyExcerptRecoveryDbPath(IEnumerable<CalleeResult> results)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, _dbPath);
    }

    private JsonNode ExecuteReferences(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        if (!TryReadLspCompatibleArgument(args, out var lspCompatible, out var lspCompatibleError))
            return CreateToolErrorResponse(id, lspCompatibleError!);
        var offset = ReadOffset(args, adjustments);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        if (!TryResolveNameExactArgument(args, "references", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountSearchReferencesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact).Count;
                var histogramResults = countOnlyTotal > 0
                    ? reader.SearchReferences(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                adjustments.ApplyTo(countOnlyPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "reference")}.", countOnlyPayload);
            }

            var results = reader.SearchReferences(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountSearchReferencesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact).Count
                : results.Count;
            if (lspCompatible)
                QueryCommandRunner.AttachLspLocations(results);
            var graphSupport = ResolveGraphSupport(reader, exact, query, lang, pathPatterns, excludePaths, excludeTests);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                results.Select(result => result.Lang),
                lang,
                graphSupport.GraphLanguage);
            var exactSignal = reader.GetReferencesExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountSearchReferences(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false) > 0,
                () => reader.CountSearchReferences(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false),
                () => reader.SearchReferences(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false),
                r => r.SymbolName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["lspCompatible"] = lspCompatible,
                ["maxLineWidth"] = maxLineWidth,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["graph_language"] = graphSupport.GraphLanguage,
                ["graph_supported"] = graphSupport.GraphSupported,
                ["graph_support_reason"] = graphSupport.GraphSupportReason,
                ["results"] = ToJsonArray(results)
            };
            AddPaginatedResultEnvelope(payload, results.Count, total, truncated, offset);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.Line, result => result.Column);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "references", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            else
            {
                var topReference = results[0];
                AddNextStepSuggestion(
                    payload,
                    "excerpt",
                    BuildExcerptArgs(topReference.Path, topReference.Line, topReference.Line),
                    "Use excerpt on representative usage sites before editing; use callers or callees when you need call graph impact.");
            }
            adjustments.ApplyTo(payload);
            return CreateToolResult(id,
                BuildGraphSummary("reference", "references", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteCallers(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        if (IsNonCallGraphReferenceKind(kind))
            return CreateToolErrorResponse(id, BuildNonCallGraphKindRejectionMessage("callers", kind!));
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var offset = ReadOffset(args, adjustments);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "callers", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        if (!TryReadReferenceRankMode(args, out var rankMode, out var rankModeError))
            return CreateToolErrorResponse(id, rankModeError!);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        var rawKinds = args?["rawKinds"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountCallersTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count;
                var histogramResults = countOnlyTotal > 0
                    ? reader.GetCallers(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["rawKinds"] = rawKinds;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                adjustments.ApplyTo(countOnlyPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "caller")}.", countOnlyPayload);
            }

            var results = reader.GetCallers(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountCallersTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count
                : results.Count;
            var graphSupport = ResolveGraphSupport(reader, exact, query, lang, pathPatterns, excludePaths, excludeTests);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                results.Select(result => result.Lang),
                lang,
                graphSupport.GraphLanguage);
            var exactSignal = reader.GetCallersExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallers(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds) > 0,
                () => reader.CountCallers(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds),
                () => reader.GetCallers(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds, rankMode: rankMode),
                r => r.CalleeName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["rawKinds"] = rawKinds,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["rankBy"] = QueryCommandRunner.FormatReferenceRankMode(rankMode),
                ["graph_language"] = graphSupport.GraphLanguage,
                ["graph_supported"] = graphSupport.GraphSupported,
                ["graph_support_reason"] = graphSupport.GraphSupportReason,
                ["results"] = ToJsonArray(results)
            };
            AddPaginatedResultEnvelope(payload, results.Count, total, truncated, offset);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.FirstLine);
            payload["aggregate_truncated"] = results.Any(result => result.AggregateTruncated);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "callers", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            else
            {
                var topCaller = results[0];
                AddNextStepSuggestion(
                    payload,
                    "excerpt",
                    BuildExcerptArgs(topCaller.Path, topCaller.FirstLine, topCaller.FirstLine),
                    "Use excerpt on a caller row to understand the concrete call site before widening impact analysis or editing.");
            }
            adjustments.ApplyTo(payload);
            return CreateToolResult(id,
                BuildGraphSummary("caller", "callers", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteCallees(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        if (IsNonCallGraphReferenceKind(kind))
            return CreateToolErrorResponse(id, BuildNonCallGraphKindRejectionMessage("callees", kind!));
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var offset = ReadOffset(args, adjustments);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "callees", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        if (!TryReadReferenceRankMode(args, out var rankMode, out var rankModeError))
            return CreateToolErrorResponse(id, rankModeError!);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        var rawKinds = args?["rawKinds"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountCalleesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count;
                var histogramResults = countOnlyTotal > 0
                    ? reader.GetCallees(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["rawKinds"] = rawKinds;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                adjustments.ApplyTo(countOnlyPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "callee")}.", countOnlyPayload);
            }

            var results = reader.GetCallees(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountCalleesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count
                : results.Count;
            var graphSupport = ResolveGraphSupport(reader, exact, query, lang, pathPatterns, excludePaths, excludeTests);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                results.Select(result => result.Lang),
                lang,
                graphSupport.GraphLanguage);
            var exactSignal = reader.GetCalleesExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallees(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds) > 0,
                () => reader.CountCallees(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds),
                () => reader.GetCallees(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds, rankMode: rankMode),
                r => r.CallerName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["rawKinds"] = rawKinds,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["rankBy"] = QueryCommandRunner.FormatReferenceRankMode(rankMode),
                ["graph_language"] = graphSupport.GraphLanguage,
                ["graph_supported"] = graphSupport.GraphSupported,
                ["graph_support_reason"] = graphSupport.GraphSupportReason,
                ["results"] = ToJsonArray(results)
            };
            AddPaginatedResultEnvelope(payload, results.Count, total, truncated, offset);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.FirstLine);
            payload["aggregate_truncated"] = results.Any(result => result.AggregateTruncated);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "callees", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            else
            {
                var topCallee = results[0];
                AddNextStepSuggestion(
                    payload,
                    "excerpt",
                    BuildExcerptArgs(topCallee.Path, topCallee.FirstLine, topCallee.FirstLine),
                    "Use excerpt on a callee row to inspect the concrete dependency before changing the caller or callee.");
            }
            adjustments.ApplyTo(payload);
            return CreateToolResult(id,
                BuildGraphSummary("callee", "callees", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteFiles(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (query != null && query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        var adjustments = new ArgumentAdjustmentCollector();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
        var orderBySize = args?["orderBySize"]?.GetValue<bool>() ?? false;
        var rawBytes = args?["rawBytes"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            var results = reader.ListFiles(query, limit, lang, pathPatterns, excludePaths, excludeTests, since, orderBySize || rawBytes);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["lang"] = lang,
                    ["path"] = PathEcho(pathPatterns),
                    ["excludeTests"] = excludeTests,
                    ["orderBySize"] = orderBySize,
                    ["rawBytes"] = rawBytes,
                    ["count"] = 0,
                    ["results"] = new JsonArray()
                };
                if (rawBytes)
                {
                    payload["raw_bytes_payload_supported"] = false;
                    payload["raw_bytes_note"] = "MCP returns indexed file size metadata; raw file bytes are not returned.";
                }
                AddFreshnessHint(payload, reader);
                adjustments.ApplyTo(payload);
                return CreateToolResult(id, "No files found.", payload);
            }

            var structured = new JsonObject
            {
                ["query"] = query,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["orderBySize"] = orderBySize,
                ["rawBytes"] = rawBytes,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (rawBytes)
            {
                structured["raw_bytes_payload_supported"] = false;
                structured["raw_bytes_note"] = "MCP returns indexed file size metadata; raw file bytes are not returned.";
            }
            adjustments.ApplyTo(structured);
            return CreateToolResult(id, ConsoleUi.FoundSummary(results.Count, "file"), structured);
        });
    }

    private JsonNode ExecuteMap(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultMapLimit, adjustments);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sections = ReadStringList(args, "sections").Select(section => section.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var depth = ReadMapDepth(args, adjustments);
        var minEntrypointConfidence = args?["minEntrypointConfidence"]?.GetValue<double>() ?? 0;
        if (minEntrypointConfidence is < 0 or > 1)
            return CreateToolErrorResponse(id, "minEntrypointConfidence must be between 0.0 and 1.0");

        return WithDbReader(id, args, reader =>
        {
            var map = reader.GetRepoMap(
                limit,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests,
                minEntrypointConfidence,
                moduleDepth: depth);
            WorkspaceMetadataEnricher.Enrich(map, _dbPath, _dbPathExplicit);
            var structured = JsonSerializer.SerializeToNode(map, _jsonOptions)!.AsObject();
            if (depth is >= 0)
                structured["depth"] = depth.Value;
            if (sections.Count > 0)
                ApplyMapSectionFilter(structured, sections);
            structured["limit"] = limit;
            structured["lang"] = lang;
            structured["path"] = PathEcho(pathPatterns);
            structured["excludeTests"] = excludeTests;
            structured["minEntrypointConfidence"] = minEntrypointConfidence;
            var hasFilter = (pathPatterns is { Count: > 0 }) || excludePaths.Count > 0 || excludeTests || lang != null;
            if (map.FileCount == 0 && hasFilter)
                AddFreshnessHint(structured, reader);
            adjustments.ApplyTo(structured);
            var summary = map.FileCount > 0
                ? "Repo map returned."
                : hasFilter ? "No files found matching the given filters." : "Repo map returned.";
            return CreateToolResult(id, summary, structured);
        });
    }

    private static void ApplyMapSectionFilter(JsonObject structured, IReadOnlySet<string> sections)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            "api_version", "fileCount", "totalLines", "totalSymbols", "totalReferences",
            "indexedAt", "latestModified", "workspaceIndexedAt", "workspaceLatestModified",
            "projectRoot", "gitHead", "gitIsDirty", "indexed_head_commit", "indexed_head_sha",
            "indexed_head_branch", "indexed_head_timestamp", "commits_ahead_of_indexed_head",
            "worktree_head_changed", "head_freshness",
            "graphTableAvailable", "limit", "lang", "path", "excludeTests", "depth", "minEntrypointConfidence",
        };
        foreach (var section in sections)
            AddMapSectionStructuredProperties(keep, section);
        foreach (var key in structured.Select(property => property.Key).Where(key => !keep.Contains(key)).ToList())
            structured.Remove(key);
        structured["sections"] = new JsonArray(sections.Select(section => JsonValue.Create(section)).ToArray<JsonNode?>());
        structured["sectionProperties"] = BuildMapSectionStructuredProperties(sections);
    }

    private static readonly IReadOnlyDictionary<string, string[]> MapSectionStructuredProperties = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["languages"] = ["languages"],
        ["tree"] = ["modules"],
        ["modules"] = ["modules"],
        ["hotspots"] = ["topFiles", "symbolRichFiles", "referenceRichFiles", "entrypoints"],
        ["metrics"] = ["largestFiles"],
    };

    private static void AddMapSectionStructuredProperties(HashSet<string> keep, string section)
    {
        if (!MapSectionStructuredProperties.TryGetValue(section, out var properties))
            return;

        foreach (var property in properties)
            keep.Add(property);
    }

    private static JsonObject BuildMapSectionStructuredProperties(IReadOnlySet<string> sections)
    {
        var payload = new JsonObject();
        foreach (var section in sections)
        {
            if (!MapSectionStructuredProperties.TryGetValue(section, out var properties))
                continue;

            payload[section] = new JsonArray(properties.Select(property => JsonValue.Create(property)).ToArray<JsonNode?>());
        }

        return payload;
    }

    private JsonNode ExecuteAnalyzeSymbol(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultMapLimit, adjustments);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "analyze_symbol", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";

        return WithDbReader(id, args, reader =>
        {
            var analysis = reader.AnalyzeSymbol(query, limit, lang, includeBody, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignal(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                DbReader.IsSqlLanguage(lang)
                    || DbReader.IsSqlLanguage(analysis.GraphLanguage)
                    || DbReader.IsSqlLanguage(analysis.File?.Lang)
                    || DbReader.ContainsSqlLanguage(analysis.Definitions.Select(definition => definition.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.References.Select(reference => reference.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callers.Select(caller => caller.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callees.Select(callee => callee.Lang)));
            analysis.SqlGraphContractReady = sqlGraphSignal.Relevant ? sqlGraphSignal.Ready : null;
            analysis.SqlGraphContractDegradedReason = sqlGraphSignal.Relevant ? sqlGraphSignal.DegradedReason : null;
            WorkspaceMetadataEnricher.Enrich(analysis, _dbPath, _dbPathExplicit);
            ApplyExcerptRecoveryDbPath(analysis.Definitions);
            ApplyExcerptRecoveryDbPath(analysis.References);
            ApplyExcerptRecoveryDbPath(analysis.Callers);
            ApplyExcerptRecoveryDbPath(analysis.Callees);
            var pathEcho = PathEcho(pathPatterns);
            var structured = countOnly
                ? BuildAnalyzeSymbolCountPayload(analysis, lang, pathEcho, excludeTests, maxLineWidth)
                : format == "compact"
                    ? BuildAnalyzeSymbolCompactPayload(analysis, lang, pathEcho, excludeTests, maxLineWidth)
                    : ToAnalyzeSymbolJsonObject(analysis);
            AddSqlGraphContractSignal(structured, sqlGraphSignal);
            structured.Remove("exactZeroHint");
            AddExactZeroHint(structured, analysis.ExactZeroHint);
            structured["maxLineWidth"] = maxLineWidth;
            structured["lang"] = lang;
            structured["path"] = pathEcho;
            structured["excludeTests"] = excludeTests;
            adjustments.ApplyTo(structured);
            return CreateToolResult(id, BuildAnalyzeSymbolSummary(analysis), structured);
        });
    }

    private static string BuildAnalyzeSymbolSummary(SymbolAnalysisResult analysis)
    {
        if (analysis.ExactZeroHint != null)
        {
            var relaxedCount = analysis.ExactZeroHint.RelaxedCount ?? analysis.ExactZeroHint.SampleNames.Count;
            return $"Symbol analysis returned. Substring would return {ConsoleUi.Counted(relaxedCount, "similarly named symbol")}.";
        }

        return "Symbol analysis returned.";
    }

    private static void AddExactGraphSignal(JsonObject payload, ExactQuerySignal signal)
    {
        payload["exact_index_available"] = signal.ExactIndexAvailable;
        if (signal.DegradedReason != null)
            payload["degraded_reason"] = signal.DegradedReason;
        // MCP uses snake_case response keys consistently; do not add camelCase aliases here.
    }

    private static void AddSqlGraphContractSignal(JsonObject payload, SqlGraphContractSignal signal)
    {
        if (!signal.Relevant)
            return;

        payload["sql_graph_contract_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
            {
                payload["sql_graph_contract_degraded_reason"] = signal.DegradedReason;
            }
        }
    }

    private static bool IsBareVerbatimQueryToken(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed.All(ch => ch == '@');
    }

    private static Dictionary<string, string?> GetHotspotFamilyMetaSnapshot(DbContext db, Func<string, string> keyFactory)
    {
        var languages = FileIndexer.GetHotspotFamilyMarkerLanguages();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var keys = new string[languages.Count];
        for (var i = 0; i < languages.Count; i++)
        {
            var lang = languages[i];
            keys[i] = keyFactory(lang);
            values[lang] = null;
        }

        var metaValues = db.GetMetaStrings(keys);
        for (var i = 0; i < languages.Count; i++)
            values[languages[i]] = metaValues.TryGetValue(keys[i], out var value) ? value : null;

        return values;
    }

    private static Dictionary<string, FileIndexer.ProjectMarkerFingerprintResult> GetHotspotFamilyMarkerFingerprints(
        FileIndexer indexer,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, FileIndexer.ProjectMarkerFingerprintResult>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            values[lang] = indexer.GetProjectMarkerFingerprintResult(lang, cancellationToken);
        return values;
    }

    private static void RestampHotspotFamilyTrust(
        DbWriter writer,
        IReadOnlySet<string>? reusedLanguages,
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            if (!currentFingerprints.TryGetValue(lang, out var currentFingerprint))
                continue;

            if (!currentFingerprint.IsComplete)
            {
                writer.MarkHotspotFamilyMarkerFingerprintIncomplete(lang, currentFingerprint.Fingerprint);
                continue;
            }

            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            if (reusedLanguages?.Contains(lang) != true || (priorVersion == currentVersion && priorFingerprint == currentFingerprint.Fingerprint))
                writer.MarkHotspotFamilyReady(lang, currentFingerprint.Fingerprint);
        }
    }

    private static Dictionary<string, bool> GetHotspotFamilyTrustMatchesCurrent(
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            currentFingerprints.TryGetValue(lang, out var currentFingerprint);
            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            values[lang] = currentFingerprint.IsComplete
                && priorVersion == currentVersion
                && priorFingerprint == currentFingerprint.Fingerprint;
        }

        return values;
    }

    private static bool AllowReuseWithCurrentHotspotFamilyTrust(
        string? lang,
        IReadOnlyDictionary<string, bool> hotspotFamilyTrustMatchesCurrent)
    {
        if (!FileIndexer.SupportsHotspotFamilyMarkerLanguage(lang))
            return true;

        return lang != null
            && hotspotFamilyTrustMatchesCurrent.TryGetValue(lang, out var matchesCurrent)
            && matchesCurrent;
    }

    private static void AddHotspotFamilySignal(JsonObject payload, HotspotFamilySignal signal)
    {
        payload["hotspot_family_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
            {
                payload["hotspot_family_degraded_reason"] = signal.DegradedReason;
            }
        }
    }

    private JsonNode ExecuteStatus(JsonNode? id, JsonNode? args)
    {
        var checkWorkspace = args?["check"]?.GetValue<bool>() ?? false;
        var staleAfterSeconds = ReadOptionalIntArgument(args, "staleAfterSeconds") ?? (int)TimeSpan.FromDays(1).TotalSeconds;
        if (staleAfterSeconds <= 0)
            return CreateToolErrorResponse(id, "staleAfterSeconds must be greater than or equal to 1");
        var explain = args?["explain"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (explain is not (null or "freshness" or "readiness" or "all"))
            return CreateToolErrorResponse(id, "explain must be one of freshness, readiness, all");
        var format = ReadResponseFormat(args);
        if (format is not ("full" or "compact"))
            return CreateToolErrorResponse(id, "format must be one of full, compact");
        if (!TryReadStatusScopes(args, out var statusScopes, out var scopeError))
            return CreateToolErrorResponse(id, scopeError!);
        var includeConfig = args?["config"]?.GetValue<bool>() ?? false;
        var includeLogPath = args?["logPath"]?.GetValue<bool>() ?? false;
        var runUpdateCheck = args?["updateCheck"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            var requestToken = _currentRequestToken.Value;
            var status = reader.GetStatus();
            QueryCommandRunner.ApplyStatusSymbolKindLimits(status, reader.GetSymbolKindCounts());
            WorkspaceMetadataEnricher.Enrich(status, _dbPath, _dbPathExplicit, requestToken);
            status.DbFileMode = DbContext.GetUnixFileModeString(
                _dbPath,
                status.DatabasePermissionPolicy,
                out var databasePermissionDiagnostic);
            if (databasePermissionDiagnostic != null)
            {
                status.DatabasePermissionDiagnostics ??= [];
                status.DatabasePermissionDiagnostics.Add(databasePermissionDiagnostic);
            }
            var macProfile = MacProfileDetector.DetectCurrentWithDiagnostics();
            status.MacProfile = macProfile.Profile;
            if (macProfile.Diagnostics.Count > 0)
                status.MacProfileDiagnostics = macProfile.Diagnostics.ToList();
            if (checkWorkspace)
            {
                status.WorkspaceCheck = IndexFreshnessChecker.Check(reader, status.ProjectRoot, requestToken);
                status.IndexMatchesWorkspace = status.WorkspaceCheck.Checked
                    ? status.WorkspaceCheck.MatchesWorkspace
                    : null;
                status.StaleAfterSeconds = staleAfterSeconds;
                if (status.IndexedAt.HasValue)
                    status.IndexAgeSeconds = Math.Max(0, (long)Math.Round((GetUtcNow() - status.IndexedAt.Value).TotalSeconds, MidpointRounding.AwayFromZero));
            }
            ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(status.ProjectRoot);
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages().OrderBy(l => l).ToList();
            status.Extractors = ExtractorPluginRegistry.GetStatusSnapshot();
            status.GitExecutable = GitHelper.GetGitExecutableStatus();
            var postExtractionHookSnapshot = PostExtractionHookRunner.DiscoverDefaultMetadata();
            var postExtractionHooks = postExtractionHookSnapshot.Hooks;
            if (postExtractionHookSnapshot.Diagnostics.Count > 0)
                status.HookDiagnostics = postExtractionHookSnapshot.Diagnostics.ToList();
            var trustOverrides = ExtractorPluginRegistry.GetAcceptedTrustOverrides(status.ProjectRoot)
                .Concat(postExtractionHookSnapshot.TrustOverrides)
                .Concat(GitHelper.GetAcceptedTrustOverrides())
                .ToList();
            if (trustOverrides.Count > 0)
                status.TrustOverrides = trustOverrides;
            if (postExtractionHooks.Count > 0)
            {
                status.Hooks = postExtractionHooks
                    .Select(hook => new PostExtractionHookStatus
                    {
                        Name = hook.Name,
                        AssemblyPath = hook.AssemblyPath,
                        TypeName = hook.TypeName,
                        CallbackBudgetMs = (long)Math.Round(postExtractionHookSnapshot.CallbackBudget.TotalMilliseconds, MidpointRounding.AwayFromZero),
                        LoadContextLifecycle = PostExtractionHookRunner.HookLoadContextLifecycle,
                    })
                    .ToList();
            }
            status.Version = _version;
            requestToken.ThrowIfCancellationRequested();
            status.UpdateCheck = runUpdateCheck
                ? (StatusUpdateCheckForTesting ?? UpdateChecker.Check)(_version, requestToken)
                : null;
            if (!status.FoldReady)
            {
                status.DegradedReason = DegradationReasonCodes.BuildFoldNotReadyExplanation(status.FoldReadyReason);
                status.RecommendedAction = BuildFoldBackfillCommand(_dbPath, _dbPathExplicit);
                status.AlternativeAction = BuildFoldRebuildRepairCommand(status.ProjectRoot, _dbPath, _dbPathExplicit);
            }
            status.Summary = QueryCommandRunner.BuildStatusSummary(status);
            var checkFailures = checkWorkspace
                ? BuildMcpStatusCheckFailures(status, statusScopes)
                : [];
            if (checkWorkspace)
                status.FailedChecks = checkFailures.Select(failure => failure.Name).ToList();

            var structured = JsonSerializer.SerializeToNode(status, _jsonOptions)!.AsObject();
            structured["project_root"] = status.ProjectRoot;
            structured["git_head"] = status.GitHead;
            structured["git_is_dirty"] = status.GitIsDirty;
            structured.Remove("hotspotFamilyReady");
            structured.Remove("hotspotFamilyDegradedReason");
            structured["sql_graph_contract_ready"] = status.SqlGraphContractReady;
            if (status.SqlGraphContractDegradedReason != null)
                structured["sql_graph_contract_degraded_reason"] = status.SqlGraphContractDegradedReason;
            structured["mcp_session"] = BuildMcpSessionStatus();
            var rateLimitDiagnostics = RateLimiter.SnapshotDiagnostics();
            structured["mcp"] = new JsonObject
            {
                ["limits"] = new JsonObject
                {
                    ["max_request_characters"] = MaxLineCharacterCount,
                    ["max_request_bytes"] = MaxLineByteLength,
                    ["max_response_bytes"] = GetMaxResponseBytes(),
                    ["max_configured_response_bytes"] = MaxConfiguredResponseBytes,
                    ["batch_response_bytes"] = GetBatchQueryResponseByteLimit(),
                    ["max_batch_response_bytes"] = MaxBatchQueryResponseByteLimit,
                    ["batch_query_response_bytes"] = GetBatchQueryResponseByteLimit(),
                    ["batch_query_max_response_bytes"] = MaxBatchQueryResponseByteLimit,
                    ["batch_query_max_queries"] = MaxBatchQuerySize,
                    ["max_pagination_offset"] = MaxMcpPaginationOffset,
                    ["max_json_depth"] = MaxJsonDepth,
                    ["max_batch_requests"] = MaxBatchRequestCount,
                    ["json_rpc_batch_max_requests"] = MaxBatchRequestCount,
                    ["keep_alive_min_interval_s"] = MinKeepAliveIntervalSeconds,
                    ["keep_alive_max_interval_s"] = MaxKeepAliveIntervalSeconds,
                    ["rate_limit_max_rps"] = RateLimiterOptions.MaxRefillTokensPerSecond,
                    ["rate_limit_max_burst"] = RateLimiterOptions.MaxBurstCapacity,
                    ["rate_limit_max_buckets"] = RateLimiterOptions.DefaultMaxBucketCount,
                },
                ["rate_limit"] = new JsonObject
                {
                    ["enabled"] = RateLimiter.Options.IsEnabled,
                    ["rps"] = RateLimiter.Options.RefillTokensPerSecond,
                    ["burst"] = RateLimiter.Options.BurstCapacity,
                    ["bucket_count"] = rateLimitDiagnostics.BucketCount,
                    ["bucket_limit"] = rateLimitDiagnostics.MaxBucketCount,
                    ["bucket_limit_rejection_count"] = rateLimitDiagnostics.BucketLimitRejectionCount,
                    ["bucket_idle_ttl_seconds"] = rateLimitDiagnostics.BucketIdleTtlSeconds,
                    ["next_prune_in_ms"] = rateLimitDiagnostics.NextPruneInMs,
                    ["last_prune_age_ms"] = rateLimitDiagnostics.LastPruneAgeMs.HasValue ? JsonValue.Create(rateLimitDiagnostics.LastPruneAgeMs.Value) : null,
                    ["last_pruned_bucket_count"] = rateLimitDiagnostics.LastPrunedBucketCount,
                },
                ["request_timeouts"] = BuildRequestTimeoutDiagnosticsStatus(),
            };
            var effectiveConfig = includeConfig
                ? BuildMcpStatusEffectiveConfig(status, staleAfterSeconds, checkWorkspace, runUpdateCheck)
                : null;
            var logPath = includeLogPath ? GlobalToolLog.ResolveLogDirectoryForStatus() : null;
            var explainPayload = explain is null
                ? null
                : BuildMcpStatusExplain(status, checkFailures, explain);
            if (effectiveConfig is not null)
                structured["effective_config"] = effectiveConfig.DeepClone();
            if (logPath is not null)
                structured["log_path"] = logPath;
            if (explainPayload is not null)
                structured["explain"] = explainPayload.DeepClone();
            if (format == "compact")
            {
                structured = BuildMcpCompactStatusPayload(status, checkFailures);
                if (effectiveConfig is not null)
                    structured["effective_config"] = effectiveConfig;
                if (logPath is not null)
                    structured["log_path"] = logPath;
                if (explainPayload is not null)
                    structured["explain"] = explainPayload;
            }
            return CreateToolResult(id, "Database stats returned.", structured);
        });
    }

    private sealed record McpStatusCheckFailure(string Name, bool IsStale, string Diagnostic);

    private static bool TryReadStatusScopes(JsonNode? args, out HashSet<string>? scopes, out string? error)
    {
        scopes = null;
        error = null;
        if (args?["scopes"] is null)
            return true;

        var values = ReadStringOrArrayList(args, "scopes")
            .Select(scope => scope.Trim().ToLowerInvariant())
            .ToList();
        if (args["scopes"] is JsonArray array && values.Count != array.Count)
        {
            error = "scopes entries must be non-empty strings.";
            return false;
        }
        if (values.Count == 0)
        {
            error = "scopes cannot be empty or whitespace-only.";
            return false;
        }

        scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!IsKnownMcpStatusScope(value))
            {
                error = $"Invalid status scope '{value}'. Use one of: workspace, graph, issues, sql, hotspot, csharp, fold, newer.";
                return false;
            }
            scopes.Add(value);
        }
        return true;
    }

    private static bool IsKnownMcpStatusScope(string scope) =>
        scope is "workspace" or "graph" or "issues" or "sql" or "hotspot" or "csharp" or "fold" or "newer";

    private static IReadOnlyList<McpStatusCheckFailure> BuildMcpStatusCheckFailures(StatusResult status, IReadOnlySet<string>? scopes)
    {
        var failures = new List<McpStatusCheckFailure>();
        var checkAll = scopes is not { Count: > 0 };
        bool Includes(string scope) => checkAll || scopes!.Contains(scope);

        if (Includes("workspace"))
        {
            if (status.WorkspaceCheck?.Checked != true)
            {
                failures.Add(new McpStatusCheckFailure("workspace_unavailable", true, "[stale] workspace_check unavailable"));
            }
            else if (!status.WorkspaceCheck.MatchesWorkspace)
            {
                var check = status.WorkspaceCheck;
                failures.Add(new McpStatusCheckFailure(
                    "workspace_stale",
                    true,
                    $"[stale] workspace_check reason={check.Reason} changed={check.ChangedFileCount} missing={check.MissingFileCount} unindexed={check.UnindexedFileCount}"));
            }
        }

        if (Includes("graph") && !status.GraphTableAvailable)
            failures.Add(new McpStatusCheckFailure("graph_table_available", false, "[degraded] graph_table_available=false"));
        if (Includes("issues") && !status.IssuesTableAvailable)
            failures.Add(new McpStatusCheckFailure("issues_table_available", false, "[degraded] issues_table_available=false"));
        if (Includes("issues") && status.IssuesTableAvailable && !status.FileIssuesDataCurrent)
            failures.Add(new McpStatusCheckFailure("file_issues_data_current", false, "[degraded] file_issues_data_current=false"));
        if (Includes("workspace") && status.MigrationInProgress)
            failures.Add(new McpStatusCheckFailure("migration_in_progress", false, "[degraded] migration_in_progress=true"));
        if (Includes("sql") && !status.SqlGraphContractReady)
            failures.Add(new McpStatusCheckFailure("sql_graph_contract_ready", false, $"[degraded] sql_graph_contract_ready=false reason={status.SqlGraphContractDegradedReason ?? "unknown"}"));
        if (Includes("hotspot") && !status.HotspotFamilyReady)
            failures.Add(new McpStatusCheckFailure("hotspot_family_ready", false, $"[degraded] hotspot_family_ready=false reason={status.HotspotFamilyDegradedReason ?? "unknown"}"));
        if (Includes("csharp") && !status.CSharpSymbolNameReady)
            failures.Add(new McpStatusCheckFailure("csharp_symbol_name_ready", false, "[degraded] csharp_symbol_name_ready=false"));
        if (Includes("csharp") && !status.CSharpMetadataTargetReady)
            failures.Add(new McpStatusCheckFailure("csharp_metadata_target_ready", false, $"[degraded] csharp_metadata_target_ready=false reason={status.CSharpMetadataTargetDegradedReason ?? "unknown"}"));
        if (Includes("fold") && !status.FoldReady)
            failures.Add(new McpStatusCheckFailure("fold_ready", false, $"[degraded] fold_ready=false reason={status.FoldReadyReason ?? "unknown"}"));
        if (Includes("newer") && status.IndexNewerThanReader)
            failures.Add(new McpStatusCheckFailure("index_newer_than_reader", false, $"[degraded] index_newer_than_reader=true reason={status.IndexNewerThanReaderReason ?? "unknown"}"));

        return failures;
    }

    private JsonObject BuildMcpStatusEffectiveConfig(StatusResult status, int staleAfterSeconds, bool checkWorkspace, bool runUpdateCheck) => new()
    {
        ["db_path"] = _dbPath,
        ["db_explicit"] = _dbPathExplicit,
        ["project_root"] = status.ProjectRoot,
        ["data_dir"] = status.DataDir,
        ["data_dir_source"] = status.DataDirSource,
        ["global_tool_log_dir"] = GlobalToolLog.ResolveLogDirectoryForStatus(),
        ["stale_after_seconds"] = staleAfterSeconds,
        ["check"] = checkWorkspace,
        ["update_check_requested"] = runUpdateCheck,
        ["version"] = status.Version,
    };

    private JsonObject BuildMcpStatusExplain(StatusResult status, IReadOnlyList<McpStatusCheckFailure> failures, string explain)
    {
        var payload = new JsonObject();
        if (explain is "freshness" or "all")
        {
            payload["freshness"] = new JsonObject
            {
                ["index_matches_workspace"] = status.IndexMatchesWorkspace.HasValue ? JsonValue.Create(status.IndexMatchesWorkspace.Value) : null,
                ["stale_after_seconds"] = status.StaleAfterSeconds.HasValue ? JsonValue.Create(status.StaleAfterSeconds.Value) : null,
                ["index_age_seconds"] = status.IndexAgeSeconds.HasValue ? JsonValue.Create(status.IndexAgeSeconds.Value) : null,
                ["workspace_check"] = status.WorkspaceCheck is null ? null : JsonSerializer.SerializeToNode(status.WorkspaceCheck, _jsonOptions),
            };
        }
        if (explain is "readiness" or "all")
        {
            payload["readiness"] = BuildMcpStatusReadiness(status);
            payload["failed_check_details"] = BuildMcpStatusFailureArray(failures);
        }
        return payload;
    }

    private static JsonObject BuildMcpStatusReadiness(StatusResult status) => new()
    {
        ["graph_table_available"] = status.GraphTableAvailable,
        ["issues_table_available"] = status.IssuesTableAvailable,
        ["file_issues_data_current"] = status.FileIssuesDataCurrent,
        ["sql_graph_contract_ready"] = status.SqlGraphContractReady,
        ["hotspot_family_ready"] = status.HotspotFamilyReady,
        ["csharp_symbol_name_ready"] = status.CSharpSymbolNameReady,
        ["csharp_metadata_target_ready"] = status.CSharpMetadataTargetReady,
        ["fold_ready"] = status.FoldReady,
        ["index_newer_than_reader"] = status.IndexNewerThanReader,
        ["migration_in_progress"] = status.MigrationInProgress,
    };

    private static JsonArray BuildMcpStatusFailureArray(IReadOnlyList<McpStatusCheckFailure> failures)
    {
        var array = new JsonArray();
        foreach (var failure in failures)
        {
            array.Add(new JsonObject
            {
                ["name"] = failure.Name,
                ["is_stale"] = failure.IsStale,
                ["diagnostic"] = failure.Diagnostic,
            });
        }
        return array;
    }

    private static JsonObject BuildMcpCompactStatusPayload(StatusResult status, IReadOnlyList<McpStatusCheckFailure> failures)
    {
        var payload = new JsonObject
        {
            ["format"] = "compact",
            ["summary"] = status.Summary,
            ["version"] = status.Version,
            ["project_root"] = status.ProjectRoot,
            ["files"] = status.Files,
            ["chunks"] = status.Chunks,
            ["symbols"] = status.Symbols,
            ["references"] = status.References,
            ["symbol_kinds"] = JsonSerializer.SerializeToNode(status.SymbolKinds),
            ["symbol_kind_limit"] = status.SymbolKindLimit,
            ["symbol_kind_name_limit"] = status.SymbolKindNameLimit,
            ["symbol_kind_total_count"] = status.SymbolKindTotalCount,
            ["symbol_kind_omitted_count"] = status.SymbolKindOmittedCount,
            ["symbol_kind_names_truncated"] = status.SymbolKindNamesTruncated,
            ["language_count"] = status.Languages.Count,
            ["top_languages"] = new JsonArray(status.Languages
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(kv => new JsonObject { ["lang"] = kv.Key, ["files"] = kv.Value })
                .ToArray<JsonNode?>()),
            ["git_head"] = status.GitHead,
            ["git_is_dirty"] = status.GitIsDirty.HasValue ? JsonValue.Create(status.GitIsDirty.Value) : null,
            ["index_matches_workspace"] = status.IndexMatchesWorkspace.HasValue ? JsonValue.Create(status.IndexMatchesWorkspace.Value) : null,
            ["stale_after_seconds"] = status.StaleAfterSeconds.HasValue ? JsonValue.Create(status.StaleAfterSeconds.Value) : null,
            ["index_age_seconds"] = status.IndexAgeSeconds.HasValue ? JsonValue.Create(status.IndexAgeSeconds.Value) : null,
            ["failed_checks"] = new JsonArray(failures.Select(failure => JsonValue.Create(failure.Name)).ToArray()),
            ["failed_check_details"] = BuildMcpStatusFailureArray(failures),
            ["readiness"] = BuildMcpStatusReadiness(status),
        };
        if (status.WorkspaceCheck is not null)
            payload["workspace_check"] = JsonSerializer.SerializeToNode(status.WorkspaceCheck);
        if (status.TrustOverrides is { Count: > 0 })
            payload["trust_overrides"] = JsonSerializer.SerializeToNode(status.TrustOverrides);
        if (status.GitExecutable is not null)
            payload["git_executable"] = JsonSerializer.SerializeToNode(status.GitExecutable);
        return payload;
    }

    private JsonObject BuildMcpSessionStatus()
    {
        var state = CurrentInitializeState;
        McpSessionSnapshotCapturedForTests?.Invoke();
        var roots = new JsonArray();
        foreach (var root in state.ClientRootDiagnostics)
            roots.Add(root);

        var session = new JsonObject
        {
            ["log_level"] = _mcpLogLevel,
            ["roots"] = roots,
        };
        if (state.ClientRootsTruncated)
        {
            session["roots_truncated"] = true;
            session["root_count"] = state.ClientRootCount;
            session["root_limit"] = MaxClientRootCount;
            session["root_uri_length_limit"] = MaxClientRootUriChars;
        }
        if (state.ClientName is not null || state.ClientVersion is not null)
        {
            var clientInfo = new JsonObject();
            if (state.ClientNameDisplay is not null)
            {
                clientInfo["name"] = state.ClientName;
                state.ClientNameDisplay.Value.AddMetadata(clientInfo, "name");
            }
            if (state.ClientVersionDisplay is not null)
            {
                clientInfo["version"] = state.ClientVersion;
                state.ClientVersionDisplay.Value.AddMetadata(clientInfo, "version");
            }
            session["client_info"] = clientInfo;
        }
        if (state.ClientCapabilities is not null)
        {
            session["client_capabilities_summary"] = BuildClientCapabilitiesSummary(state, state.ClientCapabilities);
            session["client_capabilities"] = state.ClientCapabilities.DeepClone();
        }
        if (state.ClientCapabilitiesTruncationReason is not null)
        {
            session["client_capabilities_truncated"] = true;
            session["client_capabilities_truncation_reason"] = state.ClientCapabilitiesTruncationReason;
            if (state.ClientCapabilitiesSerializedBytes is { } serializedBytes)
                session["client_capabilities_serialized_bytes"] = serializedBytes;
            session["client_capabilities_byte_limit"] = MaxClientCapabilitiesJsonBytes;
            session["client_capabilities_depth_limit"] = MaxClientCapabilitiesDepth;
            if (!session.ContainsKey("client_capabilities_summary"))
                session["client_capabilities_summary"] = BuildClientCapabilitiesSummary(state, state.ClientCapabilities);
        }
        if (_auditLog is not null)
            session["audit_log"] = BuildAuditLogStatus(_auditLog.SnapshotDiagnostics());
        session["metrics"] = BuildMetricsStatus(MetricsSink.SnapshotDiagnostics());
        return session;
    }

    private JsonObject BuildClientCapabilitiesSummary(InitializeSessionState state, JsonNode? capabilities)
    {
        var summary = new JsonObject
        {
            ["roots"] = state.ClientSupportsRoots,
            ["sampling"] = state.ClientSupportsSampling,
            ["truncated"] = state.ClientCapabilitiesTruncationReason is not null,
            ["truncation_reason"] = state.ClientCapabilitiesTruncationReason,
        };
        if (state.ClientCapabilitiesSerializedBytes is { } serializedBytes)
            summary["serialized_bytes"] = serializedBytes;
        if (capabilities is JsonObject obj)
        {
            summary["top_level_count"] = obj.Count;
            summary["top_level_keys"] = new JsonArray(obj
                .Select(kv => JsonValue.Create(McpBoundedText.ForDisplay(kv.Key, 64).Text))
                .Take(20)
                .ToArray<JsonNode?>());
            summary["top_level_keys_truncated"] = obj.Count > 20;
            if (obj["experimental"] is JsonObject experimental)
            {
                summary["experimental_count"] = experimental.Count;
                summary["experimental_keys"] = new JsonArray(experimental
                    .Select(kv => JsonValue.Create(McpBoundedText.ForDisplay(kv.Key, 64).Text))
                    .Take(20)
                    .ToArray<JsonNode?>());
                summary["experimental_keys_truncated"] = experimental.Count > 20;
            }
        }
        return summary;
    }

    private static bool IsAuditLogDegraded(AuditLogSink.AuditLogDiagnostics? diagnostics)
        => diagnostics is not null
            && (diagnostics.DroppedRecordCount > 0
                || diagnostics.RotationDegraded);

    private static JsonObject BuildAuditLogStatus(AuditLogSink.AuditLogDiagnostics diagnostics)
    {
        var payload = new JsonObject
        {
            ["enabled"] = true,
            ["path"] = diagnostics.Path,
            ["include_values"] = diagnostics.IncludeValues,
            ["max_bytes"] = diagnostics.MaxBytes,
            ["bytes_written"] = diagnostics.BytesWritten,
            ["disposed"] = diagnostics.Disposed,
            ["queue_capacity"] = diagnostics.QueueCapacity,
            ["queue_depth"] = diagnostics.QueueDepth,
            ["queued_record_count"] = diagnostics.QueuedRecordCount,
            ["written_record_count"] = diagnostics.WrittenRecordCount,
            ["dropped_record_count"] = diagnostics.DroppedRecordCount,
            ["queue_full_drop_count"] = diagnostics.QueueFullDropCount,
            ["serialization_failure_count"] = diagnostics.SerializationFailureCount,
            ["write_failure_count"] = diagnostics.WriteFailureCount,
            ["rotation_failure_count"] = diagnostics.RotationFailureCount,
            ["rotation_cleanup_failure_count"] = diagnostics.RotationCleanupFailureCount,
            ["rotation_degraded"] = diagnostics.RotationDegraded,
        };
        if (!string.IsNullOrWhiteSpace(diagnostics.LastDropReason))
            payload["last_drop_reason"] = diagnostics.LastDropReason;
        if (!string.IsNullOrWhiteSpace(diagnostics.LastRotationFailure))
            payload["last_rotation_failure"] = diagnostics.LastRotationFailure;
        return payload;
    }

    private static JsonObject BuildMetricsStatus(MetricsDiagnostics? diagnostics)
    {
        if (diagnostics is null)
            return new JsonObject { ["enabled"] = false };

        var payload = new JsonObject
        {
            ["enabled"] = true,
            ["path"] = diagnostics.Path,
            ["max_bytes"] = diagnostics.MaxBytes,
            ["bytes_written"] = diagnostics.BytesWritten,
            ["disposed"] = diagnostics.Disposed,
            ["degraded"] = diagnostics.Degraded,
            ["queue_capacity"] = diagnostics.QueueCapacity,
            ["queue_depth"] = diagnostics.QueueDepth,
            ["queued_event_count"] = diagnostics.QueuedEventCount,
            ["written_event_count"] = diagnostics.WrittenEventCount,
            ["dropped_event_count"] = diagnostics.DroppedEventCount,
            ["queue_full_drop_count"] = diagnostics.QueueFullDropCount,
            ["serialization_failure_count"] = diagnostics.SerializationFailureCount,
            ["write_failure_count"] = diagnostics.WriteFailureCount,
            ["rotation_failure_count"] = diagnostics.RotationFailureCount,
            ["batch_flush_count"] = diagnostics.BatchFlushCount,
            ["consecutive_failure_count"] = diagnostics.ConsecutiveFailureCount,
            ["recovery_count"] = diagnostics.RecoveryCount,
        };
        if (diagnostics.NextRetryAt is { } nextRetryAt)
            payload["next_retry_at"] = nextRetryAt.ToString("O", CultureInfo.InvariantCulture);
        if (diagnostics.LastRecoveryAt is { } lastRecoveryAt)
            payload["last_recovery_at"] = lastRecoveryAt.ToString("O", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(diagnostics.LastFailure))
            payload["last_failure"] = diagnostics.LastFailure;
        return payload;
    }

    private static string BuildFoldBackfillCommand(string dbPath, bool dbPathExplicit)
    {
        if (!dbPathExplicit)
            return "cdidx backfill-fold";

        return $"cdidx backfill-fold --db {QuoteCommandArgument(ResolveWritableDbPathOrPlaceholder(dbPath))}";
    }

    private static string BuildFoldRebuildRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
    {
        if (!dbPathExplicit)
            return "cdidx index . --rebuild";

        var resolvedDbPath = ResolveWritableDbPathOrPlaceholder(dbPath);
        var targetProject = string.IsNullOrWhiteSpace(projectRoot)
            ? "<projectPath>"
            : QuoteCommandArgument(projectRoot);
        return $"cdidx index {targetProject} --db {QuoteCommandArgument(resolvedDbPath)} --rebuild";
    }

    private static string ResolveWritableDbPathOrPlaceholder(string dbPath)
        => DbPathResolver.TryResolveWritableMutationDbPath(dbPath, out var writableDbPath)
            ? writableDbPath
            : "<writable-db-path>";

    private static string QuoteCommandArgument(string value)
    {
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
            return value;

        var fullPath = DbPathResolver.NormalizeDbPath(value);
        if (!fullPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.GetFullPath(fullPath);

        return fullPath.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{fullPath.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : fullPath;
    }

    private JsonNode ExecuteOutline(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredPathParameter(args, "path", out var path, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        return WithDbReader(id, args, reader =>
        {
            var outline = reader.GetOutline(path);
            if (outline == null)
            {
                var emptyPayload = new JsonObject
                {
                    ["path"] = path,
                    ["error"] = "file not found in index"
                };
                AddFreshnessHint(emptyPayload, reader);
                return CreateToolResult(id, "File not found in index.", emptyPayload);
            }

            var structured = JsonSerializer.SerializeToNode(outline, _jsonOptions)!.AsObject();
            AddNextStepSuggestion(
                structured,
                "excerpt",
                new JsonObject { ["path"] = path, ["startLine"] = 1, ["endLine"] = Math.Min(outline.TotalLines, 80) },
                "Use excerpt for only the relevant outline range instead of reading the whole file.");
            return CreateToolResult(id, $"Outline: {ConsoleUi.Counted(outline.SymbolCount, "symbol")} in {ConsoleUi.Counted(outline.TotalLines, "line")}.", structured);
        });
    }

    private JsonNode ExecuteExcerpt(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredPathParameter(args, "path", out var path, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        var startLine = ReadOptionalIntArgument(args, "startLine");
        if (startLine == null || startLine <= 0)
            return CreateToolErrorResponse(id, "Missing or invalid required parameter: startLine");

        var endLine = ReadOptionalIntArgument(args, "endLine") ?? startLine.Value;
        if (endLine < startLine.Value)
            return CreateToolErrorResponse(id, "endLine must be greater than or equal to startLine");

        var beforeValue = ReadOptionalIntArgument(args, "before");
        if (beforeValue.HasValue && beforeValue.Value < 0)
            return CreateToolErrorResponse(id, $"before must be in [0, {MaxContextLines}]");
        var before = ClampContextLines(beforeValue ?? 0);

        var afterValue = ReadOptionalIntArgument(args, "after");
        if (afterValue.HasValue && afterValue.Value < 0)
            return CreateToolErrorResponse(id, $"after must be in [0, {MaxContextLines}]");
        var after = ClampContextLines(afterValue ?? 0);
        var contextTruncated = beforeValue > MaxContextLines || afterValue > MaxContextLines;

        var focusLine = ReadOptionalIntArgument(args, "focusLine");
        var focusColumn = ReadOptionalIntArgument(args, "focusColumn");
        var focusLengthValue = ReadOptionalIntArgument(args, "focusLength");
        if (focusLengthValue.HasValue && focusLengthValue.Value <= 0)
            return CreateToolErrorResponse(id, "focusLength must be greater than or equal to 1");
        var focusLength = focusLengthValue ?? 1;
        var explicitFocusLength = args?["focusLength"] != null;
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        if (!TryReadMaxOutputBytes(args, out var maxOutputBytes, out var maxOutputBytesError))
            return CreateToolErrorResponse(id, maxOutputBytesError!);

        if (focusLine.HasValue && focusLine.Value <= 0)
            return CreateToolErrorResponse(id, "focusLine must be greater than or equal to 1");
        if (focusColumn.HasValue && focusColumn.Value <= 0)
            return CreateToolErrorResponse(id, "focusColumn must be greater than or equal to 1");
        if (!focusColumn.HasValue && (focusLine.HasValue || explicitFocusLength))
            return CreateToolErrorResponse(id, "focusLine and focusLength require focusColumn");

        return WithDbReader(id, args, reader =>
        {
            if (focusLine.HasValue)
            {
                var file = reader.GetFileByPath(path);
                if (file != null)
                {
                    // `before` is bounded by MaxContextLines and `startLine` by `int.MaxValue`, but
                    // `endLine` is caller-supplied: int + int can still overflow when endLine is
                    // close to `int.MaxValue`. Use long intermediates so the clamp sees the real
                    // window before narrowing back to int (#1528).
                    // `before` は MaxContextLines、`startLine` は `int.MaxValue` で押さえているが、
                    // `endLine` は呼び出し側入力で `int.MaxValue` 近傍なら int 同士の加算が overflow し得る。
                    // long 中間変数で実窓を確定させてから int に戻す（#1528）。
                    var requestedStart = (int)Math.Max(1L, (long)startLine.Value - before);
                    var requestedEnd = (int)Math.Min(file.Lines, (long)endLine + after);
                    if (focusLine.Value < requestedStart || focusLine.Value > requestedEnd)
                        return CreateToolErrorResponse(id, $"focusLine ({focusLine.Value}) must be within the returned excerpt range ({requestedStart}-{requestedEnd})");
                }
            }
            if (focusColumn.HasValue)
            {
                var focusLineLength = reader.GetExcerptFocusLineLength(
                    path,
                    startLine.Value,
                    endLine,
                    before,
                    after,
                    focusLine ?? startLine.Value);
                if (focusLineLength.HasValue && focusColumn.Value > focusLineLength.Value)
                    return CreateToolErrorResponse(id, $"focusColumn ({focusColumn.Value}) must be within the focused line length ({focusLineLength.Value})");
            }

            var excerpt = reader.GetExcerpt(path, startLine.Value, endLine, before, after, maxLineWidth, focusLine ?? startLine.Value, focusColumn, focusLength);
            if (excerpt == null)
            {
                var emptyPayload = new JsonObject
                {
                    ["path"] = path,
                    ["count"] = 0
                };
                AddRecoveryHint(
                    emptyPayload,
                    "file_or_range_not_indexed",
                    "excerpt found no indexed content for the requested range; verify the path with files or outline, then retry with an indexed line range.",
                    "outline",
                    new JsonObject { ["path"] = path });
                AddFreshnessHint(emptyPayload, reader);
                return CreateToolResult(id, "No excerpt found.", emptyPayload);
            }

            ExcerptRecoveryCommandFormatter.ApplyDbPath(excerpt, _dbPath);
            var payload = JsonSerializer.SerializeToNode(excerpt, _jsonOptions)!.AsObject();
            ApplyExcerptOutputBudget(payload, maxOutputBytes);
            payload["maxOutputBytes"] = maxOutputBytes;
            payload["before"] = before;
            payload["after"] = after;
            payload["contextTruncated"] = contextTruncated;
            payload["maxLineWidth"] = maxLineWidth;
            if (focusLine.HasValue)
                payload["focusLine"] = focusLine.Value;
            if (focusColumn.HasValue)
                payload["focusColumn"] = focusColumn.Value;
            payload["focusLength"] = focusLength;
            AddNextStepSuggestion(
                payload,
                "outline",
                new JsonObject { ["path"] = excerpt.Path },
                "Use outline to navigate neighboring symbols before requesting more ranges from the same file.");
            return CreateToolResult(id, "Excerpt returned.", payload);
        });
    }

    private static bool TryReadMaxOutputBytes(JsonNode? args, out int maxOutputBytes, out string? error)
    {
        maxOutputBytes = DefaultExcerptOutputByteLimit;
        error = null;
        if (args?["maxOutputBytes"] is not JsonNode node)
            return true;
        if (node is not JsonValue value || !value.TryGetValue<int>(out var requested))
        {
            error = "maxOutputBytes must be an integer";
            return false;
        }
        if (requested <= 0)
        {
            error = "maxOutputBytes must be greater than or equal to 1";
            return false;
        }
        maxOutputBytes = Math.Min(requested, DefaultExcerptOutputByteLimit);
        return true;
    }

    internal static void ApplyExcerptOutputBudget(JsonObject payload, int maxOutputBytes)
    {
        var contentKey = payload.ContainsKey("content") ? "content" : "Content";
        if (payload[contentKey]?.GetValue<string>() is not string content)
            return;
        if (Encoding.UTF8.GetByteCount(content) <= maxOutputBytes)
            return;

        var builder = new StringBuilder();
        var retainedLineCount = 0;
        var firstRetainedLine = true;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var candidate = firstRetainedLine ? line : builder.ToString() + "\n" + line;
            if (Encoding.UTF8.GetByteCount(candidate) > maxOutputBytes)
                break;
            builder.Clear();
            builder.Append(candidate);
            retainedLineCount++;
            firstRetainedLine = false;
        }
        payload[contentKey] = builder.ToString();
        TrimExcerptCoordinatePayload(payload, retainedLineCount);
        payload["contentTruncated"] = true;
        payload["truncated"] = true;
        payload["truncation_reason"] = "output_size_cap";
    }

    private static void TrimExcerptCoordinatePayload(JsonObject payload, int retainedLineCount)
    {
        var spansKey = FirstPayloadKey(payload, "contentLineSpans", "content_line_spans", "ContentLineSpans");
        var retainedSpans = new List<ExcerptPayloadSpan>();
        var hasSpanMapping = false;
        if (spansKey is not null && payload[spansKey] is JsonArray spans)
        {
            hasSpanMapping = true;
            var trimmedSpans = new JsonArray();
            foreach (var spanNode in spans)
            {
                if (spanNode is not JsonObject span)
                    continue;
                var contentLine = GetPayloadInt(span, "contentLine", "content_line", "ContentLine");
                if (!contentLine.HasValue || contentLine.Value > retainedLineCount)
                    continue;

                trimmedSpans.Add(span.DeepClone());
                var sourceLine = GetPayloadInt(span, "sourceLine", "source_line", "SourceLine");
                var sourceStartColumn = GetPayloadInt(span, "sourceStartColumn", "source_start_column", "SourceStartColumn");
                var sourceEndColumn = GetPayloadInt(span, "sourceEndColumn", "source_end_column", "SourceEndColumn");
                if (sourceLine.HasValue && sourceStartColumn.HasValue && sourceEndColumn.HasValue)
                    retainedSpans.Add(new ExcerptPayloadSpan(sourceLine.Value, sourceStartColumn.Value, sourceEndColumn.Value));
            }

            payload[spansKey] = trimmedSpans;
        }

        var tokensKey = FirstPayloadKey(payload, "semanticTokens", "semantic_tokens", "SemanticTokens");
        if (tokensKey is null || payload[tokensKey] is not JsonArray tokens)
            return;
        if (!hasSpanMapping)
        {
            if (retainedLineCount == 0)
                payload[tokensKey] = new JsonArray();
            return;
        }

        var trimmedTokens = new JsonArray();
        if (retainedLineCount > 0 && retainedSpans.Count > 0)
        {
            foreach (var tokenNode in tokens)
            {
                if (tokenNode is not JsonObject token)
                    continue;
                var startLine = GetPayloadInt(token, "startLine", "start_line", "StartLine");
                var endLine = GetPayloadInt(token, "endLine", "end_line", "EndLine");
                var startColumn = GetPayloadInt(token, "startColumn", "start_column", "StartColumn");
                var endColumn = GetPayloadInt(token, "endColumn", "end_column", "EndColumn");
                if (!startLine.HasValue || !endLine.HasValue || !startColumn.HasValue || !endColumn.HasValue)
                    continue;
                if (retainedSpans.Any(span =>
                    startLine.Value == span.SourceLine &&
                    endLine.Value == span.SourceLine &&
                    startColumn.Value >= span.SourceStartColumn &&
                    endColumn.Value <= span.SourceEndColumn))
                {
                    trimmedTokens.Add(token.DeepClone());
                }
            }
        }

        payload[tokensKey] = trimmedTokens;
    }

    private static string? FirstPayloadKey(JsonObject payload, params string[] keys)
        => keys.FirstOrDefault(payload.ContainsKey);

    private static int? GetPayloadInt(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj[key] is JsonNode node)
                return node.GetValue<int>();
        }

        return null;
    }

    private readonly record struct ExcerptPayloadSpan(int SourceLine, int SourceStartColumn, int SourceEndColumn);

    private JsonNode ExecuteFindInFile(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());

        var pathPatterns = ReadScopedPathList(args);
        if (pathPatterns == null || pathPatterns.Count == 0)
            return CreateToolErrorResponse(id, HasBlankPathFilter(args)
                ? "Parameter \"path\" cannot be empty or whitespace-only"
                : "Missing required parameter: path");

        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var beforeValue = ReadOptionalIntArgument(args, "before");
        if (beforeValue.HasValue && beforeValue.Value < 0)
            return CreateToolErrorResponse(id, "before must be greater than or equal to 0");
        var before = ClampContextLines(beforeValue ?? 0);

        var afterValue = ReadOptionalIntArgument(args, "after");
        if (afterValue.HasValue && afterValue.Value < 0)
            return CreateToolErrorResponse(id, "after must be greater than or equal to 0");
        var after = ClampContextLines(afterValue ?? 0);
        var contextTruncated = beforeValue > MaxContextLines || afterValue > MaxContextLines;
        var snippetLinesValue = ReadOptionalIntArgument(args, "snippetLines");
        if (snippetLinesValue.HasValue && (snippetLinesValue.Value <= 0 || snippetLinesValue.Value > SearchSnippetFormatter.MaxSnippetLines))
            return CreateToolErrorResponse(id, $"snippetLines must be in [1, {SearchSnippetFormatter.MaxSnippetLines}]");
        if (snippetLinesValue.HasValue)
        {
            var surroundingLines = snippetLinesValue.Value - 1;
            if (!beforeValue.HasValue)
                before = surroundingLines / 2;
            if (!afterValue.HasValue)
                after = surroundingLines - before;
        }
        var focusLine = args?["focusLine"]?.GetValue<int>();
        if (focusLine.HasValue && focusLine.Value <= 0)
            return CreateToolErrorResponse(id, "focusLine must be greater than or equal to 1");
        var focusColumn = args?["focusColumn"]?.GetValue<int>();
        if (focusColumn.HasValue && focusColumn.Value <= 0)
            return CreateToolErrorResponse(id, "focusColumn must be greater than or equal to 1");
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var exact = args?["exact"]?.GetValue<bool>() ?? false;
        var regex = args?["regex"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            List<FileFindResult> results;
            try
            {
                results = reader.FindInFiles(query, limit, lang, pathPatterns, excludePaths, excludeTests, before, after, exact, maxLineWidth, focusLine, focusColumn, regex).Results;
            }
            catch (RegexMatchTimeoutException ex) when (regex)
            {
                return CreateToolErrorResponse(
                    id,
                    RegexTimeoutPolicy.FormatFindTimeout(ex),
                    category: RegexTimeoutPolicy.RegexTimeoutCategory,
                    suggestion: RegexTimeoutPolicy.McpFindTimeoutSuggestion,
                    retrySafe: true,
                    extraData: new JsonObject
                    {
                        ["error_code"] = CommandErrorCodes.RegexMatchTimeout,
                        ["timeout_ms"] = ex.MatchTimeout.TotalMilliseconds,
                    });
            }
            catch (ArgumentException) when (regex)
            {
                return CreateToolErrorResponse(id, "invalid regular expression. Check regex syntax and retry.");
            }
            var structured = new JsonObject
            {
                ["query"] = query,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["before"] = before,
                ["after"] = after,
                ["contextTruncated"] = contextTruncated,
                ["maxLineWidth"] = maxLineWidth,
                ["exact"] = exact,
                ["regex"] = regex,
                ["count"] = results.Count,
                ["fileCount"] = results.Select(r => r.Path).Distinct().Count(),
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions),
            };
            if (snippetLinesValue.HasValue)
                structured["snippetLines"] = snippetLinesValue.Value;
            if (focusLine.HasValue)
                structured["focusLine"] = focusLine.Value;
            if (focusColumn.HasValue)
                structured["focusColumn"] = focusColumn.Value;
            if (results.Count == 0)
            {
                AddFreshnessHint(structured, reader);
                adjustments.ApplyTo(structured);
                return CreateToolResult(id, "No matches found.", structured);
            }

            var fileCount = structured["fileCount"]!.GetValue<int>();
            adjustments.ApplyTo(structured);
            return CreateToolResult(id, $"Found {ConsoleUi.Counted(results.Count, "in-file match", "in-file matches")} across {ConsoleUi.Counted(fileCount, "file")}.", structured);
        });
    }

    private static int ClampContextLines(int value)
    {
        return Math.Min(value, MaxContextLines);
    }

    private JsonNode ExecuteBatchQueryEstimate(JsonNode? id, JsonArray queries, int responseByteLimit, ArgumentAdjustmentCollector adjustments)
    {
        var slotEstimates = new JsonArray();
        for (var requestIndex = 0; requestIndex < queries.Count; requestIndex++)
        {
            var queryObject = queries[requestIndex] as JsonObject;
            slotEstimates.Add(BuildBatchSlotDescriptor(requestIndex, queryObject));
        }

        var payload = new JsonObject
        {
            ["count"] = 0,
            ["total_count"] = queries.Count,
            ["success_count"] = 0,
            ["failure_count"] = 0,
            ["partial_failure"] = false,
            ["failure_scope"] = "none",
            ["cascade_started_at_index"] = null,
            ["estimate_only"] = true,
            ["metadata"] = new JsonObject
            {
                ["submitted"] = queries.Count,
                ["executed"] = 0,
                ["errors"] = 0,
                ["total_elapsed_ms"] = 0,
                ["success_count"] = 0,
                ["failure_count"] = 0,
                ["response_byte_limit"] = responseByteLimit,
                ["estimated_response_bytes"] = responseByteLimit,
            },
            ["slot_estimates"] = slotEstimates,
            ["results"] = new JsonArray(),
        };
        adjustments.ApplyTo(payload);

        var summary = $"Estimated batch_query envelope for {queries.Count} query slot(s); no slots executed.";
        var estimatedResponseBytes = EstimateJsonUtf8Bytes(CreateToolResult(id, summary, payload.DeepClone()), responseByteLimit);
        ((JsonObject)payload["metadata"]!)["estimated_response_bytes"] = estimatedResponseBytes;
        payload["estimate_exceeds_response_byte_limit"] = estimatedResponseBytes > responseByteLimit;
        return CreateToolResult(id, summary, payload);
    }

    private static int ReadBatchQueryResponseByteLimit(JsonNode? args, ArgumentAdjustmentCollector adjustments)
    {
        var serverLimit = GetBatchQueryResponseByteLimit();
        var requested = ReadOptionalIntArgument(args, "maxResponseBytes");
        if (!requested.HasValue)
            return serverLimit;
        var effective = Math.Min(requested.Value, serverLimit);
        if (effective != requested.Value)
            adjustments.AddClamped("maxResponseBytes", requested.Value, effective, 1, serverLimit);
        return effective;
    }

    private static JsonObject BuildBatchSlotDescriptor(int requestIndex, JsonObject? queryObject)
    {
        var toolName = queryObject?["tool"] is JsonValue toolValue && toolValue.TryGetValue<string>(out var parsedToolName)
            ? parsedToolName
            : null;
        var toolArgs = queryObject?["arguments"];
        var descriptor = new JsonObject
        {
            ["request_index"] = requestIndex,
            ["args_summary"] = BuildArgsSummary(toolArgs),
        };
        AddBatchSlotId(descriptor, ReadBatchSlotId(queryObject));
        AddToolDisplayData(descriptor, toolName);
        return descriptor;
    }

    private static JsonObject BuildBatchSplitHint(int submittedCount, int? cascadeStartedAtIndex, int retainedResultCount)
    {
        var nextRequestIndex = cascadeStartedAtIndex ?? submittedCount;
        return new JsonObject
        {
            ["reason"] = "response_byte_limit_exceeded",
            ["next_request_index"] = nextRequestIndex,
            ["suggested_query_count"] = Math.Max(1, retainedResultCount),
            ["resume_cursor"] = $"batch_query:v1:{nextRequestIndex}",
        };
    }

    private static bool RemoveBatchTruncatedQueryToolDisplay(JsonArray truncatedQueries)
    {
        var changed = false;
        foreach (var item in truncatedQueries)
        {
            if (item is JsonObject entry)
                changed |= entry.Remove("tool");
        }
        return changed;
    }

    private static bool CompactBatchTruncatedQueryArgsSummaries(JsonArray truncatedQueries)
    {
        var changed = false;
        foreach (var item in truncatedQueries)
        {
            if (item is JsonObject entry
                && entry["args_summary"] is JsonValue value
                && value.TryGetValue<string>(out var summary)
                && summary.Length > 0)
            {
                entry["args_summary"] = string.Empty;
                changed = true;
            }
        }
        return changed;
    }

    private static string? ReadBatchSlotId(JsonObject? queryObject)
    {
        if (TryReadBatchSlotIdValue(queryObject?["slotId"], out var slotId)
            || TryReadBatchSlotIdValue(queryObject?["id"], out slotId))
            return McpBoundedText.ForDisplay(slotId!, MaxRequestIdCharacterCount).Text;
        return null;
    }

    private static bool TryReadBatchSlotIdValue(JsonNode? node, out string? slotId)
    {
        slotId = null;
        if (node is not JsonValue value)
            return false;
        if (value.TryGetValue<string>(out var text))
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            slotId = text;
            return true;
        }
        if (value.TryGetValue<int>(out var intValue))
        {
            slotId = intValue.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (value.TryGetValue<long>(out var longValue))
        {
            slotId = longValue.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    private static void AddBatchSlotId(JsonObject entry, string? slotId)
    {
        if (!string.IsNullOrEmpty(slotId))
            entry["slot_id"] = slotId;
    }

    private static int GetBatchQueryResponseByteLimit()
        => ReadPositiveIntEnvironmentLimit(
            BatchQueryResponseByteLimitEnvVar,
            DefaultBatchQueryResponseByteLimit,
            MaxBatchQueryResponseByteLimit,
            "MCP batch_query response byte limit");

    private int EstimateJsonUtf8Bytes(JsonNode node, int maxBytes = MaxBatchQueryResponseByteLimit)
    {
        _ = TryMeasureJsonUtf8BytesWithinLimit(node, _jsonOptions, maxBytes, out var bytesWritten);
        return bytesWritten;
    }

    private int EstimateBatchResponseBytes(JsonNode? id, string summary, int submittedCount, int successCount, int failureCount,
        string failureScope, int? cascadeStartedAtIndex, int responseByteLimit, JsonArray resultsArray, bool truncated, JsonArray truncatedQueries,
        ArgumentAdjustmentCollector? adjustments = null)
    {
        var payload = new JsonObject
        {
            ["count"] = resultsArray.Count,
            ["total_count"] = submittedCount,
            ["success_count"] = successCount,
            ["failure_count"] = failureCount,
            ["partial_failure"] = failureCount > 0 || cascadeStartedAtIndex.HasValue,
            ["failure_scope"] = failureScope,
            ["cascade_started_at_index"] = cascadeStartedAtIndex,
            ["metadata"] = new JsonObject
            {
                ["submitted"] = submittedCount,
                ["executed"] = successCount + failureCount,
                ["errors"] = failureCount,
                ["total_elapsed_ms"] = 0,
                ["success_count"] = successCount,
                ["failure_count"] = failureCount,
                ["response_byte_limit"] = responseByteLimit,
                ["estimated_response_bytes"] = responseByteLimit,
            },
            ["results"] = resultsArray.DeepClone(),
        };
        if (truncated)
        {
            payload["truncated"] = true;
            payload["truncated_queries"] = truncatedQueries.DeepClone();
        }
        adjustments?.ApplyTo(payload);

        return EstimateJsonUtf8Bytes(CreateToolResult(id, summary, payload), responseByteLimit);
    }

    private int EstimateBatchAppendBytes(int currentEstimateBytes, JsonObject entry, int executedCount, int successCount, int failureCount)
    {
        var entryBytes = EstimateJsonUtf8Bytes(entry);
        var digitGrowth = CountDecimalDigits(executedCount) + CountDecimalDigits(successCount) + CountDecimalDigits(failureCount);
        return SaturatingAdd(
            currentEstimateBytes,
            entryBytes,
            BatchQueryIncrementalEstimatePaddingBytes,
            digitGrowth);
    }

    private static int CountDecimalDigits(int value)
    {
        var digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }
        return digits;
    }

    private static int SaturatingAdd(params int[] values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            total += value;
            if (total >= int.MaxValue)
                return int.MaxValue;
        }
        return (int)total;
    }

    private static string GetBatchFailureScope(int submittedCount, int successCount, int failureCount, int? cascadeStartedAtIndex)
    {
        if (cascadeStartedAtIndex.HasValue && cascadeStartedAtIndex.Value < submittedCount)
            return "cascading";
        return failureCount == 0 ? "none" : "isolated";
    }

    /// <summary>
    /// Build a compact, single-line summary string of a batch slot's arguments
    /// so callers can correlate per-slot timings with what was requested
    /// without re-parsing the original payload.
    /// バッチスロットの arguments を1行で要約し、呼び出し側がペイロードを
    /// 再解析せずスロット別時間と対応付けられるようにする。
    /// </summary>
    private const int BatchArgsSummaryMaxLength = 200;
    private static string BuildArgsSummary(JsonNode? toolArgs)
    {
        if (toolArgs is not JsonObject obj)
            return string.Empty;
        if (obj.Count == 0)
            return string.Empty;
        var parts = new List<string>(obj.Count);
        foreach (var kv in obj)
        {
            var key = McpBoundedText.ForDisplay(kv.Key).Text;
            var rendered = RenderBatchArgumentSummaryValue(kv.Value);
            parts.Add($"{key}={rendered}");
        }
        var joined = string.Join(", ", parts);
        if (joined.Length > BatchArgsSummaryMaxLength)
            joined = joined.Substring(0, BatchArgsSummaryMaxLength - 1) + "…";
        return joined;
    }

    private static string RenderBatchArgumentSummaryValue(JsonNode? value)
    {
        if (value is null)
            return "null";
        if (value is JsonArray arr)
            return $"[{arr.Count}]";
        if (value is JsonObject inner)
            return $"{{{inner.Count}}}";
        if (value is not JsonValue jsonValue)
            return "<json>";

        return jsonValue.GetValueKind() switch
        {
            JsonValueKind.String => jsonValue.TryGetValue<string>(out var text)
                ? JsonSerializer.Serialize(McpBoundedText.ForDisplay(text).Text)
                : "\"<string>\"",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Number => RenderBatchNumericArgument(jsonValue),
            _ => "<json>",
        };
    }

    private static string RenderBatchNumericArgument(JsonValue value)
    {
        if (value.TryGetValue<int>(out var intValue))
            return intValue.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<long>(out var longValue))
            return longValue.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<decimal>(out var decimalValue))
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var doubleValue) && double.IsFinite(doubleValue))
            return doubleValue.ToString("R", CultureInfo.InvariantCulture);
        return "<number>";
    }

    private JsonNode ExecuteDeps(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultImpactLimit, adjustments);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var includeGenerated = args?["includeGenerated"]?.GetValue<bool>() ?? false;
        var reverse = args?["reverse"]?.GetValue<bool>() ?? false;
        var cyclesOnly = args?["cycles"]?.GetValue<bool>() ?? false;
        var format = args?["format"]?.GetValue<string>()?.ToLowerInvariant() ?? "edgelist";

        return WithDbReader(id, args, reader =>
        {
            var cycleCandidateLimit = QueryCommandRunner.GetDependencyCycleGraphLimit(limit);
            var cycleCandidateRowCount = 0;
            var results = cyclesOnly
                ? reader.GetFileDependencyCycleCandidates(
                    checked(cycleCandidateLimit + 1),
                    out cycleCandidateRowCount,
                    lang,
                    pathPatterns,
                    excludePaths,
                    excludeTests,
                    reverse)
                : reader.GetFileDependencies(limit, lang, pathPatterns, excludePaths, excludeTests, reverse);
            var cycleCandidateRowsRead = cyclesOnly ? cycleCandidateRowCount : 0;
            var cycleCandidates = cyclesOnly ? results.Take(cycleCandidateLimit).ToList() : results;
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            List<List<string>> cycles = [];
            var outputEdges = cyclesOnly ? QueryCommandRunner.FilterCycleEdges(cycleCandidates, out cycles) : results;
            var cycleCandidateTruncated = cyclesOnly && cycleCandidateRowsRead > cycleCandidateLimit;
            var cycleDisplayTruncated = cyclesOnly && cycles.Count > limit;
            if (cyclesOnly)
            {
                cycles = cycles.Take(limit).ToList();
                var cycleNodes = cycles.SelectMany(static cycle => cycle).ToHashSet(StringComparer.Ordinal);
                outputEdges = outputEdges
                    .Where(edge => cycleNodes.Count == 0 || (cycleNodes.Contains(edge.SourcePath) && cycleNodes.Contains(edge.TargetPath)))
                    .Take(limit)
                    .ToList();
            }
            var sqlGraphSignalPaths = cyclesOnly
                ? cycles.Count > 0
                    ? cycles.SelectMany(static cycle => cycle)
                    : cycleCandidates.SelectMany(static result => new[] { result.SourcePath, result.TargetPath })
                : results.SelectMany(static result => new[] { result.SourcePath, result.TargetPath });
            var sqlGraphSignal = results.Count == 0
                ? baseSqlGraphSignal
                : QueryCommandRunner.NarrowSqlGraphContractSignalByPaths(
                    reader,
                    baseSqlGraphSignal,
                    sqlGraphSignalPaths,
                    lang);
            var payload = new JsonObject { ["count"] = cyclesOnly ? cycles.Count : results.Count };
            if (cyclesOnly)
                payload["cycles"] = QueryCommandRunner.BuildDependencyCyclesJson(cycles);
            else if (format == "json-graph")
                payload["graph"] = BuildJsonGraphPayload(outputEdges);
            else
                payload["edges"] = JsonSerializer.SerializeToNode(outputEdges, _jsonOptions);
            if (cyclesOnly)
            {
                var truncatedReason = cycleCandidateTruncated
                    ? "candidate_edge_limit"
                    : cycleDisplayTruncated
                        ? "display_limit"
                        : null;
                var terminationReason = truncatedReason switch
                {
                    "candidate_edge_limit" => "candidate_limit_reached",
                    "display_limit" => "display_limit_reached",
                    _ => "completed",
                };
                QueryCommandRunner.AddDependencyCycleAnalysisJsonFields(
                    payload,
                    cycleCandidateTruncated || cycleDisplayTruncated,
                    terminationReason,
                    truncatedReason,
                    Math.Min(cycleCandidateRowsRead, cycleCandidateLimit),
                    cycleCandidateLimit,
                    limit,
                    QueryCommandRunner.DependencyCycleDetectionMode,
                    QueryCommandRunner.BuildMcpDependencyCycleNextStepFlagsJson(
                        truncatedReason,
                        cycleCandidateLimit,
                        limit));
            }
            payload["format"] = format;
            payload["includeGenerated"] = includeGenerated;
            payload["generated_code_filter_supported"] = true;
            payload["generated_code_scope"] = "source_and_target_files";
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            var summary = payload["count"]!.GetValue<int>() > 0
                ? cyclesOnly ? $"Found {ConsoleUi.Counted(cycles.Count, "dependency cycle")}." : $"Found {ConsoleUi.Counted(results.Count, "dependency edge")}."
                : cyclesOnly ? "No dependency cycles found." : "No file dependencies found.";
            if (results.Count == 0)
                AddFreshnessHint(payload, reader);
            adjustments.ApplyTo(payload);
            return CreateToolResult(id, summary, payload);
        });
    }

    private static JsonObject BuildJsonGraphPayload(IReadOnlyList<FileDependencyResult> edges)
    {
        var nodes = new JsonArray();
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var graphEdges = new JsonArray();
        foreach (var edge in edges)
        {
            if (seenNodes.Add(edge.SourcePath))
                nodes.Add(new JsonObject { ["id"] = edge.SourcePath });
            if (seenNodes.Add(edge.TargetPath))
                nodes.Add(new JsonObject { ["id"] = edge.TargetPath });

            graphEdges.Add(new JsonObject
            {
                ["source"] = edge.SourcePath,
                ["target"] = edge.TargetPath,
                ["reference_count"] = edge.ReferenceCount,
                ["ranking_score"] = edge.RankingScore,
            });
        }

        return new JsonObject { ["nodes"] = nodes, ["edges"] = graphEdges };
    }

}
