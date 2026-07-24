using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private JsonNode ExecuteSymbolHotspots(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var requestedGroupBy = args?["groupBy"]?.GetValue<string>()?.ToLowerInvariant();
        if (!QueryCommandRunner.TryResolveHotspotsGroupBy(requestedGroupBy, lang, groupByName: false, out var groupBy, out var groupByError))
        {
            var groupByDisplay = McpBoundedText.ForDisplay(requestedGroupBy ?? string.Empty);
            var extra = new JsonObject
            {
                ["parameter"] = "groupBy",
                ["value"] = groupByDisplay.Text,
            };
            groupByDisplay.AddMetadata(extra, "value");
            var message = groupByError.StartsWith("Error: ", StringComparison.Ordinal)
                ? groupByError["Error: ".Length..]
                : groupByError;
            message = message
                .Replace("hotspots --group-by", "symbol_hotspots groupBy", StringComparison.Ordinal)
                .Replace("--lang sql", "lang=sql", StringComparison.Ordinal)
                .Replace("--group-by symbol", "groupBy=symbol", StringComparison.Ordinal)
                .Replace("--group-by file", "groupBy=file", StringComparison.Ordinal);
            return CreateToolErrorResponse(
                id,
                message,
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Use groupBy=symbol or groupBy=file for non-SQL scopes, or set lang=sql with groupBy=statement.",
                retrySafe: false,
                extraData: extra);
        }
        if (groupBy == QueryCommandRunner.HotspotsGroupedByNameKind)
        {
            var groupByDisplay = McpBoundedText.ForDisplay(requestedGroupBy ?? groupBy);
            var extra = new JsonObject
            {
                ["parameter"] = "groupBy",
                ["value"] = groupByDisplay.Text,
            };
            groupByDisplay.AddMetadata(extra, "value");
            return CreateToolErrorResponse(
                id,
                $"Unsupported symbol_hotspots groupBy '{groupByDisplay.Text}'. Use groupBy=symbol or groupBy=file for non-SQL scopes, or set lang=sql with groupBy=statement.",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Use groupBy=symbol or groupBy=file for non-SQL scopes, or set lang=sql with groupBy=statement.",
                retrySafe: false,
                extraData: extra);
        }
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var visibilityFilters = ReadStringOrCommaSeparatedList(args, "visibility");
        var excludeVisibilityFilters = ReadStringOrCommaSeparatedList(args, "excludeVisibility");

        return WithDbReader(id, args, reader =>
        {
            var fileResults = groupBy == "file"
                ? reader.GetFileSymbolHotspots(limit, kind, lang, pathPatterns, excludePaths, excludeTests, visibilityFilters, excludeVisibilityFilters)
                : null;
            var results = fileResults == null
                ? reader.GetSymbolHotspots(limit, kind, lang, pathPatterns, excludePaths, excludeTests, visibilityFilters, excludeVisibilityFilters)
                : [];
            var hotspotSignal = reader.GetHotspotFamilySignal(lang);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            var hdlGraphSignal = reader.GetHdlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            var zeroResultSqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePaths, excludeTests));
            var resultLangs = fileResults != null
                ? fileResults.Select(result => result.Lang)
                : results.Select(result => result.Symbol.Lang);
            var visibleCount = fileResults?.Count ?? results.Count;
            var sqlGraphSignal = visibleCount == 0
                ? zeroResultSqlGraphSignal
                : QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                    baseSqlGraphSignal,
                    resultLangs,
                    lang);
            JsonNode? hotspotsNode;
            if (fileResults != null)
            {
                var hotspots = new JsonArray();
                foreach (var result in fileResults)
                {
                    hotspots.Add(new JsonObject
                    {
                        ["path"] = result.Path,
                        ["lang"] = result.Lang,
                        ["reference_count"] = result.ReferenceCount,
                        ["symbol_count"] = result.SymbolCount,
                    });
                }
                hotspotsNode = hotspots;
            }
            else
            {
                hotspotsNode = ToJsonArray(results, r => new
                {
                    name = r.Symbol.Name,
                    kind = r.Symbol.Kind,
                    path = r.Symbol.Path,
                    line = r.Symbol.Line,
                    reference_count = r.ReferenceCount,
                    reference_score = r.ReferenceScore,
                    ranking_score = r.RankingScore,
                    generic_name_penalty = r.GenericNamePenalty,
                    visibility = r.Symbol.Visibility,
                    container = r.Symbol.ContainerName,
                });
            }

            var payload = new JsonObject
            {
                ["count"] = visibleCount,
                ["grouped_by"] = groupBy,
                ["hotspots"] = hotspotsNode
            };
            QueryCommandRunner.AddHotspotsGroupingContractJsonFields(payload, groupBy, queryOptions: null, jsonOptions: _jsonOptions, countOnly: false);
            payload["query_context"] = BuildSymbolHotspotsQueryContext(
                limit,
                kind,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests,
                groupBy,
                QueryCommandRunner.GetHotspotsGroupingUnit(groupBy),
                QueryCommandRunner.GetHotspotsCountKind(groupBy, countOnly: false),
                QueryCommandRunner.GetHotspotsLimitAppliesTo(groupBy, countOnly: false));
            AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
            if (fileResults != null)
                payload["files"] = fileResults.Count;
            AddHotspotFamilySignal(payload, hotspotSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            AddHdlGraphContractSignal(payload, hdlGraphSignal);
            var summary = visibleCount > 0
                ? $"Found {ConsoleUi.Counted(visibleCount, $"{groupBy} hotspot")}."
                : "No symbol hotspots found.";
            if (!hotspotSignal.Ready)
            {
                payload["note"] = "cross-file hotspot family grouping is degraded; conservative same-file fallback may hide or undercount hotspot families until the next successful reindex.";
                summary += " Warning: cross-file hotspot family grouping is degraded, so results may be conservative until the next successful reindex.";
            }
            if (visibleCount == 0)
            {
                AddRecoveryHint(
                    payload,
                    "no_results",
                    "symbol_hotspots returned no rows; verify that graph references are indexed and loosen kind/lang/path filters.",
                    "status",
                    new JsonObject());
                AddFreshnessHint(payload, reader);
            }
            adjustments.ApplyTo(payload);
            return CreateToolResult(id, summary, payload);
        });
    }

    private JsonObject BuildSymbolHotspotsQueryContext(
        int limit,
        string? kind,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePaths,
        bool excludeTests,
        string groupBy,
        string groupingUnit,
        string countKind,
        string limitAppliesTo)
    {
        var queryContext = new JsonObject
        {
            ["limit"] = limit,
        };
        QueryCommandRunner.AddHotspotsGroupingQueryContextFields(queryContext, groupBy, groupingUnit, countKind, limitAppliesTo);
        if (kind != null)
            queryContext["kind"] = kind;
        if (lang != null)
            queryContext["lang"] = lang;
        if (pathPatterns is { Count: > 0 })
            queryContext["path"] = JsonSerializer.SerializeToNode(pathPatterns, _jsonOptions);
        if (excludePaths is { Count: > 0 })
            queryContext["exclude_path"] = JsonSerializer.SerializeToNode(excludePaths, _jsonOptions);
        if (excludeTests)
            queryContext["exclude_tests"] = true;
        return queryContext;
    }


}
