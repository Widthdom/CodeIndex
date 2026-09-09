using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal static string BindDependencyCycleGroupingGeneration(string fingerprint, QueryCommandOptions options, DbReader reader)
        => !options.GroupDependencyPartialTypes ? fingerprint : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            fingerprint + ":" + reader.GetPaginationGeneration().Identity + ":" + reader.DependencyCycleGroupingReady)));

    private static JsonObject BuildDependencyCycleGroupingJson(
        DbReader reader,
        IReadOnlyList<FileDependencyResult> edges,
        IReadOnlyList<FileDependencyResult> internalEdges,
        IReadOnlyList<DependencyCycleComponent> components,
        DependencyCycleComponent? largest)
    {
        var applied = reader.DependencyCycleGroupingReady;
        var payload = new JsonObject
        {
            ["requested_mode"] = "csharp_partial_type",
            ["applied"] = applied,
            ["reason"] = applied ? "authoritative_csharp_type_metadata" : "raw_file_fallback_metadata_unavailable",
        };
        if (!applied)
            return payload;
        const int mappingLimit = 40;
        var allNodes = components.SelectMany(static component => component.Nodes)
            .Concat(largest?.Nodes ?? []).Concat(internalEdges.Select(static edge => edge.SourcePath))
            .Distinct(StringComparer.Ordinal).ToList();
        payload["node_mappings"] = new JsonArray(allNodes.Take(mappingLimit)
            .Select(node => (JsonNode?)reader.DescribeDependencyCycleNode(node)).ToArray());
        payload["mapping_node_count"] = allNodes.Count;
        payload["mapping_node_limit"] = mappingLimit;
        payload["mapping_nodes_truncated"] = allNodes.Count > mappingLimit;
        payload["mapping_nodes_omitted_count"] = Math.Max(0, allNodes.Count - mappingLimit);
        payload["typed_edge_count"] = edges.Count;
        payload["raw_candidate_edge_count"] = reader.DependencyCycleRawCandidateCount;
        payload["budget_scope"] = "raw_file_pairs_and_typed_edges_each_bounded_by_graph_budget";
        payload["intra_type_edge_count"] = internalEdges.Count;
        payload["intra_type_reference_count"] = internalEdges.Sum(static edge => (long)edge.ReferenceCount);
        payload["inter_node_edge_count"] = edges.Count - internalEdges.Count;
        payload["inter_node_reference_count"] = edges.Sum(static edge => (long)edge.ReferenceCount)
            - internalEdges.Sum(static edge => (long)edge.ReferenceCount);
        payload["count_scope"] = "bounded_typed_candidate_graph_before_scc";
        payload["unassigned_scope"] = "file_scope_preserves_non_type_or_non_authoritative_evidence";
        payload["internal_evidence"] = new JsonArray(internalEdges.Take(20).Select(edge => (JsonNode?)new JsonObject
        {
            ["node"] = edge.SourcePath,
            ["reference_count"] = edge.ReferenceCount,
            ["symbols"] = new JsonArray(edge.Symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5).Select(symbol => (JsonNode?)JsonValue.Create(symbol)).ToArray()),
        }).ToArray());
        payload["internal_evidence_limit"] = 20;
        payload["internal_evidence_omitted_count"] = Math.Max(0, internalEdges.Count - 20);
        return payload;
    }
}
