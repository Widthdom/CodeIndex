using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    internal static Action<string>? McpIndexInputSnapshotBarrierForTesting { get; set; }

    private async Task<JsonNode> ExecuteIndexAsync(JsonNode? id, JsonNode? args, JsonNode? progressToken = null)
    {
        try
        {
            return await ExecuteIndexCoreAsync(id, args, progressToken).ConfigureAwait(false);
        }
        catch (McpIndexAuthorizationException ex)
        {
            return CreateIndexAuthorizationErrorResponse(id, ex);
        }
        catch (AggregateException ex) when (TryExtractIndexAuthorizationException(ex, out var authorizationException))
        {
            return CreateIndexAuthorizationErrorResponse(id, authorizationException);
        }
    }

    private JsonNode CreateIndexAuthorizationErrorResponse(
        JsonNode? id,
        McpIndexAuthorizationException exception)
        => CreateToolErrorResponse(
            id,
            "MCP index authorization changed after validation; indexing stopped.",
            category: McpErrorEnvelope.CategoryPermissionDenied,
            suggestion: "Restore a stable directory mapping within the current working directory and MCP client roots, then retry.",
            retrySafe: true,
            extraData: new JsonObject
            {
                ["authorization_failure_reason"] = exception.Reason,
                ["checked_root_identity"] = exception.CheckedRootIdentity,
            });

    private static bool TryExtractIndexAuthorizationException(
        AggregateException exception,
        out McpIndexAuthorizationException authorizationException)
    {
        foreach (var innerException in exception.Flatten().InnerExceptions)
        {
            if (innerException is McpIndexAuthorizationException matched)
            {
                authorizationException = matched;
                return true;
            }
        }

        authorizationException = null!;
        return false;
    }




}
