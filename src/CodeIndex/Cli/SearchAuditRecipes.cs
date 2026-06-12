using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodeIndex.Cli;

internal static class SearchAuditRecipes
{
    internal const string RecipePathsEnvironmentVariable = "CDIDX_SEARCH_RECIPE_PATHS";
    private const int MaxRecipeSourceFiles = 8;
    private const long MaxRecipeSourceBytes = 128 * 1024;
    private const int MaxExternalRecipesPerFile = 32;
    private const int MaxExternalQueriesPerRecipe = 32;
    private const int MaxExternalNameLength = 80;
    private const int MaxExternalDescriptionLength = 512;
    private const int MaxExternalFalsePositiveGuidanceLength = 512;
    private const int MaxExternalLabelCount = 16;
    private const int MaxExternalLabelLength = 64;
    private const int MaxRecipeDiagnosticCount = 64;
    private const int MaxRecipeDiagnosticLength = 512;

    private static readonly List<SearchAuditRecipe> BuiltInRecipes =
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

    internal static IReadOnlyList<SearchAuditRecipe> All => Load().Recipes;

    internal static SearchAuditRecipeRegistry Load()
    {
        var recipes = BuiltInRecipes.ToList();
        var diagnostics = new List<string>();
        var knownNames = new HashSet<string>(recipes.Select(recipe => recipe.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var sourcePath in ReadConfiguredRecipeSourcePaths(diagnostics))
        {
            if (!TryLoadExternalRecipes(sourcePath, diagnostics, out var externalRecipes))
                continue;

            foreach (var recipe in externalRecipes)
            {
                if (!knownNames.Add(recipe.Name))
                {
                    AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' defines duplicate recipe '{recipe.Name}'; keeping the first definition.");
                    continue;
                }

                recipes.Add(recipe);
            }
        }

        return new SearchAuditRecipeRegistry(recipes, diagnostics);
    }

    internal static bool TryGet(string name, out SearchAuditRecipe recipe)
    {
        var registry = Load();
        recipe = registry.Recipes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return recipe != null;
    }

    private static List<string> ReadConfiguredRecipeSourcePaths(List<string> diagnostics)
    {
        var raw = Environment.GetEnvironmentVariable(RecipePathsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var paths = new List<string>();
        foreach (var part in raw.Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (paths.Count >= MaxRecipeSourceFiles)
            {
                AddDiagnostic(diagnostics, $"{RecipePathsEnvironmentVariable} lists more than {MaxRecipeSourceFiles} recipe sources; extra entries are ignored.");
                break;
            }

            paths.Add(part);
        }

        return paths;
    }

    private static bool TryLoadExternalRecipes(string sourcePath, List<string> diagnostics, out List<SearchAuditRecipe> recipes)
    {
        recipes = [];
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' is not a valid path: {ex.Message}");
            return false;
        }

        if (!File.Exists(fullPath))
        {
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' does not exist.");
            return false;
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaxRecipeSourceBytes)
            {
                AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' is too large ({info.Length} bytes; max {MaxRecipeSourceBytes}).");
                return false;
            }

            var root = JsonNode.Parse(
                File.ReadAllText(fullPath),
                documentOptions: new JsonDocumentOptions { MaxDepth = 16 });
            var recipeArray = root as JsonArray ?? root?["recipes"] as JsonArray;
            if (recipeArray is null)
            {
                AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' must be a JSON array or an object with a 'recipes' array.");
                return false;
            }

            for (var i = 0; i < recipeArray.Count && i < MaxExternalRecipesPerFile; i++)
            {
                if (TryParseRecipe(recipeArray[i], sourcePath, i, diagnostics, out var recipe))
                    recipes.Add(recipe);
            }

            if (recipeArray.Count > MaxExternalRecipesPerFile)
                AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' has more than {MaxExternalRecipesPerFile} recipes; extra entries are ignored.");
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' could not be loaded: {ex.Message}");
            return false;
        }
    }

    private static bool TryParseRecipe(
        JsonNode? node,
        string sourcePath,
        int recipeIndex,
        List<string> diagnostics,
        out SearchAuditRecipe recipe)
    {
        recipe = null!;
        if (node is not JsonObject obj)
        {
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' recipe #{recipeIndex + 1} must be an object.");
            return false;
        }

        if (!TryReadRequiredString(obj, "name", MaxExternalNameLength, sourcePath, recipeIndex, diagnostics, out var name)
            || !TryReadRequiredString(obj, "description", MaxExternalDescriptionLength, sourcePath, recipeIndex, diagnostics, out var description))
        {
            return false;
        }

        if (obj["queries"] is not JsonArray queryArray)
        {
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' recipe '{name}' must include a 'queries' array.");
            return false;
        }

        var queries = new List<SearchAuditRecipeQuery>();
        for (var i = 0; i < queryArray.Count && i < MaxExternalQueriesPerRecipe; i++)
        {
            if (TryParseRecipeQuery(queryArray[i], sourcePath, name, i, diagnostics, out var query))
                queries.Add(query);
        }

        if (queryArray.Count > MaxExternalQueriesPerRecipe)
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' recipe '{name}' has more than {MaxExternalQueriesPerRecipe} queries; extra entries are ignored.");
        if (queries.Count == 0)
        {
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' recipe '{name}' has no valid queries and was ignored.");
            return false;
        }

        recipe = new SearchAuditRecipe(name, description, queries);
        return true;
    }

