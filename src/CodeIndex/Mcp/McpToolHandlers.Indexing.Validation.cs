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
    private sealed record IndexAuthorizationResult(
        string CurrentWorkingDirectory,
        McpPathBoundary.IndexRootAuthorization? Authorization,
        JsonNode? ErrorResponse);

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

    private async Task<IndexAuthorizationResult> CaptureIndexAuthorizationAsync(
        JsonNode? id,
        string requestedProjectPath)
    {
        // Prevent path traversal — only allow indexing within current working directory
        // パストラバーサル防止 — カレントディレクトリ配下のみインデックスを許可
        var cwd = Path.GetFullPath(".");
        if (!McpPathBoundary.IsPathWithinDirectory(cwd, requestedProjectPath))
        {
            return new IndexAuthorizationResult(
                cwd,
                null,
                CreateToolErrorResponse(id, "Path must be within the current working directory"));
        }

        await RefreshClientRootsIfNeededAsync().ConfigureAwait(false);
        if (!IsPathWithinClientRoots(requestedProjectPath))
        {
            return new IndexAuthorizationResult(
                cwd,
                null,
                CreateToolErrorResponse(id, "Path must be within an MCP client root"));
        }

        if (!McpPathBoundary.TryCaptureIndexRoot(
                requestedProjectPath,
                path => IsIndexPathAuthorized(cwd, path),
                McpIndexEntryOpenBoundaryForTesting,
                McpIndexDirectoryEnumerationBoundaryForTesting,
                McpIndexDirectoryEnumerationCompletedForTesting,
                out var authorization,
                out var authorizationError))
        {
            return new IndexAuthorizationResult(
                cwd,
                null,
                CreateToolErrorResponse(id, authorizationError!));
        }

        return new IndexAuthorizationResult(cwd, authorization, null);
    }

    private bool IsIndexPathAuthorized(string cwd, string path)
        => McpPathBoundary.IsPathWithinDirectory(cwd, path) && IsPathWithinClientRoots(path);

    private JsonNode CreateUnsupportedIndexModeResponse(
        JsonNode? id,
        McpIndexRequestOptions indexOptions,
        JsonArray unsupportedModes,
        string checkedRootIdentity)
    {
        var unsupportedData = new JsonObject
        {
            ["unsupported_modes"] = unsupportedModes,
            ["index_options"] = indexOptions.OptionsPayload,
            ["index_started"] = false,
            ["checked_root_identity"] = checkedRootIdentity,
        };
        return CreateToolErrorResponse(
            id,
            "MCP index does not support the requested scoped or watch indexing mode; no indexing started.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Use dryRun:true to inspect the plan, remove unsupported scope/watch arguments, or run the equivalent cdidx index command in the CLI.",
            retrySafe: false,
            extraData: unsupportedData);
    }
}
