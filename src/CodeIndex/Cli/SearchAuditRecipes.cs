using System.Text.Json.Serialization;

namespace CodeIndex.Cli;

internal static class SearchAuditRecipes
{
    private static readonly List<SearchAuditRecipe> Recipes =
    [
        new(
            "risky-code",
            "Reusable audit searches for risky code patterns that often need manual triage.",
            [
                new(
                    "unbounded-json-parse",
                    "JsonDocument.Parse",
                    "Find direct JSON parsing calls that may need input size limits or streaming alternatives.",
                    ["audit", "bug"],
                    "False positives include tests, deliberately bounded callers, and parsing of already-small generated payloads."),
                new(
                    "full-materialization",
                    "ReadToEnd",
                    "Find full stream/string materialization that may need bounded reads or incremental processing.",
                    ["audit", "performance"],
                    "False positives include bounded in-memory test fixtures and tiny diagnostic payloads."),
                new(
                    "max-value-probe",
                    "int.MaxValue",
                    "Find sentinel or unbounded limit probes that may hide huge allocation or traversal paths.",
                    ["audit", "bug"],
                    "False positives include defensive upper-bound constants that are never passed to allocation or query limits."),
                new(
                    "raw-diagnostic-echo",
                    "ex.Message",
                    "Find raw exception-message echoes that may need redaction before CLI, JSON, MCP, or GitHub output.",
                    ["audit", "security"],
                    "False positives include messages that are already sanitized by the surrounding writer."),
                new(
                    "cancellation-gap",
                    "CancellationToken.None",
                    "Find async or stream paths that may be ignoring caller cancellation.",
                    ["audit", "bug"],
                    "False positives include intentionally fire-and-forget work and APIs that have no meaningful caller cancellation token.")
            ])
    ];

    internal static IReadOnlyList<SearchAuditRecipe> All => Recipes;

    internal static bool TryGet(string name, out SearchAuditRecipe recipe)
    {
        recipe = Recipes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return recipe != null;
    }
}

internal sealed record SearchAuditRecipe(
    string Name,
    string Description,
    List<SearchAuditRecipeQuery> Queries)
{
    public List<string> RecommendedLabels =>
        Queries
            .SelectMany(query => query.RecommendedLabels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

internal sealed record SearchAuditRecipeQuery(
    string Name,
    string Query,
    string Description,
    List<string> RecommendedLabels,
    string FalsePositiveGuidance,
    bool ExactSubstring = true);

internal sealed record SearchRecipeListJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("recipes")] List<SearchRecipeListItemJsonResult> Recipes);

internal sealed record SearchRecipeListItemJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("recommended_labels")] List<string> RecommendedLabels,
    [property: JsonPropertyName("queries")] List<SearchRecipeQueryListItemJsonResult> Queries);

internal sealed record SearchRecipeQueryListItemJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("recommended_labels")] List<string> RecommendedLabels,
    [property: JsonPropertyName("false_positive_guidance")] string FalsePositiveGuidance,
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring);

internal sealed record SearchRecipeRunJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("recipe")] SearchRecipeListItemJsonResult Recipe,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("queries")] List<SearchRecipeQueryResultJsonResult> Queries);

internal sealed record SearchRecipeQueryResultJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("recommended_labels")] List<string> RecommendedLabels,
    [property: JsonPropertyName("false_positive_guidance")] string FalsePositiveGuidance,
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("results")] List<CompactSearchResult> Results);

internal sealed record SearchIssueDraftExportJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("recipe")] SearchRecipeListItemJsonResult Recipe,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("duplicate_preflight")] SuggestionIssueDraftPreflightSummaryJsonResult DuplicatePreflight,
    [property: JsonPropertyName("drafts")] List<SearchIssueDraftJsonResult> Drafts);

internal sealed record SearchIssueDraftJsonResult(
    [property: JsonPropertyName("draft_id")] string DraftId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("labels")] List<string> Labels,
    [property: JsonPropertyName("evidence_paths")] List<string> EvidencePaths,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("source")] SearchIssueDraftSourceJsonResult Source,
    [property: JsonPropertyName("duplicate_preflight")] SuggestionIssueDraftDuplicatePreflightJsonResult DuplicatePreflight);

internal sealed record SearchIssueDraftSourceJsonResult(
    [property: JsonPropertyName("recipe")] string Recipe,
    [property: JsonPropertyName("query_name")] string QueryName,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("false_positive_guidance")] string FalsePositiveGuidance,
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring,
    [property: JsonPropertyName("result_count")] int ResultCount);
