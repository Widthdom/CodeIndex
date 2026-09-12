using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private JsonNode WithSearchQueryErrors(JsonNode? id, Func<JsonNode> action,
        string? recipeName = null, string? recipeQueryName = null)
    {
        try { return action(); }
        catch (CodeIndexException ex) when (ex.Code == "same_symbol_scope_unavailable")
        {
            return CreateToolErrorResponse(id, ex.Message + " " + ex.Hint);
        }
        catch (SearchQueryLimitException)
        {
            return CreateToolErrorResponse(id, FormatLiteralSearchQueryLimitError());
        }
        catch (SearchGuardCandidateLimitException ex)
        {
            return CreateToolErrorResponse(id, recipeName is null
                ? FormatSearchGuardCandidateLimitError(ex)
                : FormatSearchRecipeGuardCandidateLimitError(recipeName, recipeQueryName!, ex));
        }
    }

    private static bool AddSemanticSearchCoverage(JsonObject payload, bool scanComplete, bool classificationComplete)
    {
        var authoritative = scanComplete && classificationComplete;
        payload["candidate_scan_complete"] = scanComplete;
        payload["origin_classification_complete"] = classificationComplete;
        payload["partial_result"] = !authoritative;
        payload["degraded"] = !authoritative;
        payload["total_count_authoritative"] = authoritative;
        if (!authoritative)
            payload["recovery_guidance"] = "Inspect unknown matches without semantic exclusions; narrow the path/query when candidate or pagination bounds are reached. Filtered absence is not authoritative.";
        return authoritative;
    }

    private static bool HasSemanticSearchArguments(JsonNode? args)
        => new[] { "origin", "excludeOrigin", "resultKind", "excludeComments", "excludeStrings", "excludeFixtures" }
            .Any(name => args?[name] is not null);

    private JsonNode? ReadSemanticSearchFilters(JsonNode? id, JsonNode? args, out FindSemanticFilters? filters)
    {
        filters = null;
        if (!HasSemanticSearchArguments(args))
            return null;
        var origins = new List<string>();
        var exclusions = new List<string>();
        var kinds = new List<string>();
        string? error = null;
        foreach (var value in ReadStringList(args, "origin"))
            QueryCommandRunner.AddSearchMatchOrigins("--origin", value, origins, message => error ??= message);
        foreach (var value in ReadStringList(args, "excludeOrigin"))
            QueryCommandRunner.AddSearchMatchOrigins("--exclude-origin", value, exclusions, message => error ??= message);
        foreach (var value in ReadStringList(args, "resultKind"))
            QueryCommandRunner.AddSearchResultKinds(value, kinds, message => error ??= message);
        if (error is not null)
            return CreateToolErrorResponse(id, error);
        var excludeComments = args?["excludeComments"]?.GetValue<bool>() ?? false;
        var excludeStrings = args?["excludeStrings"]?.GetValue<bool>() ?? false;
        var excludeFixtures = args?["excludeFixtures"]?.GetValue<bool>() ?? false;
        if (origins.Count == 0 && exclusions.Count == 0 && kinds.Count == 0
            && !excludeComments && !excludeStrings && !excludeFixtures)
            return null;
        filters = new(origins, exclusions, kinds,
            excludeComments, excludeStrings, excludeFixtures);
        return null;
    }

    private JsonNode ExecuteSemanticSearchPage(JsonNode? id, JsonNode? args, DbReader reader,
        QueryCommandOptions options, string format, string? cursorValue, ArgumentAdjustmentCollector adjustments)
    {
        if (options.CountOnly && cursorValue is not null)
            return CreateToolErrorResponse(id, "countOnly semantic search does not accept cursor; omit cursor to count the bounded candidate set.");
        McpQueryCursor? cursor = null;
        if (cursorValue is not null && !TryParseMcpQueryCursor(cursorValue, out cursor))
            return CreateMcpCursorError(id, "search", "cursor_malformed", "Invalid semantic search cursor.", stale: false);
        var fingerprint = BuildMcpQueryFingerprint("search-semantic", 0, format,
            (args?.AsObject() ?? new JsonObject())
                .Where(property => property.Key is not ("cursor" or "limit"))
                .Select(property => new KeyValuePair<string, string?>(property.Key, property.Value?.ToJsonString())));
        var generation = BuildMcpGenerationFingerprint(reader, includeFoldState: true);
        if (ValidateMcpQueryCursor(id, "search", cursor, fingerprint, generation.Fingerprint, MaxMcpPaginationOffset) is { } cursorError)
            return cursorError;
        var offset = cursor?.Offset ?? 0;
        var requested = options.CountOnly ? MaxMcpPaginationOffset + 1 : Math.Min(MaxMcpPaginationOffset + 1, offset + options.Limit + 1);
        var page = QueryCommandRunner.ReadSemanticSearchRows(reader, options, requested);
        var total = page.Rows.Count;
        var rows = options.CountOnly ? page.Rows : page.Rows.Skip(offset).Take(options.Limit).ToList();
        var payload = options.CountOnly
            ? BuildCountOnlyPayload(total, page.ScanComplete ? total : null, !page.ScanComplete, rows, row => row.Path)
            : new JsonObject { ["count"] = rows.Count, ["results"] = ToJsonArray(rows) };
        payload["query"] = options.Query;
        payload["path"] = PathEcho(options.PathPatterns);
        payload["excludeTests"] = options.ExcludeTests;
        var authoritative = AddSemanticSearchCoverage(payload, page.ScanComplete, page.ClassificationComplete);
        if (options.CountOnly)
        {
            payload["authoritative_count"] = authoritative;
            payload["total_count_authoritative"] = authoritative;
        }
        else
        {
            AddMcpPaginationEnvelope(payload, total, rows.Count, offset, options.Limit, fingerprint, generation, authoritative);
            if (offset + rows.Count >= MaxMcpPaginationOffset && total > offset + rows.Count)
            {
                payload["next_cursor"] = null;
                payload["pagination_window_exhausted"] = true;
            }
            if (format == "compact")
                ApplyCompactResults(payload, rows, row => row.Path,
                    row => row.MatchLines.Count > 0 ? row.MatchLines[0] : row.ChunkStartLine);
        }
        AddSameSymbolGuardContext(payload, options.GuardFilters, options.GuardScope, options.GuardWindow);
        AddFreshnessHint(payload, reader);
        adjustments.ApplyTo(payload);
        return CreateToolResult(id, $"Found {rows.Count} semantic search result(s).", payload);
    }
}
