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

    private JsonNode ExecuteDeps(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultImpactLimit, adjustments);
        var requestedGraphBudget = ReadOptionalIntArgument(args, "graphBudget");
        var graphBudget = Math.Clamp(
            requestedGraphBudget ?? QueryCommandRunner.DefaultDependencyCycleGraphBudget,
            1,
            QueryCommandRunner.MaxDependencyCycleGraphBudget);
        if (requestedGraphBudget.HasValue && requestedGraphBudget.Value != graphBudget)
            adjustments.AddClamped("graphBudget", requestedGraphBudget.Value, graphBudget, 1, QueryCommandRunner.MaxDependencyCycleGraphBudget);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var includeGenerated = args?["includeGenerated"]?.GetValue<bool>() ?? false;
        var reverse = args?["reverse"]?.GetValue<bool>() ?? false;
        var cyclesOnly = args?["cycles"]?.GetValue<bool>() ?? false;
        var format = args?["format"]?.GetValue<string>()?.ToLowerInvariant() ?? "edgelist";
        var cursorValue = args?["cursor"]?.GetValue<string>();
        if (requestedGraphBudget.HasValue && !cyclesOnly)
            return CreateToolErrorResponse(id, "'graphBudget' requires 'cycles=true'.");
        if (cursorValue != null && !cyclesOnly)
            return CreateToolErrorResponse(id, "'cursor' requires 'cycles=true'.");
        if (cursorValue != null && !QueryCommandRunner.TryParseDependencyCycleCursor(cursorValue, out _))
            return CreateToolErrorResponse(id, "'cursor' must be an opaque dependency-cycle next_cursor returned by deps.");

        var cursorOptions = new QueryCommandOptions
        {
            Lang = lang,
            PathPatterns = pathPatterns?.ToList() ?? [],
            ExcludePaths = excludePaths,
            ExcludeTests = excludeTests,
            IncludeGenerated = includeGenerated,
            DependencyCycleGraphBudget = graphBudget,
        };
        var cursorBaseFingerprint = QueryCommandRunner.BuildDependencyCycleCursorFingerprint(cursorOptions, reverse);
        var cursor = cursorValue == null
            ? (DependencyCycleCursor?)null
            : QueryCommandRunner.TryParseDependencyCycleCursor(cursorValue, out var parsedCursor)
                ? parsedCursor
                : null;
        var pageOffset = cursor?.Offset ?? 0;

        return WithDbReader(id, args, reader =>
        {
            var cycleCandidateRowCount = 0;
            var results = cyclesOnly
                ? reader.GetFileDependencyCycleCandidates(
                    checked(graphBudget + 1),
                    out cycleCandidateRowCount,
                    lang,
                    pathPatterns,
                    excludePaths,
                    excludeTests,
                    reverse,
                    reader.Cancellation)
                : reader.GetFileDependencies(limit, lang, pathPatterns, excludePaths, excludeTests, reverse);
            var cycleCandidates = cyclesOnly ? results.Take(graphBudget).ToList() : results;
            var cursorFingerprint = QueryCommandRunner.BuildDependencyCycleGraphFingerprint(
                cursorBaseFingerprint,
                cycleCandidates,
                cycleCandidateRowCount);
            if (cursor is { } suppliedCursor
                && !string.Equals(suppliedCursor.Fingerprint, cursorFingerprint, StringComparison.Ordinal))
                return CreateToolErrorResponse(id, "'cursor' does not match the current deps filters, graphBudget, or indexed graph.");
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests);
            var cycleAnalysis = cyclesOnly
                ? QueryCommandRunner.AnalyzeDependencyCycles(
                    cycleCandidates,
                    graphBudget,
                    cycleCandidateRowCount,
                    limit,
                    pageOffset,
                    cursorFingerprint,
                    reader.Cancellation)
                : null;
            if (cursor.HasValue && cycleAnalysis != null && pageOffset >= cycleAnalysis.TotalCycleCount)
                return CreateToolErrorResponse(id, "'cursor' points beyond the available dependency-cycle result set.");
            var cycles = cycleAnalysis?.Cycles ?? [];
            var outputEdges = cycleAnalysis?.Edges ?? results;
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
                payload["cycles"] = QueryCommandRunner.BuildDependencyCyclesJson(cycleAnalysis!.Components, cycleAnalysis.PageOffset);
            else if (format == "json-graph")
                payload["graph"] = BuildJsonGraphPayload(outputEdges);
            else
                payload["edges"] = JsonSerializer.SerializeToNode(outputEdges, _jsonOptions);
            if (cyclesOnly)
                QueryCommandRunner.AddDependencyCycleAnalysisJsonFields(payload, cycleAnalysis!, mcpArguments: true);
            payload["format"] = format;
            payload["includeGenerated"] = includeGenerated;
            payload["generated_code_filter_supported"] = true;
            payload["generated_code_scope"] = "source_and_target_files";
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            AddReferenceGraphCompletenessSignal(
                payload,
                reader,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests);
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
