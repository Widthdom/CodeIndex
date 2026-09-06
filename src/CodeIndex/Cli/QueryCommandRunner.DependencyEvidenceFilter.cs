using System.Text.Json.Nodes;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal static JsonObject BuildDependencyEvidenceFilterJson(DependencyEvidenceFilter filter)
        => new()
        {
            ["resolution_states"] = new JsonArray(filter.Resolutions.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["reference_kinds"] = new JsonArray(filter.Kinds.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["combination"] = "or_within_dimension_and_between_dimensions",
            ["applied_before"] = "aggregation_ranking_and_graph_budget",
            ["resolution_basis"] = "current_persisted_reference_identity",
            ["unavailable_resolution"] = "missing_stale_null_or_unknown",
            ["kind_mapping"] = "canonical_subscribe_includes_raw_event_variants",
            ["whole_program_completeness_implied"] = false,
        };
}
