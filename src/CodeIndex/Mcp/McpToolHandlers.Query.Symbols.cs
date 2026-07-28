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
        var includeGenerated = args?["includeGenerated"]?.GetValue<bool>() ?? false;
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
        var rawCursor = args?["cursor"]?.GetValue<string>();
        if (countOnly && rawCursor is not null)
            return CreateToolErrorResponse(id, "cursor is not supported when symbols uses countOnly or format=count.");
        McpQueryCursor? cursor = null;
        if (rawCursor is not null && !TryParseMcpQueryCursor(rawCursor, out cursor))
        {
            return CreateMcpCursorError(
                id,
                "symbols",
                "cursor_malformed",
                "cursor must be an opaque response:v2 next_cursor returned by symbols.",
                stale: false);
        }

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

        return WithDbReader(id, args, reader => reader.RunInReadSnapshot(() =>
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

            var queryFingerprint = BuildMcpQueryFingerprint(
                "symbols",
                limit,
                format,
                new Dictionary<string, string?>
                {
                    ["query"] = query,
                    ["kind"] = kind,
                    ["lang"] = lang,
                    ["since"] = since?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    ["exact"] = exact ? "true" : "false",
                    ["exclude-tests"] = excludeTests ? "true" : "false",
                    ["include-generated"] = includeGenerated ? "true" : "false",
                },
                ("names", effectiveQueries, PreserveOrder: true),
                ("path", pathPatterns, PreserveOrder: false),
                ("exclude-path", excludePaths, PreserveOrder: false),
                ("visibility", visibilityFilters, PreserveOrder: false),
                ("exclude-visibility", excludeVisibilityFilters, PreserveOrder: false));
            var generation = BuildMcpGenerationFingerprint(reader, includeFoldState: true);
            var total = reader.CountSearchSymbolsTotal(
                effectiveQueries,
                kind,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests,
                since,
                exact,
                visibilityFilters,
                excludeVisibilityFilters).Count;
            if (ValidateMcpQueryCursor(
                    id,
                    "symbols",
                    cursor,
                    queryFingerprint,
                    generation.Fingerprint,
                    total) is JsonObject cursorError)
            {
                return cursorError;
            }
            var offset = cursor?.Offset ?? 0;
            var results = reader.SearchSymbols(
                effectiveQueries,
                limit,
                kind,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests,
                since,
                exact,
                visibilityFilters,
                excludeVisibilityFilters,
                offset: offset);
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
                AddMcpPaginationEnvelope(
                    payload,
                    total,
                    returnedCount: 0,
                    offset,
                    limit,
                    queryFingerprint,
                    generation);
                AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
                if (hasExactPredicate)
                    AddExactGraphSignal(payload, exactSignal);
                if (total == 0)
                {
                    AddExactZeroHint(payload, exactZeroHint);
                    AddFreshnessHint(payload, reader);
                }
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
            AddMcpPaginationEnvelope(
                structured,
                total,
                results.Count,
                offset,
                limit,
                queryFingerprint,
                generation);
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
        }));
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

}
