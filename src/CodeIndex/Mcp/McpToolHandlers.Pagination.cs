using System.Globalization;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    internal const int MaxMcpQueryCursorCharacters = 16_384;

    private sealed record McpQueryCursor(
        int Offset,
        string QueryFingerprint,
        string GenerationFingerprint);

    private static bool TryParseMcpQueryCursor(string cursor, out McpQueryCursor? parsed)
    {
        parsed = null;
        if (cursor.Length > MaxMcpQueryCursorCharacters
            || !JsonEnvelopeWrapper.TryParseResponseCursor(
                cursor,
                out var offset,
                out var queryFingerprint,
                out var generationFingerprint,
                out var resumePath,
                out var resumeLine)
            || queryFingerprint is null
            || generationFingerprint is null
            || resumePath is not null
            || resumeLine.HasValue)
        {
            return false;
        }

        parsed = new McpQueryCursor(offset, queryFingerprint, generationFingerprint);
        return true;
    }

    private static string BuildMcpQueryFingerprint(
        string toolName,
        int pageLimit,
        string format,
        IEnumerable<KeyValuePair<string, string?>> scalarComponents,
        params (string Name, IReadOnlyList<string>? Values, bool PreserveOrder)[] listComponents)
    {
        var components = new List<string?>
        {
            "mcp-query:v1",
            "tool:" + toolName,
            "page-limit:" + pageLimit.ToString(CultureInfo.InvariantCulture),
            "format:" + format,
        };
        components.AddRange(scalarComponents
            .OrderBy(component => component.Key, StringComparer.Ordinal)
            .Select(component => component.Key + ":" + (component.Value ?? string.Empty)));
        foreach (var (name, values, preserveOrder) in listComponents.OrderBy(component => component.Name, StringComparer.Ordinal))
        {
            var indexedValues = (values ?? []).Select((value, index) => (value, index));
            if (!preserveOrder)
                indexedValues = indexedValues.OrderBy(item => item.value, StringComparer.Ordinal);
            components.AddRange(indexedValues.Select(item =>
                name + ":" + (preserveOrder
                    ? item.index.ToString(CultureInfo.InvariantCulture) + ":"
                    : string.Empty) + item.value));
        }

        return InspectGraphCursorCodec.BuildQueryFingerprint(components);
    }

    private static (string Fingerprint, string? StableAt) BuildMcpGenerationFingerprint(
        DbReader reader,
        bool includeFoldState = false,
        bool includeIssueState = false)
    {
        if (!includeFoldState && !includeIssueState)
            return InspectGraphCursorCodec.BuildGenerationFingerprint(reader);

        var generation = reader.GetPaginationGeneration();
        var components = new List<string>
        {
            "mcp-generation:v1",
            generation.Identity,
        };
        if (includeFoldState)
            components.Add(reader.GetFoldPaginationGenerationIdentity());
        if (includeIssueState)
            components.Add(reader.GetIssuePaginationGenerationIdentity());
        return (
            InspectGraphCursorCodec.BuildQueryFingerprint(components),
            generation.StableAt);
    }

    private JsonObject CreateMcpCursorError(
        JsonNode? id,
        string toolName,
        string errorCode,
        string message,
        bool stale)
        => CreateToolErrorResponse(
            id,
            message,
            category: stale ? McpErrorEnvelope.CategoryIndexStale : McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: stale
                ? $"The index changed after this {toolName} cursor was issued. Restart pagination without cursor."
                : $"Use the exact next_cursor returned by the previous {toolName} page with unchanged filters, format, and limit.",
            retrySafe: stale,
            extraData: new JsonObject
            {
                ["error_code"] = errorCode,
                ["tool"] = toolName,
                ["restart_required"] = true,
                ["max_cursor_characters"] = MaxMcpQueryCursorCharacters,
            });

    private JsonObject? ValidateMcpQueryCursor(
        JsonNode? id,
        string toolName,
        McpQueryCursor? cursor,
        string queryFingerprint,
        string generationFingerprint,
        int totalCount)
    {
        if (cursor is null)
            return null;
        if (!string.Equals(cursor.QueryFingerprint, queryFingerprint, StringComparison.Ordinal))
        {
            return CreateMcpCursorError(
                id,
                toolName,
                "cursor_query_mismatch",
                $"cursor does not match this {toolName} query, filters, format, or limit.",
                stale: false);
        }
        if (!string.Equals(cursor.GenerationFingerprint, generationFingerprint, StringComparison.Ordinal))
        {
            return CreateMcpCursorError(
                id,
                toolName,
                "cursor_stale",
                $"cursor is stale because the {toolName} index generation changed.",
                stale: true);
        }
        if (cursor.Offset > totalCount)
        {
            return CreateMcpCursorError(
                id,
                toolName,
                "cursor_offset_out_of_range",
                $"cursor offset {cursor.Offset.ToString(CultureInfo.InvariantCulture)} exceeds the current {toolName} result count {totalCount.ToString(CultureInfo.InvariantCulture)}.",
                stale: false);
        }

        return null;
    }

    private static void AddMcpPaginationEnvelope(
        JsonObject payload,
        int totalCount,
        int returnedCount,
        int offset,
        int pageLimit,
        string queryFingerprint,
        (string Fingerprint, string? StableAt) generation)
    {
        var nextOffset = checked(offset + returnedCount);
        var hasMore = nextOffset < totalCount;
        payload["returned_count"] = returnedCount;
        payload["total_count"] = totalCount;
        payload["total_count_authoritative"] = true;
        payload["omitted_count"] = Math.Max(0, totalCount - returnedCount);
        payload["remaining_count"] = Math.Max(0, totalCount - nextOffset);
        payload["cursor_offset"] = offset;
        payload["page_limit"] = pageLimit;
        payload["has_more"] = hasMore;
        payload["truncated"] = hasMore;
        payload["more_available"] = hasMore;
        payload["result_stable_at"] = generation.StableAt;
        payload["next_cursor"] = hasMore
            ? JsonEnvelopeWrapper.FormatResponseCursor(
                nextOffset,
                queryFingerprint,
                generation.Fingerprint)
            : null;
    }
}
