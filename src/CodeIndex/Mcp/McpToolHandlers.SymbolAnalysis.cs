using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private JsonNode ExecuteUnusedSymbols(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultImpactLimit, adjustments);
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var bucket = args?["bucket"]?.GetValue<string>()?.ToLowerInvariant();
        var minConfidence = args?["minConfidence"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var visibilityFilters = ReadStringOrCommaSeparatedList(args, "visibility");
        var excludeVisibilityFilters = ReadStringOrCommaSeparatedList(args, "excludeVisibility");
        var byBucket = args?["byBucket"]?.GetValue<bool>() ?? false;
        if (bucket != null && !QueryCommandRunner.IsKnownUnusedBucket(bucket))
            return CreateToolErrorResponse(id, $"Invalid bucket '{bucket}'. Use one of: {string.Join(", ", QueryCommandRunner.OrderedUnusedBuckets)}.");
        if (minConfidence != null && !QueryCommandRunner.IsKnownUnusedConfidence(minConfidence))
            return CreateToolErrorResponse(id, $"Invalid minConfidence '{minConfidence}'. Use one of: medium, low.");

        return WithDbReader(id, args, reader =>
        {
            // Add graph-support metadata for AI trust decisions
            // AI の信頼判断のためにグラフ対応メタデータを追加
            bool? graphSupported = lang != null ? reader.SupportsReferenceLanguage(lang) : null;
            var graphSupportReason = ReferenceExtractor.BuildGraphSupportReason(lang, graphSupported);
            var results = reader.GetUnusedSymbols(
                limit,
                kind,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests,
                visibilityFilters,
                excludeVisibilityFilters,
                bucketFilter: bucket,
                minConfidence: minConfidence);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            var hdlGraphSignal = reader.GetHdlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            var zeroResultSqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePaths, excludeTests));
            var sqlGraphSignal = results.Count == 0
                ? zeroResultSqlGraphSignal
                : QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                    baseSqlGraphSignal,
                    results.Select(result => result.Lang),
                    lang);
            var bucketCounts = QueryCommandRunner.BuildUnusedBucketCounts(results);
            var contractDomainCounts = QueryCommandRunner.BuildUnusedContractDomainCounts(results);
            var payload = new JsonObject
            {
                ["count"] = results.Count,
                ["graph_supported"] = graphSupported,
                ["graph_support_reason"] = graphSupportReason,
                ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(bucketCounts, _jsonOptions),
                ["returned_contract_domain_counts"] = JsonSerializer.SerializeToNode(contractDomainCounts, _jsonOptions),
                ["summary"] = QueryCommandRunner.BuildUnusedSummaryJson(results, _jsonOptions),
                ["bucket_taxonomy"] = QueryCommandRunner.BuildUnusedBucketTaxonomyJson(),
                ["symbols"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            AddVisibilityFilterEcho(payload, visibilityFilters, excludeVisibilityFilters);
            payload["byBucket"] = byBucket;
            if (byBucket)
                payload["symbols_by_bucket"] = BuildUnusedSymbolsByBucket(results);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            AddHdlGraphContractSignal(payload, hdlGraphSignal);
            var summary = results.Count > 0
                ? $"Found {ConsoleUi.Counted(results.Count, "potentially unused symbol")} across {ConsoleUi.Counted(bucketCounts.Count, "returned bucket")} and {ConsoleUi.Counted(contractDomainCounts.Count, "contract domain")}. Private hits are ranked ahead of exported/config suspects, but not labeled high-confidence from indexed refs alone. Note: name-based matching — same-named symbols in different contexts may mask true unused symbols."
                : "No unused symbols found.";
            if (graphSupported == false)
                summary += $" Warning: '{lang}' does not support reference extraction. Unused results are unavailable for this language.";
            if (!reader._hasReferencesTable)
            {
                payload["graph_table_available"] = false;
                payload["degraded"] = true;
                payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                summary += " Warning: symbol_references table is missing in this index; zero-result unused output is degraded, not authoritative.";
            }
            if (results.Count == 0)
            {
                AddRecoveryHint(
                    payload,
                    "no_results",
                    "unused_symbols returned no rows; verify graph readiness and loosen kind/lang/path filters before treating this as authoritative.",
                    "status",
                    new JsonObject());
                AddFreshnessHint(payload, reader);
            }
            adjustments.ApplyTo(payload);
            return CreateToolResult(id, summary, payload);
        });
    }


}