    private static bool TryParseRecipeQuery(
        JsonNode? node,
        string sourcePath,
        string recipeName,
        int queryIndex,
        List<string> diagnostics,
        out SearchAuditRecipeQuery query)
    {
        query = null!;
        if (node is not JsonObject obj)
        {
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' recipe '{recipeName}' query #{queryIndex + 1} must be an object.");
            return false;
        }

        if (!TryReadRequiredString(obj, "name", MaxExternalNameLength, sourcePath, queryIndex, diagnostics, out var name)
            || !TryReadRequiredString(obj, "query", QueryLimits.MaxQueryLength, sourcePath, queryIndex, diagnostics, out var queryText)
            || !TryReadRequiredString(obj, "description", MaxExternalDescriptionLength, sourcePath, queryIndex, diagnostics, out var description))
        {
            return false;
        }

        var labels = ReadLabels(obj, sourcePath, recipeName, name, diagnostics);
        var falsePositiveGuidance = TryReadString(obj["falsePositiveGuidance"] ?? obj["false_positive_guidance"], out var guidance)
            && !string.IsNullOrWhiteSpace(guidance)
            ? guidance.Trim()
            : "Review surrounding context before filing an issue.";
        if (falsePositiveGuidance.Length > MaxExternalFalsePositiveGuidanceLength)
            falsePositiveGuidance = falsePositiveGuidance[..MaxExternalFalsePositiveGuidanceLength].TrimEnd();
        var exactSubstring = TryReadBool(obj["exactSubstring"] ?? obj["exact_substring"], out var exactValue)
            ? exactValue
            : true;

        query = new SearchAuditRecipeQuery(name, queryText, description, labels, falsePositiveGuidance, exactSubstring);
        return true;
    }

    private static bool TryReadRequiredString(
        JsonObject obj,
        string propertyName,
        int maxLength,
        string sourcePath,
        int itemIndex,
        List<string> diagnostics,
        out string value)
    {
        value = string.Empty;
        if (!TryReadString(obj[propertyName], out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' item #{itemIndex + 1} must include a non-empty '{propertyName}' string.");
            return false;
        }

        value = raw.Trim();
        if (value.Length <= maxLength)
            return true;

        AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' item #{itemIndex + 1} field '{propertyName}' exceeds {maxLength} characters.");
        value = string.Empty;
        return false;
    }

    private static List<string> ReadLabels(
        JsonObject obj,
        string sourcePath,
        string recipeName,
        string queryName,
        List<string> diagnostics)
    {
        var labelsNode = obj["recommendedLabels"] ?? obj["recommended_labels"];
        if (labelsNode is null)
            return [];
        if (labelsNode is not JsonArray labelArray)
        {
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' recipe '{recipeName}' query '{queryName}' labels must be an array.");
            return [];
        }

        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < labelArray.Count && i < MaxExternalLabelCount; i++)
        {
            if (!TryReadString(labelArray[i], out var label) || string.IsNullOrWhiteSpace(label))
                continue;
            label = label.Trim();
            if (label.Length > MaxExternalLabelLength)
                continue;
            if (seen.Add(label))
                labels.Add(label);
        }

        if (labelArray.Count > MaxExternalLabelCount)
            AddDiagnostic(diagnostics, $"recipe source '{sourcePath}' recipe '{recipeName}' query '{queryName}' has more than {MaxExternalLabelCount} labels; extra entries are ignored.");
        return labels;
    }

    private static void AddDiagnostic(List<string> diagnostics, string message)
    {
        if (diagnostics.Count >= MaxRecipeDiagnosticCount)
        {
            if (diagnostics.Count == MaxRecipeDiagnosticCount)
                diagnostics.Add($"recipe source diagnostics were truncated after {MaxRecipeDiagnosticCount} entries.");
            return;
        }

        if (message.Length > MaxRecipeDiagnosticLength)
            message = message[..MaxRecipeDiagnosticLength].TrimEnd() + " ... [truncated]";
        diagnostics.Add(message);
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is null)
            return false;
        try
        {
            value = node.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryReadBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is null)
            return false;
        try
        {
            value = node.GetValue<bool>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed record SearchAuditRecipeRegistry(
    IReadOnlyList<SearchAuditRecipe> Recipes,
    IReadOnlyList<string> Diagnostics);

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

internal sealed record SearchNamedBatchRunJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("queries")] List<SearchNamedBatchQueryResultJsonResult> Queries);

internal sealed record SearchNamedBatchQueryResultJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("results")] List<CompactSearchResult> Results);

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
