using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    private const string ResponseCursorPrefix = "response:v1:";
    private static readonly AsyncLocal<BoundedExecutionContext?> BoundedExecution = new();

    private static readonly HashSet<string> BoundedResponseCommands = new(StringComparer.Ordinal)
    {
        "definition", "find", "status", "hotspots", "references", "callers", "callees", "impact", "map",
    };

    private static readonly HashSet<string> AutoWrapByteBudgetCommands = new(StringComparer.Ordinal)
    {
        "find", "status", "references", "callers", "callees",
    };

    private static readonly HashSet<string> AutoWrapCompactCommands = new(StringComparer.Ordinal)
    {
        "definition", "find", "status", "hotspots", "references", "callers", "callees", "impact", "map",
    };

    private static readonly HashSet<string> LegacyLocationCompactCommands = new(StringComparer.Ordinal)
    {
        "definition", "find", "references", "callers", "callees",
    };

    private static readonly HashSet<string> PageableResponseCommands = new(StringComparer.Ordinal)
    {
        "definition", "find", "hotspots", "references", "callers", "callees", "impact", "map",
    };

    private static readonly HashSet<string> CountableResponseCommands = new(StringComparer.Ordinal)
    {
        "definition", "find", "hotspots", "references", "callers", "callees", "impact",
    };

    private static readonly Dictionary<string, string[]> CompactFieldsByCommand = new(StringComparer.Ordinal)
    {
        ["definition"] = ["file", "line", "column"],
        ["find"] = ["file", "line", "column"],
        ["references"] = ["file", "line", "column"],
        ["callers"] = ["file", "line", "column"],
        ["callees"] = ["file", "line", "column"],
        ["hotspots"] = ["name", "kind", "path", "line", "reference_count", "reference_score", "ranking_score"],
        ["impact"] = ["path", "caller_name", "callee_name", "depth", "first_line", "reference_count", "result_kind"],
        ["status"] = ["api_version", "files", "chunks", "symbols", "references", "indexed_at", "git_head", "git_is_dirty", "head_freshness", "version", "graph_table_available", "hotspot_family_ready", "summary"],
        ["map"] = ["api_version", "file_count", "total_lines", "total_symbols", "total_references", "indexed_at", "git_head", "git_is_dirty", "head_freshness", "graph_table_available", "sections"],
    };

    internal static bool ShouldAutoWrapBoundedResponse(string command, string[] args)
    {
        if (!BoundedResponseCommands.Contains(command))
            return false;
        if (HasArgument(args, "--fields") || HasArgument(args, "--cursor"))
            return true;
        if (AutoWrapByteBudgetCommands.Contains(command) && HasArgument(args, "--max-json-bytes"))
            return true;
        return AutoWrapCompactCommands.Contains(command) && HasCompactOutputSelection(args);
    }

    private static bool IsBoundedResponseRequest(string command, string[] args)
    {
        if (!BoundedResponseCommands.Contains(command))
            return false;

        return HasArgument(args, "--fields")
               || HasArgument(args, "--cursor")
               || (HasEnvelopeFlag(args) && HasArgument(args, "--max-json-bytes"))
               || ShouldAutoWrapBoundedResponse(command, args);
    }

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
        var dbPathExplicit = TryExtractDbPath(args, out var explicitDbPath);
        var resolvedDbPath = string.IsNullOrWhiteSpace(explicitDbPath)
            ? Path.Combine(".cdidx", "codeindex.db")
            : explicitDbPath!;
        var fingerprint = BuildResponseFingerprint(command, args);
        if (controls.CursorFingerprint is not null
            && !string.Equals(controls.CursorFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return WriteBoundedResponseUsageError(
                "--cursor does not match this command, query, or filter set.",
                "Use next_cursor from the preceding page without changing query, filter, or sort arguments.");
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
        try
        {
            using var outputScope = ScopedConsoleOutput.Redirect(captured);
            using var executionScope = EnterBoundedExecution(command, controls.Offset, controls.PageLimit, controls.Fields, controls.Compact);
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

        PromoteEmptyLegacyCompactPayload(command, controls, rawResults, streamControlRecords);
        var extraction = ExtractResponseItems(command, rawResults, controls);
        var availableItems = extraction.Items;
        var pageItems = availableItems
            .Take(controls.PageLimit)
            .Select(item => ProjectResponseItem(item, controls.EffectiveFields(command, extraction.PrimaryCollection)))
            .ToList();

        var count = ResolveTotalCount(command, args, runInner, extraction, availableItems.Count, controls.Offset, streamTerminal);
        var totalCount = Math.Max(count.TotalCount, controls.Offset + pageItems.Count);
        var totalAuthoritative = count.Authoritative;
        var envelope = BuildBoundedEnvelopeWithinBudget(
            command,
            queryNormalized,
            resolvedDbPath,
            dbPathExplicit,
            appVersion,
            stopwatch.Elapsed.TotalMilliseconds,
            pageItems,
            controls,
            fingerprint,
            totalCount,
            totalAuthoritative,
            exitCode,
            extraction,
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
        string fingerprint,
        int totalCount,
        bool totalAuthoritative,
        int exitCode,
        ResponseExtraction extraction,
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
                streamTerminal: streamTerminal,
                streamControlRecords: streamControlRecords);
            var metadata = (JsonObject)envelope["metadata"]!;
            var nextOffset = controls.Offset + count;
            var paginationWindowExhausted = nextOffset < totalCount && nextOffset >= MaxPageWindow;
            var hasMore = count > 0 && nextOffset < totalCount && !paginationWindowExhausted;
            metadata["result_count"] = count;
            metadata["returned_count"] = count;
            metadata["total_count"] = totalCount;
            metadata["total_count_authoritative"] = totalAuthoritative;
            metadata["omitted_count"] = Math.Max(0, totalCount - count);
            metadata["remaining_count"] = Math.Max(0, totalCount - nextOffset);
            metadata["cursor_offset"] = controls.Offset;
            metadata["page_limit"] = controls.PageLimit;
            metadata["has_more"] = hasMore;
            metadata["next_cursor"] = hasMore && count > 0
                ? FormatResponseCursor(nextOffset, fingerprint)
                : null;
            metadata["truncated"] = totalCount > count;
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
                    fingerprint);
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
        string fingerprint)
    {
        var compatible = (JsonObject)sourcePayload.DeepClone();
        compatible["results"] = results.DeepClone();
        compatible["metadata"] = envelope["metadata"]!.DeepClone();
        compatible["count"] = returnedCount;
        compatible["returned_count"] = returnedCount;
        compatible["total_count"] = totalCount;
        compatible["total_count_authoritative"] = totalAuthoritative;
        compatible["omitted_count"] = Math.Max(0, totalCount - returnedCount);
        compatible["remaining_count"] = Math.Max(0, totalCount - nextOffset);
        compatible["cursor_offset"] = controls.Offset;
        compatible["page_limit"] = controls.PageLimit;
        compatible["has_more"] = hasMore;
        compatible["next_cursor"] = hasMore && returnedCount > 0
            ? FormatResponseCursor(nextOffset, fingerprint)
            : null;
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

        var rows = new JsonArray();
        foreach (var result in rawResults)
        {
            if (result is JsonObject obj && IsJsonStreamTerminal(obj))
                continue;
            rows.Add(result?.DeepClone());
        }
        return new ResponseExtraction(rows, null, null, null);
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
        if (streamTerminal is not null
            && TryReadInt(streamTerminal, "total_count", out var terminalTotal)
            && TryReadBool(streamTerminal, "total_count_authoritative", out var terminalAuthoritative))
            return new ResponseCount(terminalTotal, terminalAuthoritative);
        if (!CountableResponseCommands.Contains(command))
            return new ResponseCount(offset + availableCount, false);

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
            return new ResponseCount(offset + availableCount, false);
        }
        try
        {
            var countItems = ParseRawJsonItems(command, captured.ToString(), out _, out _);
            var countPayload = countItems.OfType<JsonObject>().FirstOrDefault(obj => obj.ContainsKey("count"));
            if (countPayload is null || !TryReadInt(countPayload, "count", out var total))
                return new ResponseCount(offset + availableCount, false);
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
            return new ResponseCount(offset + availableCount, false);
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
        string? cursorFingerprint = null;
        if (cursor is not null && !TryParseResponseCursor(cursor, out offset, out cursorFingerprint))
        {
            controls = default!;
            error = "--cursor must be a response:v1:<offset>:<fingerprint> cursor returned as next_cursor.";
            return false;
        }
        controls = new BoundedResponseControls(fields, compact, maxJsonBytes, pageLimit, offset, cursorFingerprint);
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
        var input = command + "\0" + string.Join('\0', normalized);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string FormatResponseCursor(int offset, string fingerprint)
        => $"{ResponseCursorPrefix}{offset.ToString(CultureInfo.InvariantCulture)}:{fingerprint}";

    private static bool TryParseResponseCursor(string cursor, out int offset, out string? fingerprint)
    {
        offset = 0;
        fingerprint = null;
        if (!cursor.StartsWith(ResponseCursorPrefix, StringComparison.Ordinal))
            return false;
        var remainder = cursor[ResponseCursorPrefix.Length..];
        var separator = remainder.IndexOf(':');
        if (separator <= 0 || separator == remainder.Length - 1)
            return false;
        if (!int.TryParse(remainder[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
            return false;
        fingerprint = remainder[(separator + 1)..];
        return fingerprint.Length == 16 && fingerprint.All(Uri.IsHexDigit);
    }

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
        string? CursorFingerprint)
    {
        public IReadOnlyList<string>? EffectiveFields(string command, string? primaryCollection)
        {
            var selected = Fields ?? (CompactFieldsByCommand.TryGetValue(command, out var defaults) ? defaults : null);
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

    internal static int GetBoundedResponseOffset(string command)
    {
        var execution = BoundedExecution.Value;
        return execution is not null
               && string.Equals(execution.Command, CanonicalizeCommandName(command), StringComparison.Ordinal)
            ? execution.Offset
            : 0;
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

    private static IDisposable EnterBoundedExecution(
        string command,
        int offset,
        int limit,
        IReadOnlyList<string>? fields,
        bool compact)
    {
        var previous = BoundedExecution.Value;
        BoundedExecution.Value = new BoundedExecutionContext(command, offset, limit, fields, compact);
        return new BoundedExecutionScope(previous);
    }

    private sealed record BoundedExecutionContext(
        string Command,
        int Offset,
        int Limit,
        IReadOnlyList<string>? Fields,
        bool Compact);

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
