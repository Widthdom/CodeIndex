using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

/// <summary>
/// Shared opt-in projection, byte-budget, and offset-cursor contract for high-volume
/// query responses. Existing streaming and compact output remains unchanged unless a
/// bounded-response control is requested. Issue #4585.
/// 高ボリュームなクエリ応答で共有する、opt-in のフィールド投影・総バイト上限・
/// offset cursor 契約。bounded-response control が指定されない限り、既存の streaming / compact
/// 出力は変更しない。Issue #4585。
/// </summary>
internal static partial class JsonEnvelopeWrapper
{
    private const int DefaultPageLimit = 20;
    private const int MaxPageWindow = MaxRawJsonItems;
    private const string LegacyResponseCursorPrefix = "response:v1:";
    private const string ResponseCursorPrefix = "response:v2:";
    private static readonly AsyncLocal<BoundedExecutionContext?> BoundedExecution = new();

    private static readonly HashSet<string> BoundedResponseCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "find", "status", "hotspots", "references", "callers", "callees",
        "symbols", "files", "languages", "impact", "map",
    };

    private static readonly HashSet<string> AutoWrapByteBudgetCommands = new(StringComparer.Ordinal)
    {
        "find", "status", "references", "callers", "callees", "languages",
    };

    private static readonly HashSet<string> AutoWrapCompactCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "find", "status", "hotspots", "references", "callers", "callees",
        "symbols", "files", "impact", "map",
    };

    private static readonly HashSet<string> LegacyLocationCompactCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "find", "references", "callers", "callees", "symbols", "files",
    };

    private static readonly HashSet<string> PageableResponseCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "find", "hotspots", "references", "callers", "callees",
        "symbols", "files", "languages", "impact", "map",
    };

    private static readonly HashSet<string> CountableResponseCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "find", "hotspots", "references", "callers", "callees",
        "symbols", "files", "languages", "impact",
    };

    private static readonly Dictionary<string, string[]> CompactFieldsByCommand = new(StringComparer.Ordinal)
    {
        ["search"] = ["file", "line"],
        ["definition"] = ["file", "line", "column"],
        ["find"] = ["file", "line", "column"],
        ["references"] = ["file", "line", "column"],
        ["callers"] = ["file", "line", "column"],
        ["callees"] = ["file", "line", "column"],
        ["hotspots"] = ["name", "kind", "path", "line", "reference_count", "reference_score", "ranking_score"],
        ["impact"] = ["path", "caller_name", "callee_name", "depth", "first_line", "reference_count", "result_kind"],
        ["symbols"] = ["path", "line", "kind", "name"],
        ["files"] = ["path", "lang", "lines"],
        ["languages"] = ["lang", "extensions", "symbol_extraction", "reference_extraction", "graph_queries"],
        ["status"] = ["api_version", "files", "chunks", "symbols", "references", "indexed_at", "git_head", "git_is_dirty", "head_freshness", "version", "graph_table_available", "hotspot_family_ready", "summary"],
        ["map"] = ["api_version", "file_count", "total_lines", "total_symbols", "total_references", "indexed_at", "git_head", "git_is_dirty", "head_freshness", "graph_table_available", "sections"],
    };

    internal static bool ShouldAutoWrapBoundedResponse(string command, string[] args)
    {
        if (!BoundedResponseCommands.Contains(command))
            return false;
        if (command == "search" && IsSearchAggregateResponseRequest(args))
            return false;
        if (HasArgument(args, "--fields") || HasArgument(args, "--cursor"))
            return true;
        if (command == "languages"
            && HasJsonOutputSelection(args)
            && (HasArgument(args, "--limit") || HasArgument(args, "--top")))
        {
            return true;
        }
        if (AutoWrapByteBudgetCommands.Contains(command) && HasArgument(args, "--max-json-bytes"))
            return true;
        return AutoWrapCompactCommands.Contains(command) && HasCompactOutputSelection(args);
    }

    private static bool IsBoundedResponseRequest(string command, string[] args)
    {
        if (!BoundedResponseCommands.Contains(command))
            return false;
        if (command == "search" && IsSearchAggregateResponseRequest(args))
            return false;

        return HasArgument(args, "--fields")
               || HasArgument(args, "--cursor")
               || (command == "search" && HasEnvelopeFlag(args) && HasJsonArrayOutputSelection(args))
               || (command != "search" && HasEnvelopeFlag(args) && HasArgument(args, "--max-json-bytes"))
               || ShouldAutoWrapBoundedResponse(command, args);
    }

    private static bool IsSearchAggregateResponseRequest(string[] args)
        => HasArgument(args, "--recipe")
           || HasArgument(args, "--list-recipes")
           || HasArgument(args, "--named-query")
           || HasArgument(args, "--count")
           || HasArgument(args, "--group-by")
           || HasArgument(args, "--unique")
           || HasArgument(args, "--count-by")
           || HasArgument(args, "--summary-only");

    private static bool HasJsonOutputSelection(string[] args)
        => args.Any(arg => string.Equals(arg, "--json", StringComparison.Ordinal)
                           || arg.StartsWith("--json=", StringComparison.Ordinal));

    private static bool HasJsonArrayOutputSelection(string[] args)
        => args.Any(arg => string.Equals(arg, "--json=array", StringComparison.OrdinalIgnoreCase));

    private static bool HasCompactOutputSelection(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--compact", StringComparison.Ordinal)
                || string.Equals(arg, "--format=compact", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(arg, "--format", StringComparison.Ordinal)
                && i + 1 < args.Length
                && string.Equals(args[i + 1], "compact", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int RunBoundedResponse(
        string command,
        string[] args,
        string appVersion,
        JsonSerializerOptions jsonOptions,
        Func<string[], int> runInner)
    {
        if (!TryParseBoundedResponseControls(command, args, out var controls, out var controlError))
            return WriteBoundedResponseUsageError(controlError!, "Use the command help to pass positive --limit/--max-json-bytes values and a next_cursor returned by the same query.");
        if (HasArgument(args, "--count"))
            return WriteBoundedResponseUsageError("Bounded response controls cannot be combined with --count.", "Run --count --json separately for a count-only response, or remove --count to page projected rows.");
        if (command == "map" && ValidateMapProjectionControls(args, controls.Fields) is { } mapProjectionError)
            return WriteBoundedResponseUsageError(mapProjectionError, "Remove the conflicting map filter, or select a collection enabled by --sections.");

        var queryNormalized = ExtractQueryArg(args);
        var (resolvedDbPath, dbPathExplicit) = ResolveQueryDbPath(args);
        var queryFingerprint = BuildResponseFingerprint(command, args);
        var snapshot = SafeReadResponseSnapshot(resolvedDbPath, dbPathExplicit, appVersion);
        if (controls.CursorQueryFingerprint is not null
            && !string.Equals(controls.CursorQueryFingerprint, queryFingerprint, StringComparison.Ordinal))
        {
            return WriteBoundedResponseUsageError(
                "--cursor does not match this command, query, or filter set.",
                "Use next_cursor from the preceding page without changing query, filter, or sort arguments.");
        }
        if (controls.CursorGenerationFingerprint is not null
            && !string.Equals(controls.CursorGenerationFingerprint, snapshot.GenerationFingerprint, StringComparison.Ordinal))
        {
            return WriteBoundedResponseUsageError(
                "--cursor is stale because the index generation changed.",
                "Restart pagination without --cursor and use the next_cursor returned by the refreshed index.");
        }
        if (controls.Offset > MaxPageWindow - controls.PageLimit)
        {
            return WriteBoundedResponseUsageError(
                $"The requested cursor window exceeds the {MaxPageWindow} row safety cap.",
                "Narrow the query or filters before continuing pagination.");
        }

        var innerArgs = PrepareBoundedInnerArgs(command, args, controls);
        using var captured = new BoundedStringWriter(MaxCapturedOutputChars);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int exitCode;
        JsonEnvelopeCaptureLimitExceededException? captureLimitExceeded = null;
        BoundedExecutionContext? executionContext = null;
        try
        {
            using var outputScope = ScopedConsoleOutput.Redirect(captured);
            executionContext = new BoundedExecutionContext(
                command,
                controls.Offset,
                controls.PageLimit,
                controls.Fields,
                controls.Compact,
                controls.ResumePath,
                controls.ResumeLine);
            using var executionScope = EnterBoundedExecution(executionContext);
            exitCode = runInner(innerArgs);
        }
        catch (JsonEnvelopeCaptureLimitExceededException ex)
        {
            captureLimitExceeded = ex;
            exitCode = CommandExitCodes.InvalidArgument;
        }
        finally
        {
            stopwatch.Stop();
        }

        if (captureLimitExceeded is not null)
        {
            var message = $"Bounded response captured output exceeded {captureLimitExceeded.MaxChars} characters.";
            return WriteBoundedCaptureError(
                command,
                queryNormalized,
                resolvedDbPath,
                dbPathExplicit,
                appVersion,
                stopwatch.Elapsed.TotalMilliseconds,
                jsonOptions,
                exitCode,
                message,
                "Reduce --limit, choose fewer --fields, or use a narrower query.",
                controls.MaxJsonBytes);
        }

        JsonArray rawResults;
        JsonObject? streamTerminal;
        JsonArray streamControlRecords;
        try
        {
            rawResults = ParseRawJsonItems(command, captured.ToString(), out streamTerminal, out streamControlRecords);
        }
        catch (JsonEnvelopeRawJsonItemLimitExceededException ex)
        {
            return WriteBoundedParseError(command, queryNormalized, resolvedDbPath, dbPathExplicit, appVersion, stopwatch.Elapsed.TotalMilliseconds, jsonOptions, $"Bounded response raw JSON item line exceeded {ex.MaxChars} characters.", "Reduce --limit or exclude large detail fields.", "max_chars", ex.MaxChars, controls.MaxJsonBytes);
        }
        catch (JsonEnvelopeRawJsonBudgetExceededException ex)
        {
            return WriteBoundedParseError(command, queryNormalized, resolvedDbPath, dbPathExplicit, appVersion, stopwatch.Elapsed.TotalMilliseconds, jsonOptions, $"Bounded response raw JSON {ex.BudgetName} exceeded {ex.MaxValue}.", "Reduce --limit or narrow the query.", ex.JsonPropertyName, ex.MaxValue, controls.MaxJsonBytes);
        }

        var commandError = TakeCommandError(rawResults, exitCode);
        PromoteEmptyLegacyCompactPayload(command, controls, rawResults, streamControlRecords);
        var extraction = ExtractResponseItems(command, rawResults, controls);
        var availableItems = extraction.Items;
        var pageItems = availableItems
            .Take(controls.PageLimit)
            .Select(item => ProjectResponseItem(item, controls.EffectiveFields(command, extraction.PrimaryCollection)))
            .ToList();

        var count = executionContext?.ReportedTotalCount is { } reportedTotalCount
            ? new ResponseCount(
                reportedTotalCount,
                executionContext.ReportedTotalCountAuthoritative)
            : ResolveTotalCount(command, args, runInner, extraction, availableItems.Count, controls.Offset, streamTerminal);
        var totalCount = Math.Max(count.TotalCount, controls.Offset + pageItems.Count);
        var totalAuthoritative = count.Authoritative;
        var completedSnapshot = SafeReadResponseSnapshot(resolvedDbPath, dbPathExplicit, appVersion);
        if (!string.Equals(snapshot.GenerationFingerprint, completedSnapshot.GenerationFingerprint, StringComparison.Ordinal))
        {
            return WriteBoundedResponseUsageError(
                "The index generation changed while this page was being read.",
                "Restart pagination without --cursor after the active index refresh completes.");
        }
        var envelope = BuildBoundedEnvelopeWithinBudget(
            command,
            queryNormalized,
            resolvedDbPath,
            dbPathExplicit,
            appVersion,
            stopwatch.Elapsed.TotalMilliseconds,
            pageItems,
            controls,
            queryFingerprint,
            snapshot,
            totalCount,
            totalAuthoritative,
            exitCode,
            extraction,
            commandError,
            streamTerminal,
            streamControlRecords,
            jsonOptions,
            out var emittedJson,
            out var emittedCount);

        if (envelope is null)
        {
            return WriteBoundedResponseUsageError(
                $"--max-json-bytes {controls.MaxJsonBytes} is too small for the bounded response metadata and one projected row.",
                "Increase --max-json-bytes or choose fewer --fields.");
        }

        Console.WriteLine(emittedJson);
        return exitCode;
    }

    private static JsonObject? BuildBoundedEnvelopeWithinBudget(
        string command,
        string? queryNormalized,
        string dbPath,
        bool dbPathExplicit,
        string appVersion,
        double elapsedMs,
        IReadOnlyList<JsonNode?> pageItems,
        BoundedResponseControls controls,
        string queryFingerprint,
        ResponseSnapshot snapshot,
        int totalCount,
        bool totalAuthoritative,
        int exitCode,
        ResponseExtraction extraction,
        JsonObject? commandError,
        JsonObject? streamTerminal,
        JsonArray streamControlRecords,
        JsonSerializerOptions jsonOptions,
        out string emittedJson,
        out int emittedCount)
    {
        emittedJson = string.Empty;
        emittedCount = 0;
        JsonObject BuildCandidate(int count)
        {
            var results = new JsonArray();
            for (var i = 0; i < count; i++)
                results.Add(pageItems[i]?.DeepClone());
            var envelope = BuildEnvelope(
                command,
                queryNormalized,
                dbPath,
                dbPathExplicit,
                appVersion,
                elapsedMs,
                results,
                exitCode,
                error: commandError is null ? null : (JsonObject)commandError.DeepClone(),
                streamTerminal: streamTerminal,
                streamControlRecords: streamControlRecords);
            var metadata = (JsonObject)envelope["metadata"]!;
            metadata["result_stable_at"] = snapshot.ResultStableAt;
            if (commandError is not null)
            {
                metadata["returned_count"] = 0;
                metadata["total_count"] = 0;
                metadata["total_count_authoritative"] = true;
                metadata["omitted_count"] = 0;
                if (controls.Fields is { Count: > 0 })
                {
                    var errorFields = new JsonArray();
                    foreach (var field in controls.Fields)
                        errorFields.Add(field);
                    metadata["fields"] = errorFields;
                }
                if (controls.MaxJsonBytes.HasValue)
                    metadata["max_json_bytes"] = controls.MaxJsonBytes.Value;
                return envelope;
            }
            var nextOffset = controls.Offset + count;
            var paginationWindowExhausted = nextOffset < totalCount && nextOffset >= MaxPageWindow;
            var scanCursor = ReadString(streamTerminal, "next_cursor");
            var emittedAllCapturedRows = count == pageItems.Count;
            var selectedScanCursor = emittedAllCapturedRows ? scanCursor : null;
            var hasMore = selectedScanCursor is not null
                          || count > 0 && nextOffset < totalCount && !paginationWindowExhausted;
            metadata["result_count"] = count;
            metadata["returned_count"] = count;
            metadata["total_count"] = totalCount;
            metadata["total_count_authoritative"] = totalAuthoritative;
            metadata["omitted_count"] = Math.Max(0, totalCount - count);
            metadata["remaining_count"] = Math.Max(0, totalCount - nextOffset);
            metadata["cursor_offset"] = controls.Offset;
            metadata["page_limit"] = controls.PageLimit;
            metadata["has_more"] = hasMore;
            metadata["next_cursor"] = selectedScanCursor
                ?? (hasMore && count > 0
                    ? FormatResponseCursor(nextOffset, queryFingerprint, snapshot.GenerationFingerprint)
                    : null);
            metadata["truncated"] = scanCursor is not null || totalCount > count;
            metadata["pagination_window_limit"] = MaxPageWindow;
            metadata["pagination_window_exhausted"] = paginationWindowExhausted;
            if (controls.Compact)
                metadata["format"] = "compact";
            if (controls.Fields is { Count: > 0 })
            {
                var fields = new JsonArray();
                foreach (var field in controls.Fields)
                    fields.Add(field);
                metadata["fields"] = fields;
            }
            if (controls.MaxJsonBytes.HasValue)
                metadata["max_json_bytes"] = controls.MaxJsonBytes.Value;
            if (pageItems.Count > count)
            {
                metadata["byte_limit_reached"] = true;
                metadata["byte_limit_omitted_count"] = pageItems.Count - count;
            }
            if (extraction.PrimaryCollection is not null)
                metadata["primary_collection"] = extraction.PrimaryCollection;
            if (extraction.Context is { Count: > 0 })
                metadata["response_context"] = extraction.Context.DeepClone();
            if (controls.Compact
                && LegacyLocationCompactCommands.Contains(command)
                && extraction.SourcePayload is not null)
            {
                return BuildBackwardCompatibleCompactEnvelope(
                    extraction.SourcePayload,
                    envelope,
                    results,
                    controls,
                    count,
                    totalCount,
                    totalAuthoritative,
                    nextOffset,
                    hasMore,
                    paginationWindowExhausted,
                    queryFingerprint,
                    snapshot,
                    extraction.PrimaryCollection);
            }
            if (controls.Compact
                && command == "map"
                && controls.Fields is null
                && extraction.SourcePayload is not null)
            {
                return BuildBackwardCompatibleMapCompactEnvelope(extraction.SourcePayload, envelope);
            }
            return envelope;
        }

        var requestedCount = pageItems.Count;
        var candidate = BuildCandidate(requestedCount);
        var candidateJson = candidate.ToJsonString(jsonOptions);
        if (!controls.MaxJsonBytes.HasValue || JsonFitsResponseBudget(candidateJson, controls.MaxJsonBytes.Value))
        {
            emittedJson = candidateJson;
            emittedCount = requestedCount;
            return candidate;
        }

        JsonObject? best = null;
        string? bestJson = null;
        var low = 0;
        var high = requestedCount;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var current = BuildCandidate(mid);
            var currentJson = current.ToJsonString(jsonOptions);
            if (JsonFitsResponseBudget(currentJson, controls.MaxJsonBytes.Value))
            {
                best = current;
                bestJson = currentJson;
                emittedCount = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (best is null || (requestedCount > 0 && emittedCount == 0))
            return null;
        emittedJson = bestJson!;
        return best;
    }

    private static bool JsonFitsResponseBudget(string json, int maxJsonBytes)
        => Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine) <= maxJsonBytes;

    private static JsonObject? TakeCommandError(JsonArray rawResults, int exitCode)
    {
        if (exitCode == CommandExitCodes.Success
            || rawResults.Count != 1
            || rawResults[0] is not JsonObject candidate
            || candidate["status"] is not JsonValue status
            || !status.TryGetValue<string>(out var statusText)
            || !string.Equals(statusText, "error", StringComparison.Ordinal)
            || candidate["error_code"] is null)
        {
            return null;
        }

        var error = (JsonObject)candidate.DeepClone();
        error.Remove("status");
        error.Remove("api_version");
        rawResults.Clear();
        return error;
    }

    private static void PromoteEmptyLegacyCompactPayload(
        string command,
        BoundedResponseControls controls,
        JsonArray rawResults,
        JsonArray streamControlRecords)
    {
        if (!controls.Compact || !LegacyLocationCompactCommands.Contains(command) || rawResults.Count > 0)
            return;
        for (var index = streamControlRecords.Count - 1; index >= 0; index--)
        {
            if (streamControlRecords[index] is not JsonObject candidate
                || candidate["results"] is not JsonArray
                || candidate["format"]?.GetValue<string>() != "compact")
                continue;
            rawResults.Add(candidate.DeepClone());
            streamControlRecords.RemoveAt(index);
            return;
        }
    }

    private static JsonObject BuildBackwardCompatibleCompactEnvelope(
        JsonObject sourcePayload,
        JsonObject envelope,
        JsonArray results,
        BoundedResponseControls controls,
        int returnedCount,
        int totalCount,
        bool totalAuthoritative,
        int nextOffset,
        bool hasMore,
        bool paginationWindowExhausted,
        string queryFingerprint,
        ResponseSnapshot snapshot,
        string? primaryCollection)
    {
        var compatible = (JsonObject)sourcePayload.DeepClone();
        var collectionName = primaryCollection ?? "results";
        compatible[collectionName] = results.DeepClone();
        compatible["metadata"] = envelope["metadata"]!.DeepClone();
        if (collectionName == "results")
            compatible["count"] = returnedCount;
        else
            compatible["emitted_count"] = returnedCount;
        compatible["returned_count"] = returnedCount;
        compatible["total_count"] = totalCount;
        compatible["total_count_authoritative"] = totalAuthoritative;
        compatible["omitted_count"] = Math.Max(0, totalCount - returnedCount);
        compatible["remaining_count"] = Math.Max(0, totalCount - nextOffset);
        compatible["cursor_offset"] = controls.Offset;
        compatible["page_limit"] = controls.PageLimit;
        compatible["has_more"] = hasMore;
        compatible["next_cursor"] = hasMore && returnedCount > 0
            ? FormatResponseCursor(nextOffset, queryFingerprint, snapshot.GenerationFingerprint)
            : null;
        compatible["result_stable_at"] = snapshot.ResultStableAt;
        compatible["truncated"] = totalCount > returnedCount;
        compatible["pagination_window_limit"] = MaxPageWindow;
        compatible["pagination_window_exhausted"] = paginationWindowExhausted;
        var truncation = compatible["truncation"] as JsonObject;
        if (truncation is null)
        {
            truncation = new JsonObject();
            compatible["truncation"] = truncation;
        }
        truncation["limit"] = controls.PageLimit;
        truncation["limit_reached"] = totalCount > returnedCount;
        if (compatible["query_context"] is JsonObject queryContext)
            queryContext["limit"] = controls.PageLimit;
        return compatible;
    }

    private static JsonObject BuildBackwardCompatibleMapCompactEnvelope(
        JsonObject sourcePayload,
        JsonObject envelope)
    {
        var compatible = (JsonObject)sourcePayload.DeepClone();
        compatible["metadata"] = envelope["metadata"]!.DeepClone();
        return compatible;
    }

    private static ResponseExtraction ExtractResponseItems(string command, JsonArray rawResults, BoundedResponseControls controls)
    {
        if (controls.Compact
            && LegacyLocationCompactCommands.Contains(command)
            && rawResults.FirstOrDefault() is JsonObject compactPayload
            && compactPayload["results"] is JsonArray compactResults)
        {
            return new ResponseExtraction(
                new JsonArray(compactResults.Select(item => item?.DeepClone()).ToArray()),
                "results",
                null,
                compactPayload);
        }
        if (command == "hotspots" && rawResults.FirstOrDefault() is JsonObject hotspotsPayload)
            return ExtractNestedCollection(hotspotsPayload, "hotspots");
        if (command == "symbols"
            && rawResults.FirstOrDefault() is JsonObject symbolsPayload
            && symbolsPayload["symbols"] is JsonArray)
            return ExtractNestedCollection(symbolsPayload, "symbols");
        if (command == "files"
            && rawResults.FirstOrDefault() is JsonObject filesPayload
            && filesPayload["files"] is JsonArray)
            return ExtractNestedCollection(filesPayload, "files");
        if (command == "languages" && rawResults.FirstOrDefault() is JsonObject languagesPayload)
            return ExtractNestedCollection(languagesPayload, "languages");
        if (command == "impact" && rawResults.FirstOrDefault() is JsonObject impactPayload)
        {
            var requestedCollection = SelectRequestedCollection(controls.Fields, "callers", "file_impacts", "definitions");
            var primary = requestedCollection
                          ?? FirstPresentArray(impactPayload, "callers", "file_impacts", "definitions")
                          ?? "callers";
            return ExtractNestedCollection(impactPayload, primary);
        }
        if (command == "map" && rawResults.FirstOrDefault() is JsonObject mapPayload)
        {
            var requestedCollection = SelectRequestedCollection(
                controls.Fields,
                "languages",
                "modules",
                "top_files",
                "largest_files",
                "symbol_rich_files",
                "reference_rich_files",
                "entrypoints");
            if (requestedCollection is not null)
                return ExtractNestedCollection(mapPayload, requestedCollection);
            if (controls.Compact && controls.Fields is null)
            {
                return new ResponseExtraction(
                    new JsonArray(mapPayload.DeepClone()),
                    null,
                    null,
                    mapPayload);
            }
        }
        if (command is "symbols" or "files")
            return ExtractDiscoveryRows(command, rawResults);
        if (rawResults.Count == 1 && rawResults[0] is JsonArray arrayPayload)
        {
            return new ResponseExtraction(
                new JsonArray(arrayPayload.Select(item => item?.DeepClone()).ToArray()),
                "results",
                null,
                null);
        }

        var rows = new JsonArray();
        foreach (var result in rawResults)
        {
            if (result is JsonObject obj && IsJsonStreamTerminal(obj))
                continue;
            rows.Add(result?.DeepClone());
        }
        return new ResponseExtraction(rows, null, null, null);
    }

    private static ResponseExtraction ExtractDiscoveryRows(string command, JsonArray rawResults)
    {
        var rows = new JsonArray();
        JsonObject? context = null;
        foreach (var result in rawResults)
        {
            if (result is JsonObject obj && IsJsonStreamTerminal(obj))
                continue;
            rows.Add(result?.DeepClone());
            if (command != "symbols" || result is not JsonObject row)
                continue;

            if (row.TryGetPropertyValue("exact_index_available", out var exactIndexAvailable))
            {
                context ??= new JsonObject();
                context["exact_index_available"] = exactIndexAvailable?.DeepClone();
            }
            if (row.TryGetPropertyValue("degraded_reason", out var degradedReason))
            {
                context ??= new JsonObject();
                context["degraded_reason"] = degradedReason?.DeepClone();
            }
        }
        return new ResponseExtraction(rows, command, context, null);
    }

    private static ResponseExtraction ExtractNestedCollection(JsonObject payload, string collectionName)
    {
        var items = payload[collectionName] as JsonArray ?? [];
        var clonedItems = new JsonArray(items.Select(item => item?.DeepClone()).ToArray());
        var context = new JsonObject();
        foreach (var property in payload)
        {
            if (property.Value is JsonArray)
                continue;
            context[property.Key] = property.Value?.DeepClone();
        }
        return new ResponseExtraction(clonedItems, collectionName, context, payload);
    }

    private static string? SelectRequestedCollection(IReadOnlyList<string>? fields, params string[] collections)
    {
        if (fields is null)
            return null;
        foreach (var collection in collections)
        {
            if (fields.Any(field => string.Equals(field, collection, StringComparison.Ordinal)
                                    || field.StartsWith(collection + ".", StringComparison.Ordinal)))
                return collection;
        }
        return null;
    }

    private static string? FirstPresentArray(JsonObject payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (payload[name] is JsonArray { Count: > 0 })
                return name;
        }
        return names.FirstOrDefault(name => payload[name] is JsonArray);
    }

    private static JsonNode? ProjectResponseItem(JsonNode? item, IReadOnlyList<string>? fields)
    {
        if (item is not JsonObject obj || fields is null || fields.Count == 0 || fields.Contains("all", StringComparer.Ordinal))
            return item?.DeepClone();
        var projected = new JsonObject();
        foreach (var field in fields)
        {
            if (obj.TryGetPropertyValue(field, out var value))
                projected[field] = value?.DeepClone();
            else if (string.Equals(field, "path", StringComparison.Ordinal)
                     && obj.TryGetPropertyValue("file", out var file))
                projected[field] = file?.DeepClone();
            else if (string.Equals(field, "file", StringComparison.Ordinal)
                     && obj.TryGetPropertyValue("path", out var path))
                projected[field] = path?.DeepClone();
            else if (string.Equals(field, "body", StringComparison.Ordinal)
                     && obj.TryGetPropertyValue("body_content", out var bodyContent))
                projected["body_content"] = bodyContent?.DeepClone();
        }
        return projected;
    }

    private static ResponseCount ResolveTotalCount(
        string command,
        string[] args,
        Func<string[], int> runInner,
        ResponseExtraction extraction,
        int availableCount,
        int offset,
        JsonObject? streamTerminal)
    {
        if (command == "status")
            return new ResponseCount(availableCount, true);
        if (command == "map")
        {
            var totalProperty = extraction.PrimaryCollection switch
            {
                "languages" => "language_count",
                "modules" => "module_count",
                "entrypoints" => "entrypoint_count",
                "top_files" or "largest_files" or "symbol_rich_files" or "reference_rich_files" => "file_count",
                _ => null,
            };
            if (totalProperty is not null && TryReadInt(extraction.SourcePayload, totalProperty, out var sectionTotal))
                return new ResponseCount(sectionTotal, true);
            return new ResponseCount(availableCount, true);
        }
        if (command == "impact" && extraction.PrimaryCollection is "callers" or "file_impacts")
        {
            var impactMode = extraction.SourcePayload?["impact_mode"]?.GetValue<string>();
            var collectionIsActive = extraction.PrimaryCollection == "callers"
                ? string.Equals(impactMode, "callers", StringComparison.Ordinal)
                : string.Equals(impactMode, "file_dependency_hints", StringComparison.Ordinal);
            if (!collectionIsActive)
                return new ResponseCount(0, true);
        }
        if (command == "impact" && extraction.PrimaryCollection == "definitions")
        {
            if (TryReadInt(extraction.SourcePayload, "logical_definition_count", out var logicalDefinitionCount))
                return new ResponseCount(logicalDefinitionCount, true);
            if (TryReadInt(extraction.SourcePayload, "definition_count", out var definitionCount))
                return new ResponseCount(definitionCount, true);
        }
        ResponseCount? terminalCount = null;
        if (streamTerminal is not null
            && TryReadInt(streamTerminal, "total_count", out var terminalTotal)
            && TryReadBool(streamTerminal, "total_count_authoritative", out var terminalAuthoritative))
        {
            terminalCount = new ResponseCount(terminalTotal, terminalAuthoritative);
            if (terminalAuthoritative)
                return terminalCount.Value;
        }
        if (!CountableResponseCommands.Contains(command))
            return terminalCount ?? new ResponseCount(offset + availableCount, false);

        var countArgs = PrepareCountArgs(command, args);
        using var captured = new BoundedStringWriter(MaxRawJsonItemChars);
        int countExitCode;
        try
        {
            using var outputScope = ScopedConsoleOutput.Redirect(captured);
            countExitCode = runInner(countArgs);
        }
        catch
        {
            return terminalCount ?? new ResponseCount(offset + availableCount, false);
        }
        try
        {
            var countItems = ParseRawJsonItems(command, captured.ToString(), out _, out _);
            var countPayload = countItems.OfType<JsonObject>().FirstOrDefault(obj => obj.ContainsKey("count"));
            if (countPayload is null || !TryReadInt(countPayload, "count", out var total))
                return terminalCount ?? new ResponseCount(offset + availableCount, false);
            var authoritative = TryReadBool(countPayload, "authoritative_count", out var explicitAuthority)
                ? explicitAuthority
                : countExitCode == CommandExitCodes.Success
                  && !ReadOptionalBool(countPayload, "degraded")
                  && ReadOptionalBool(countPayload, "graph_table_available", defaultValue: true)
                  && ReadOptionalBool(countPayload, "hotspot_family_ready", defaultValue: true);
            return new ResponseCount(total, authoritative);
        }
        catch
        {
            return terminalCount ?? new ResponseCount(offset + availableCount, false);
        }
    }

    private static bool TryReadInt(JsonObject? obj, string propertyName, out int value)
    {
        value = 0;
        return obj is not null
               && obj[propertyName] is JsonValue jsonValue
               && jsonValue.TryGetValue(out value);
    }

    private static bool TryReadBool(JsonObject obj, string propertyName, out bool value)
    {
        value = false;
        return obj[propertyName] is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static bool ReadOptionalBool(JsonObject obj, string propertyName, bool defaultValue = false)
        => TryReadBool(obj, propertyName, out var value) ? value : defaultValue;

    private static string? ReadString(JsonObject? obj, string propertyName)
        => obj?[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string[] PrepareBoundedInnerArgs(string command, string[] args, BoundedResponseControls controls)
    {
        var stripped = StripResponseOptions(args, stripLimit: PageableResponseCommands.Contains(command));
        var bodyRequested = HasExplicitBodyProjection(controls.Fields);
        if (!bodyRequested && (controls.Compact || controls.Fields is { Count: > 0 }))
            stripped.RemoveAll(arg => string.Equals(arg, "--body", StringComparison.Ordinal));
        if (PageableResponseCommands.Contains(command))
        {
            stripped.Add("--limit");
            stripped.Add(controls.PageLimit.ToString(CultureInfo.InvariantCulture));
        }
        if (controls.Compact
            && (command == "map" || LegacyLocationCompactCommands.Contains(command) && !bodyRequested))
        {
            stripped.Add("--format");
            stripped.Add("compact");
        }
        stripped.Add("--json");
        return [.. stripped];
    }

    private static bool HasExplicitBodyProjection(IReadOnlyList<string>? fields)
        => fields?.Any(field =>
            string.Equals(field, "all", StringComparison.Ordinal)
            || string.Equals(field, "body", StringComparison.Ordinal)
            || string.Equals(field, "body_content", StringComparison.Ordinal)
            || field.StartsWith("body_", StringComparison.Ordinal)) == true;

    private static string? ValidateMapProjectionControls(string[] args, IReadOnlyList<string>? fields)
    {
        var collection = SelectRequestedCollection(
            fields,
            "languages",
            "modules",
            "top_files",
            "largest_files",
            "symbol_rich_files",
            "reference_rich_files",
            "entrypoints");
        if (collection is null)
            return null;
        if (HasArgument(args, "--summary-only"))
            return $"Map collection projection '{collection}' cannot be combined with --summary-only.";

        var requestedSections = ReadMapSections(args);
        if (requestedSections is null)
            return null;
        var requiredSection = collection switch
        {
            "languages" => "languages",
            "modules" => "tree",
            "largest_files" => "metrics",
            _ => "hotspots",
        };
        return requestedSections.Contains(requiredSection, StringComparer.Ordinal)
            ? null
            : $"Map collection projection '{collection}' requires --sections {requiredSection}.";
    }

    private static List<string>? ReadMapSections(string[] args)
    {
        List<string>? sections = null;
        for (var i = 0; i < args.Length; i++)
        {
            string? value = null;
            if (args[i].StartsWith("--sections=", StringComparison.Ordinal))
                value = args[i]["--sections=".Length..];
            else if (string.Equals(args[i], "--sections", StringComparison.Ordinal) && i + 1 < args.Length)
                value = args[++i];
            if (value is null)
                continue;
            sections ??= [];
            foreach (var section in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                sections.Add(section switch
                {
                    "modules" or "module" => "tree",
                    "entrypoints" or "entrypoint" => "hotspots",
                    "largest-files" or "largest" => "metrics",
                    _ => section,
                });
            }
        }
        return sections;
    }

    private static string[] PrepareCountArgs(string command, string[] args)
    {
        var stripped = StripResponseOptions(args, stripLimit: true);
        stripped.RemoveAll(arg => string.Equals(arg, "--body", StringComparison.Ordinal)
                                  || string.Equals(arg, "--summary-only", StringComparison.Ordinal)
                                  || string.Equals(arg, "--strict-not-found", StringComparison.Ordinal));
        if (command == "impact")
        {
            stripped.Add("--limit");
            stripped.Add(MaxPageWindow.ToString(CultureInfo.InvariantCulture));
        }
        stripped.Add("--count");
        stripped.Add("--json");
        return [.. stripped];
    }

    private static List<string> StripResponseOptions(string[] args, bool stripLimit)
    {
        var stripped = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, EnvelopeFlag, StringComparison.Ordinal)
                || string.Equals(arg, "--compact", StringComparison.Ordinal)
                || string.Equals(arg, "--pretty", StringComparison.Ordinal)
                || string.Equals(arg, "--json", StringComparison.Ordinal)
                || arg.StartsWith("--json=", StringComparison.Ordinal)
                || arg.StartsWith("--fields=", StringComparison.Ordinal)
                || arg.StartsWith("--cursor=", StringComparison.Ordinal)
                || arg.StartsWith("--max-json-bytes=", StringComparison.Ordinal)
                || arg.StartsWith("--format=", StringComparison.Ordinal)
                || (stripLimit && (arg.StartsWith("--limit=", StringComparison.Ordinal) || arg.StartsWith("--top=", StringComparison.Ordinal))))
                continue;
            if (IsResponseValueOption(arg, stripLimit) && i + 1 < args.Length)
            {
                i++;
                continue;
            }
            if (string.Equals(arg, "--count", StringComparison.Ordinal))
                continue;
            stripped.Add(arg);
        }
        return stripped;
    }

    private static bool IsResponseValueOption(string arg, bool includeLimit)
        => arg is "--fields" or "--cursor" or "--max-json-bytes" or "--format"
           || includeLimit && arg is "--limit" or "--top";

    private static bool TryParseBoundedResponseControls(
        string command,
        string[] args,
        out BoundedResponseControls controls,
        out string? error)
    {
        List<string>? fields = null;
        string? cursor = null;
        int? maxJsonBytes = null;
        var pageLimit = DefaultPageLimit;
        var compact = HasCompactOutputSelection(args);
        error = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!TryReadInlineOrSeparated(args, ref i, arg, "--fields", out var fieldsValue, out var matched, out error))
            {
                controls = default!;
                return false;
            }
            if (matched)
            {
                fields = fieldsValue!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (fields.Count == 0 || fields.Count > 64 || fields.Any(field => field.Length > 128))
                {
                    controls = default!;
                    error = "--fields must contain 1 to 64 comma-separated field names, each at most 128 characters.";
                    return false;
                }
                continue;
            }
            if (!TryReadInlineOrSeparated(args, ref i, arg, "--cursor", out var cursorValue, out matched, out error))
            {
                controls = default!;
                return false;
            }
            if (matched)
            {
                cursor = cursorValue;
                continue;
            }
            if (!TryReadPositiveIntControl(args, ref i, arg, "--max-json-bytes", out var parsedBytes, out matched, out error))
            {
                controls = default!;
                return false;
            }
            if (matched)
            {
                maxJsonBytes = parsedBytes;
                continue;
            }
            if (!TryReadPositiveIntControl(args, ref i, arg, "--limit", out var parsedLimit, out matched, out error)
                || !matched && !TryReadPositiveIntControl(args, ref i, arg, "--top", out parsedLimit, out matched, out error))
            {
                controls = default!;
                return false;
            }
            if (matched)
                pageLimit = parsedLimit;
        }

        var offset = 0;
        string? cursorQueryFingerprint = null;
        string? cursorGenerationFingerprint = null;
        string? resumePath = null;
        int? resumeLine = null;
        if (cursor is not null
            && !TryParseResponseCursor(
                cursor,
                out offset,
                out cursorQueryFingerprint,
                out cursorGenerationFingerprint,
                out resumePath,
                out resumeLine))
        {
            controls = default!;
            error = "--cursor must be an opaque response:v2 cursor returned as next_cursor.";
            return false;
        }
        controls = new BoundedResponseControls(
            fields,
            compact,
            maxJsonBytes,
            pageLimit,
            offset,
            cursorQueryFingerprint,
            cursorGenerationFingerprint,
            resumePath,
            resumeLine);
        return true;
    }

    private static bool TryReadInlineOrSeparated(
        string[] args,
        ref int index,
        string arg,
        string option,
        out string? value,
        out bool matched,
        out string? error)
    {
        value = null;
        matched = false;
        error = null;
        if (arg.StartsWith(option + "=", StringComparison.Ordinal))
        {
            matched = true;
            value = arg[(option.Length + 1)..];
        }
        else if (string.Equals(arg, option, StringComparison.Ordinal))
        {
            matched = true;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                error = $"{option} requires a value.";
                return false;
            }
            value = args[++index];
        }
        return true;
    }

    private static bool TryReadPositiveIntControl(
        string[] args,
        ref int index,
        string arg,
        string option,
        out int value,
        out bool matched,
        out string? error)
    {
        value = 0;
        if (!TryReadInlineOrSeparated(args, ref index, arg, option, out var raw, out matched, out error))
            return false;
        if (!matched)
            return true;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            error = $"{option} requires a positive integer.";
            return false;
        }
        return true;
    }

    private static string BuildResponseFingerprint(string command, string[] args)
    {
        var normalized = StripResponseOptions(args, stripLimit: true);
        normalized.RemoveAll(arg => string.Equals(arg, "--body", StringComparison.Ordinal));
        normalized.RemoveAll(arg => arg is "--allow-partial" or "--results-only" or "--verbose" or "--profile");
        RemoveOptionWithValue(normalized, "--line-scan-limit");
        var input = command + "\0" + string.Join('\0', normalized);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static void RemoveOptionWithValue(List<string> args, string option)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            if (args[i].StartsWith(option + "=", StringComparison.Ordinal))
            {
                args.RemoveAt(i);
                continue;
            }
            if (!string.Equals(args[i], option, StringComparison.Ordinal))
                continue;
            args.RemoveAt(i);
            if (i < args.Count)
                args.RemoveAt(i);
        }
    }

    private static string FormatResponseCursor(
        int offset,
        string queryFingerprint,
        string generationFingerprint,
        string? resumePath = null,
        int? resumeLine = null)
    {
        var payload = new JsonObject
        {
            ["offset"] = offset,
            ["query"] = queryFingerprint,
            ["generation"] = generationFingerprint,
        };
        if (resumePath is not null)
            payload["resume_path"] = resumePath;
        if (resumeLine.HasValue)
            payload["resume_line"] = resumeLine.Value;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return ResponseCursorPrefix + encoded;
    }

    private static bool TryParseResponseCursor(
        string cursor,
        out int offset,
        out string? queryFingerprint,
        out string? generationFingerprint,
        out string? resumePath,
        out int? resumeLine)
    {
        offset = 0;
        queryFingerprint = null;
        generationFingerprint = null;
        resumePath = null;
        resumeLine = null;
        if (cursor.StartsWith(LegacyResponseCursorPrefix, StringComparison.Ordinal))
        {
            var remainder = cursor[LegacyResponseCursorPrefix.Length..];
            var separator = remainder.IndexOf(':');
            if (separator <= 0 || separator == remainder.Length - 1)
                return false;
            if (!int.TryParse(remainder[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
                return false;
            queryFingerprint = remainder[(separator + 1)..];
            return IsCursorFingerprint(queryFingerprint);
        }
        if (!cursor.StartsWith(ResponseCursorPrefix, StringComparison.Ordinal)
            || cursor.Length > 16_384)
        {
            return false;
        }

        var encoded = cursor[ResponseCursorPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var paddingLength = (4 - encoded.Length % 4) % 4;
        if (paddingLength > 0)
            encoded += new string('=', paddingLength);
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))) as JsonObject;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
        if (payload is null
            || !TryReadInt(payload, "offset", out offset)
            || offset < 0)
        {
            return false;
        }
        queryFingerprint = ReadString(payload, "query");
        generationFingerprint = ReadString(payload, "generation");
        resumePath = ReadString(payload, "resume_path");
        if (payload["resume_line"] is JsonValue resumeValue && resumeValue.TryGetValue<int>(out var parsedResumeLine))
            resumeLine = parsedResumeLine;
        return IsCursorFingerprint(queryFingerprint)
               && IsCursorFingerprint(generationFingerprint)
               && (resumePath is null || resumePath.Length <= 4096)
               && (!resumeLine.HasValue || resumeLine.Value > 0)
               && (resumePath is null) == !resumeLine.HasValue;
    }

    private static bool IsCursorFingerprint(string? fingerprint)
        => fingerprint is { Length: 16 } && fingerprint.All(Uri.IsHexDigit);

    private static int WriteBoundedResponseUsageError(string message, string hint)
    {
        CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: {message}");
        CommandErrorWriter.WriteStderr($"Hint: {hint}");
        return CommandExitCodes.UsageError;
    }

    private static int WriteBoundedCaptureError(
        string command,
        string? queryNormalized,
        string dbPath,
        bool dbPathExplicit,
        string appVersion,
        double elapsedMs,
        JsonSerializerOptions jsonOptions,
        int exitCode,
        string message,
        string hint,
        int? maxJsonBytes)
        => WriteBoundedErrorEnvelope(command, queryNormalized, dbPath, dbPathExplicit, appVersion, elapsedMs, jsonOptions, exitCode, message, hint, null, null, maxJsonBytes);

    private static int WriteBoundedParseError(
        string command,
        string? queryNormalized,
        string dbPath,
        bool dbPathExplicit,
        string appVersion,
        double elapsedMs,
        JsonSerializerOptions jsonOptions,
        string message,
        string hint,
        string budgetProperty,
        int budgetValue,
        int? maxJsonBytes)
        => WriteBoundedErrorEnvelope(command, queryNormalized, dbPath, dbPathExplicit, appVersion, elapsedMs, jsonOptions, CommandExitCodes.InvalidArgument, message, hint, budgetProperty, budgetValue, maxJsonBytes);

    private static int WriteBoundedErrorEnvelope(
        string command,
        string? queryNormalized,
        string dbPath,
        bool dbPathExplicit,
        string appVersion,
        double elapsedMs,
        JsonSerializerOptions jsonOptions,
        int exitCode,
        string message,
        string hint,
        string? budgetProperty,
        int? budgetValue,
        int? maxJsonBytes)
    {
        CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: {message}");
        CommandErrorWriter.WriteStderr($"Hint: {hint}");
        var error = new JsonObject
        {
            ["message"] = message,
            ["hint"] = hint,
            ["error_code"] = CommandErrorCodes.UsageError,
        };
        if (budgetProperty is not null)
            error[budgetProperty] = budgetValue;
        var envelope = BuildEnvelope(command, queryNormalized, dbPath, dbPathExplicit, appVersion, elapsedMs, [], exitCode, error);
        var json = envelope.ToJsonString(jsonOptions);
        if (!maxJsonBytes.HasValue || JsonFitsResponseBudget(json, maxJsonBytes.Value))
            Console.WriteLine(json);
        return exitCode;
    }

    private sealed record BoundedResponseControls(
        IReadOnlyList<string>? Fields,
        bool Compact,
        int? MaxJsonBytes,
        int PageLimit,
        int Offset,
        string? CursorQueryFingerprint,
        string? CursorGenerationFingerprint,
        string? ResumePath,
        int? ResumeLine)
    {
        public IReadOnlyList<string>? EffectiveFields(string command, string? primaryCollection)
        {
            var preserveFullDiscoveryRows = command is "search" or "languages";
            var selected = Fields
                           ?? ((!preserveFullDiscoveryRows || Compact)
                               && CompactFieldsByCommand.TryGetValue(command, out var defaults)
                               ? defaults
                               : null);
            if (selected is null || primaryCollection is null)
                return selected;
            var dotted = selected
                .Where(field => field.StartsWith(primaryCollection + ".", StringComparison.Ordinal))
                .Select(field => field[(primaryCollection.Length + 1)..])
                .ToList();
            if (dotted.Count > 0)
                return dotted;
            if (selected.Contains(primaryCollection, StringComparer.Ordinal))
                return null;
            return selected.Where(field => !field.Contains('.')).ToList();
        }
    }

    private sealed record ResponseExtraction(
        JsonArray Items,
        string? PrimaryCollection,
        JsonObject? Context,
        JsonObject? SourcePayload);

    private readonly record struct ResponseCount(int TotalCount, bool Authoritative);

    private readonly record struct ResponseSnapshot(
        string GenerationFingerprint,
        string? ResultStableAt);

    private static ResponseSnapshot SafeReadResponseSnapshot(
        string dbPath,
        bool dbPathExplicit,
        string appVersion)
    {
        try
        {
            if (!dbPathExplicit
                && !dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(dbPath))
            {
                return BuildFallbackResponseSnapshot(appVersion);
            }

            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            if (!db.TryValidateIsCodeIndexDb(out _))
                return BuildFallbackResponseSnapshot(appVersion);
            return BuildResponseSnapshot(new DbReader(db));
        }
        catch
        {
            return BuildFallbackResponseSnapshot(appVersion);
        }
    }

    private static ResponseSnapshot BuildResponseSnapshot(DbReader reader)
    {
        var generation = reader.GetPaginationGeneration();
        return new(
            BuildResponseValueFingerprint(generation.Identity),
            generation.StableAt);
    }

    private static ResponseSnapshot BuildFallbackResponseSnapshot(string appVersion)
        => new(BuildResponseValueFingerprint("catalog\0" + appVersion), null);

    private static string BuildResponseValueFingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    internal static (string Cursor, string? ResultStableAt) BuildFindResumeCursor(
        string[] args,
        DbReader reader,
        string resumePath,
        int resumeLine)
    {
        var snapshot = BuildResponseSnapshot(reader);
        return (
            FormatResponseCursor(
                offset: 0,
                BuildResponseFingerprint("find", args),
                snapshot.GenerationFingerprint,
                resumePath,
                resumeLine),
            snapshot.ResultStableAt);
    }

    internal static int GetBoundedResponseOffset(string command)
    {
        var execution = BoundedExecution.Value;
        return execution is not null
               && string.Equals(execution.Command, CanonicalizeCommandName(command), StringComparison.Ordinal)
            ? execution.Offset
            : 0;
    }

    internal static (string? Path, int? Line) GetBoundedFindResume()
    {
        var execution = BoundedExecution.Value;
        return execution is not null && string.Equals(execution.Command, "find", StringComparison.Ordinal)
            ? (execution.ResumePath, execution.ResumeLine)
            : (null, null);
    }

    internal static int? GetBoundedResponseLimit(string command)
    {
        var execution = BoundedExecution.Value;
        return execution is not null
               && string.Equals(execution.Command, CanonicalizeCommandName(command), StringComparison.Ordinal)
            ? execution.Limit
            : null;
    }

    internal static void ReportBoundedResponseTotal(
        string command,
        int totalCount,
        bool authoritative)
    {
        var execution = BoundedExecution.Value;
        if (execution is null
            || !string.Equals(execution.Command, CanonicalizeCommandName(command), StringComparison.Ordinal))
        {
            return;
        }

        execution.ReportedTotalCount = Math.Max(0, totalCount);
        execution.ReportedTotalCountAuthoritative = authoritative;
    }

    internal static string? GetBoundedMapCollection()
    {
        var execution = BoundedExecution.Value;
        if (execution is null || !string.Equals(execution.Command, "map", StringComparison.Ordinal))
            return null;
        return SelectRequestedCollection(
            execution.Fields,
            "languages",
            "modules",
            "top_files",
            "largest_files",
            "symbol_rich_files",
            "reference_rich_files",
            "entrypoints");
    }

    internal static bool IsBoundedMapScalarProjection()
    {
        var execution = BoundedExecution.Value;
        return execution is not null
               && string.Equals(execution.Command, "map", StringComparison.Ordinal)
               && GetBoundedMapCollection() is null
               && execution.Fields is { Count: > 0 };
    }

    internal static string? GetBoundedImpactCollection()
    {
        var execution = BoundedExecution.Value;
        return execution is not null && string.Equals(execution.Command, "impact", StringComparison.Ordinal)
            ? SelectRequestedCollection(execution.Fields, "callers", "file_impacts", "definitions")
            : null;
    }

    private static IDisposable EnterBoundedExecution(BoundedExecutionContext execution)
    {
        var previous = BoundedExecution.Value;
        BoundedExecution.Value = execution;
        return new BoundedExecutionScope(previous);
    }

    private sealed record BoundedExecutionContext(
        string Command,
        int Offset,
        int Limit,
        IReadOnlyList<string>? Fields,
        bool Compact,
        string? ResumePath,
        int? ResumeLine)
    {
        public int? ReportedTotalCount { get; set; }
        public bool ReportedTotalCountAuthoritative { get; set; }
    }

    private sealed class BoundedExecutionScope(BoundedExecutionContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            BoundedExecution.Value = previous;
            _disposed = true;
        }
    }
}
