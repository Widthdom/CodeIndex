using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private JsonNode ExecuteImpactAnalysis(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var maxHopsNode = args?["maxHops"];
        var deprecatedMaxDepthNode = args?["maxDepth"];
        var usedDeprecatedMaxDepth = deprecatedMaxDepthNode != null;
        var adjustments = new ArgumentAdjustmentCollector();
        var maxDepthRequested = ReadOptionalIntArgument(args, "maxHops") ?? ReadOptionalIntArgument(args, "maxDepth") ?? 5;
        var maxDepth = Math.Clamp(maxDepthRequested, 0, MaxImpactDepth);
        string? maxDepthClampWarning = null;
        string? maxDepthDeprecationWarning = null;
        if (usedDeprecatedMaxDepth)
        {
            maxDepthDeprecationWarning = "maxDepth is deprecated for impact_analysis; use maxHops instead.";
            adjustments.AddWarning(maxDepthDeprecationWarning);
        }
        if (maxDepthRequested != maxDepth)
        {
            maxDepthClampWarning = $"maxHops was clamped from {maxDepthRequested} to {maxDepth} (server cap is [0, {MaxImpactDepth}]).";
            adjustments.AddClamped("maxHops", maxDepthRequested, maxDepth, 0, MaxImpactDepth);
        }
        var limit = ReadLimit(args, QueryCommandRunner.DefaultImpactLimit, adjustments);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var withPaths = args?["withPaths"]?.GetValue<bool>() ?? false;
        var includeMemberReads = args?["includeMemberReads"]?.GetValue<bool>() ?? false;
        var countOnly = ReadCountOnly(args);

        return WithDbReader(id, args, reader =>
        {
            var analysis = reader.AnalyzeImpact(query, maxDepth, limit, lang, pathPatterns, excludePaths, excludeTests, withPaths, includeMemberReads: includeMemberReads);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignal(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                DbReader.IsSqlLanguage(lang)
                    || DbReader.ContainsSqlLanguage(analysis.Definitions.Select(definition => definition.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callers.Select(caller => caller.Lang))
                    || reader.AnyFilePathHasLanguage(analysis.FileImpacts.SelectMany(impact => new[] { impact.SourcePath, impact.TargetPath }), "sql"));
            var confirmedCount = analysis.Callers.Count;
            var confirmedFileCount = analysis.Callers.Select(r => r.Path).Distinct().Count();
            var hintCount = analysis.FileImpacts.Count;
            var hintFileCount = analysis.FileImpacts.Select(r => r.SourcePath).Distinct().Count();
            var hasHeuristicHints = analysis.ImpactMode == "file_dependency_hints" && hintCount > 0;
            var count = hasHeuristicHints ? hintCount : confirmedCount;
            var fileCount = hasHeuristicHints ? hintFileCount : confirmedFileCount;
            var maxActualDepth = analysis.Callers.Count > 0 ? analysis.Callers.Max(r => r.Depth) : 0;
            if (countOnly)
            {
                var topFiles = hasHeuristicHints
                    ? BuildTopFileHistogram(analysis.FileImpacts, impact => impact.SourcePath)
                    : BuildTopFileHistogram(analysis.Callers, caller => caller.Path);
                var countOnlyPayload = new JsonObject
                {
                    ["query"] = query,
                    ["resolved_name"] = analysis.ResolvedName,
                    ["count_only"] = true,
                    ["count"] = count,
                    ["file_count"] = fileCount,
                    ["confirmed_count"] = confirmedCount,
                    ["confirmed_file_count"] = confirmedFileCount,
                    ["hint_count"] = hintCount,
                    ["hint_file_count"] = hintFileCount,
                    ["max_hops"] = maxDepth,
                    ["actual_depth"] = maxActualDepth,
                    ["truncated"] = analysis.Truncated,
                    ["total"] = analysis.CountIsAuthoritative ? JsonValue.Create(count) : null,
                    ["termination_reason"] = analysis.TerminationReason,
                    ["impact_mode"] = analysis.ImpactMode,
                    ["heuristic"] = analysis.Heuristic,
                    ["includeMemberReads"] = includeMemberReads,
                    ["top_files"] = topFiles,
                    ["results"] = new JsonArray(),
                };
                AddImpactTraversalRootFields(countOnlyPayload, analysis);
                AddImpactFailureFields(countOnlyPayload, analysis);
                AddSqlGraphContractSignal(countOnlyPayload, sqlGraphSignal);
                AddReferenceGraphCompletenessSignal(
                    countOnlyPayload,
                    reader,
                    lang,
                    pathPatterns,
                    excludePaths,
                    excludeTests);
                adjustments.ApplyTo(countOnlyPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(count, "impact result")}.", countOnlyPayload);
            }

            var payload = new JsonObject
            {
                ["query"] = query,
                ["resolved_name"] = analysis.ResolvedName,
                ["count"] = count,
                ["file_count"] = fileCount,
                ["confirmed_count"] = confirmedCount,
                ["confirmed_file_count"] = confirmedFileCount,
                ["hint_count"] = hintCount,
                ["hint_file_count"] = hintFileCount,
                ["max_hops"] = maxDepth,
                ["max_hops_requested"] = maxDepthRequested,
                ["max_depth"] = maxDepth,
                ["max_depth_requested"] = maxDepthRequested,
                ["actual_depth"] = maxActualDepth,
                ["truncated"] = analysis.Truncated,
                ["termination_reason"] = analysis.TerminationReason,
                ["cycle_detected"] = analysis.CycleDetected,
                ["impact_mode"] = analysis.ImpactMode,
                ["heuristic"] = analysis.Heuristic,
                ["includeMemberReads"] = includeMemberReads,
                ["callers"] = ToJsonArray(analysis.Callers),
                ["file_impacts"] = ToJsonArray(analysis.FileImpacts),
                ["definition_count"] = analysis.DefinitionCount,
                ["definition_file_count"] = analysis.DefinitionFileCount,
                ["has_multiple_definitions"] = analysis.HasMultipleDefinitions,
                ["has_class_like_definitions"] = analysis.HasClassLikeDefinitions,
                ["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles,
                ["definitions"] = ToJsonArray(analysis.Definitions),
                ["graph_table_available"] = analysis.GraphTableAvailable,
            };
            AddImpactTraversalRootFields(payload, analysis);
            if (analysis.TruncatedReason != null)
                payload["truncated_reason"] = analysis.TruncatedReason;
            if (analysis.Cycles is { Count: > 0 })
                payload["cycles"] = ToJsonArray(analysis.Cycles);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            AddReferenceGraphCompletenessSignal(
                payload,
                reader,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests);
            if (analysis.ZeroResultReason != null)
                payload["zero_result_reason"] = analysis.ZeroResultReason;
            AddImpactFailureFields(payload, analysis);
            if (analysis.Suggestion != null)
                payload["suggestion"] = analysis.Suggestion;

            // Summary tail differs by truncated_reason so retry advice is actionable: user_limit
            // is solvable by raising --limit, safety_cap is not. Issue #1533.
            // 切り捨て理由ごとに retry 助言を分岐 (user_limit は --limit 緩和で解消、safety_cap は不可) (#1533)。
            string truncatedTail;
            if (!analysis.Truncated)
                truncatedTail = "";
            else if (analysis.TruncatedReason == ImpactTruncatedReasons.SafetyCap)
                truncatedTail = " Results truncated by internal safety cap (graph likely pathological); raising limit will not help.";
            else
                truncatedTail = " Results truncated — increase limit for more.";
            var cycleTail = analysis.CycleDetected
                ? $" Cycle detected ({ConsoleUi.Counted(analysis.Cycles?.Count ?? 0, "cycle")})."
                : "";

            var summary = analysis.ImpactMode switch
            {
                "file_dependency_hints" => $"No symbol-level callers found for '{analysis.ResolvedName}'; found {ConsoleUi.Counted(hintCount, "possible file-level dependent")} across {ConsoleUi.Counted(hintFileCount, "file")}. These hints are heuristic only."
                    + truncatedTail + cycleTail,
                _ when count > 0 => $"Found {ConsoleUi.Counted(count, "transitive caller")} across {ConsoleUi.Counted(fileCount, "file")} (depth {maxActualDepth})."
                    + truncatedTail + cycleTail,
                _ => "No impact found." + cycleTail,
            };
            if (maxDepthClampWarning != null)
                summary += $" Warning: {maxDepthClampWarning}";
            if (maxDepthDeprecationWarning != null)
                summary += $" Warning: {maxDepthDeprecationWarning}";

            if (count == 0)
            {
                AddSymbolRecoveryHint(payload, query, "impact_analysis", lang, null, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
                var graphReason = ReferenceExtractor.BuildGraphSupportReason(lang, lang != null ? reader.SupportsReferenceLanguage(lang) : null);
                if (graphReason != null)
                    payload["graph_support_reason"] = graphReason;
                if (!analysis.GraphTableAvailable)
                    payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
            }
            else if (analysis.Heuristic)
                payload["note"] = "file_impacts are heuristic hints only; the current graph does not record resolved target file/type for each call.";
            adjustments.ApplyTo(payload);
            return CreateToolResult(id, summary, payload);
        });
    }

    private static void AddImpactTraversalRootFields(JsonObject payload, ImpactAnalysisResult analysis)
    {
        payload["traversal_root_scope"] = analysis.TraversalRootScope;
        if (analysis.TraversalPartialFamilyId == null)
            return;

        payload["traversal_partial_family_id"] = analysis.TraversalPartialFamilyId;
        payload["partial_family_member_count"] = analysis.PartialFamilyMemberCount;
        payload["partial_family_member_root_count"] = analysis.PartialFamilyMemberRootCount;
        payload["partial_family_member_root_limit"] = analysis.PartialFamilyMemberRootLimit;
        payload["partial_family_member_root_truncated"] = analysis.PartialFamilyMemberRootTruncated;
        payload["partial_family_member_root_omitted"] = analysis.PartialFamilyMemberRootOmitted;
    }


}
