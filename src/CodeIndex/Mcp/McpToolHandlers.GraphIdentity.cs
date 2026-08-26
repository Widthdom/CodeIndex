using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private static bool TryReadGraphSymbolRequest(
        JsonNode? args,
        string toolName,
        out string query,
        out string? selector,
        out string? error)
    {
        var queryNode = args?["query"];
        var selectorNode = args?["selector"];
        query = queryNode?.GetValue<string>()?.Trim() ?? string.Empty;
        selector = selectorNode?.GetValue<string>()?.Trim();
        error = null;
        if (queryNode != null && query.Length == 0)
        {
            error = "Parameter \"query\" cannot be empty or whitespace-only";
            return false;
        }
        if (selectorNode != null && string.IsNullOrWhiteSpace(selector))
        {
            error = "Parameter \"selector\" cannot be empty or whitespace-only";
            return false;
        }
        if (query.Length > 0 && !string.IsNullOrWhiteSpace(selector))
        {
            error = $"{toolName} accepts either query or selector, not both.";
            return false;
        }
        if (query.Length == 0 && string.IsNullOrWhiteSpace(selector))
        {
            error = "Missing required parameter: query";
            return false;
        }

        if (selector == null && SymbolSelector.TryParse(query, out _))
            selector = query;
        if (query.Length == 0)
            query = selector!;
        return true;
    }

    private JsonNode? TryResolveMcpGraphSelector(
        JsonNode? id,
        DbReader reader,
        string? selectorValue,
        out DefinitionResult? selectedDefinition)
    {
        selectedDefinition = null;
        if (selectorValue == null)
            return null;

        var resolution = reader.ResolveGraphSymbolSelector(selectorValue);
        if (resolution.Status == GraphSymbolSelectorStatus.Success)
        {
            selectedDefinition = resolution.Definition;
            return null;
        }

        return resolution.Status switch
        {
            GraphSymbolSelectorStatus.GenerationRequired => CreateToolErrorResponse(
                id,
                $"Symbol selector requires a generation fingerprint: {selectorValue}.",
                McpErrorEnvelope.CategoryInvalidArgument,
                "Rerun inspect and pass its complete id:<positive-integer>@g:<fingerprint> selector.",
                retrySafe: false,
                extraData: GraphSelectorErrorData("selector_generation_required")),
            GraphSymbolSelectorStatus.Stale => CreateToolErrorResponse(
                id,
                $"Symbol selector is stale or belongs to another database: {selectorValue}.",
                McpErrorEnvelope.CategoryIndexStale,
                "Rerun inspect against this database and use the current emitted selector.",
                retrySafe: true,
                extraData: GraphSelectorErrorData("selector_stale")),
            GraphSymbolSelectorStatus.NotFound => CreateToolErrorResponse(
                id,
                $"Symbol selector was not found in the active index: {selectorValue}.",
                McpErrorEnvelope.CategoryInvalidArgument,
                "Rerun inspect and use a selector emitted by the active database.",
                retrySafe: false,
                extraData: GraphSelectorErrorData("selector_not_found")),
            _ => CreateToolErrorResponse(
                id,
                $"Invalid symbol selector: {selectorValue}.",
                McpErrorEnvelope.CategoryInvalidArgument,
                "Pass a selector emitted by inspect in the form id:<positive-integer>@g:<fingerprint>.",
                retrySafe: false,
                extraData: GraphSelectorErrorData("selector_malformed")),
        };
    }

    private static JsonObject GraphSelectorErrorData(string errorCode)
        => new() { ["error_code"] = errorCode };

    private void AddMcpGraphIdentityFields(
        JsonObject payload,
        GraphQueryIdentityMetadata metadata)
    {
        if (!metadata.Applies)
            return;

        payload["identity_scoped"] = metadata.IdentityScoped;
        payload["identity_scope_reason"] = metadata.IdentityScopeReason;
        if (metadata.Selected != null)
            payload["selected_symbol"] = JsonSerializer.SerializeToNode(metadata.Selected, _jsonOptions);
        if (metadata.Candidates.Count == 0)
            return;

        payload["candidate_count"] = metadata.Candidates.Count;
        payload["candidates"] = new JsonArray(
            metadata.Candidates
                .Select(candidate => JsonSerializer.SerializeToNode(candidate, _jsonOptions))
                .ToArray());
        payload["candidates_truncated"] = metadata.CandidatesTruncated;
        payload["identity_warning"] = "This name matches multiple symbol identities; results aggregate them and are not identity-scoped. Pass one candidate selector to narrow the graph.";
    }
}
