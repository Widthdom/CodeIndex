using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

/// <summary>
/// MCP tool execution handlers (partial class split from McpServer.cs).
/// MCPツール実行ハンドラ（McpServer.csからのpartial class分割）。
/// </summary>
public partial class McpServer
{
    private sealed class IndexAuditContext
    {
        internal string? CheckedRootIdentity { get; set; }
    }

    private const int DefaultBatchQueryResponseByteLimit = MaxLineByteLength;
    internal const int MaxBatchQueryResponseByteLimit = 10 * 1024 * 1024;
    internal const int MaxBatchQuerySize = 10;
    private const int BatchQueryIncrementalEstimatePaddingBytes = 512;
    private const int DefaultExcerptOutputByteLimit = MaxLineByteLength;
    private const string BatchQueryResponseByteLimitEnvVar = "CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES";
    internal const int MaxMcpArrayFilterCount = QueryCommandRunner.MaxQueryPathFilterCount;
    internal const int MaxMcpArrayFilterStringLength = QueryCommandRunner.MaxQueryPathFilterLength;
    private static readonly HashSet<string> BoundedEnumLikeScalarArguments = new(StringComparer.Ordinal)
    {
        "category",
        "format",
        "groupBy",
        "alias",
        "capability",
        "extension",
        "kind",
        "lang",
        "language",
        "rankBy",
        "severity",
        "snippetFocus",
        "visibility",
        "excludeVisibility",
        "followSymlinks",
    };
    internal const int MaxMcpIndexFailureMessageLength = 512;
    internal static Action<string>? McpIndexFileCommittedForTesting { get; set; }
    internal static Action<string>? McpIndexFileContentLoadForTesting { get; set; }
    internal static Action? McpIndexAuthorizationCompletedForTesting { get; set; }
    internal static Action<string>? McpIndexEntryOpenBoundaryForTesting { get; set; }
    internal static Action<string>? McpIndexDirectoryEnumerationBoundaryForTesting { get; set; }
    internal static Action<string>? McpIndexDirectoryEnumerationCompletedForTesting { get; set; }
    internal static Action? McpIndexPostExtractionHookDiscoveryForTesting { get; set; }
    internal static Action? McpIndexFtsOptimizeForTesting { get; set; }
    internal static Action? McpIndexFtsMergeForTesting { get; set; }
    internal static Action<int>? McpIndexFtsStatPreflightBufferAllocatedForTesting { get; set; }
    internal static Action<int>? McpIndexRetainedPathFilterAllocatedForTesting { get; set; }
    internal static Action<int>? McpIndexStaleFilePurgePlannedForTesting { get; set; }
    internal static Action<bool>? McpIndexStaleFilePurgeForTesting { get; set; }
    internal static Func<CancellationToken, Task>? McpIndexStaleFilePurgedForTesting { get; set; }
    internal static Action? McpIndexReferencePurgeForTesting { get; set; }
    internal static Action? McpIndexCSharpPrepassForTesting { get; set; }
    internal static Action? McpIndexCSharpFinalStatRevalidationForTesting { get; set; }
    internal static Action? McpIndexCSharpReadinessValidationForTesting { get; set; }
    internal static Action? McpIndexCSharpMetadataResolveForTesting { get; set; }
    internal static Action? McpIndexTypeScriptAugmentationRebuildForTesting { get; set; }
    internal static Func<string, CancellationToken, UpdateCheckResult>? StatusUpdateCheckForTesting { get; set; }
    private QueryCommandRunner.ProjectFilterRootResolution? _projectFilterRootResolutionForCurrentToolCall;

    // --- Tool implementations / ツール実装 ---

    private static void AddRecoveryHint(JsonObject payload, string reason, string suggestedAction, string? tool = null, JsonObject? args = null)
    {
        var hint = new JsonObject
        {
            ["reason"] = reason,
            ["suggested_action"] = suggestedAction,
        };
        if (tool != null)
            hint["tool"] = tool;
        if (args != null)
            hint["args"] = args;
        payload["recovery_hint"] = hint;
    }

    private static void AddExactSubstringRecoveryHint(JsonObject payload, string query)
        => AddRecoveryHint(
            payload,
            SearchQueryAdvisor.ExactSubstringHintReason,
            SearchQueryAdvisor.McpExactSubstringSuggestedAction,
            "search",
            new JsonObject
            {
                ["query"] = query,
                ["exactSubstring"] = true,
                ["limit"] = 5,
            });

    private static void AddSymbolRecoveryHint(JsonObject payload, string query, string toolName, string? lang, string? kind, JsonNode? path)
    {
        var args = new JsonObject
        {
            ["query"] = query,
            ["limit"] = 5,
        };
        if (lang != null)
            args["lang"] = lang;
        if (kind != null)
            args["kind"] = kind;
        if (path != null)
            args["path"] = path.DeepClone();

        AddRecoveryHint(
            payload,
            "no_results",
            $"{toolName} returned no rows; relax exactName/path/lang/kind filters, try symbols for nearby names, or search related identifiers/error text before assuming the symbol is absent.",
            "symbols",
            args);
    }

    private static void AddNextStepSuggestion(JsonObject payload, string tool, JsonObject args, string suggestedAction)
    {
        payload["next_step_suggestion"] = new JsonObject
        {
            ["tool"] = tool,
            ["args"] = args,
            ["suggested_action"] = suggestedAction,
        };
    }

    private static JsonObject BuildExcerptArgs(string path, int startLine, int endLine)
        => new()
        {
            ["path"] = path,
            ["startLine"] = startLine,
            ["endLine"] = endLine,
        };

    private sealed class ArgumentAdjustmentCollector
    {
        private readonly JsonArray _warnings = [];
        private readonly JsonArray _adjustments = [];

        public int Count => _adjustments.Count;

        public void AddClamped(string argument, int requested, int effective, int minimum, int maximum)
        {
            var message = $"{argument} was clamped from {requested} to {effective} (server cap is [{minimum}, {maximum}]).";
            _warnings.Add(message);
            _adjustments.Add(new JsonObject
            {
                ["argument"] = argument,
                ["action"] = "clamped",
                ["requested"] = requested,
                ["effective"] = effective,
                ["minimum"] = minimum,
                ["maximum"] = maximum,
                ["message"] = message,
            });
        }

        public void AddIgnored(string argument, int requested, string reason)
        {
            var message = $"{argument} value {requested} was ignored: {reason}";
            _warnings.Add(message);
            _adjustments.Add(new JsonObject
            {
                ["argument"] = argument,
                ["action"] = "ignored",
                ["requested"] = requested,
                ["effective"] = null,
                ["message"] = message,
            });
        }

        public void AddWarning(string message)
        {
            _warnings.Add(message);
        }

        public void ApplyTo(JsonObject payload)
        {
            if (_warnings.Count > 0)
            {
                var warnings = payload["warnings"] as JsonArray ?? [];
                foreach (var warning in _warnings)
                    warnings.Add(warning?.DeepClone());
                payload["warnings"] = warnings;
            }
            if (_adjustments.Count > 0)
                payload["argument_adjustments"] = _adjustments.DeepClone();
        }
    }

    private static int ReadLimit(JsonNode? args, int defaultLimit, ArgumentAdjustmentCollector adjustments)
    {
        var requested = ReadOptionalIntArgument(args, "limit");
        var effective = Math.Clamp(requested ?? defaultLimit, 1, MaxLimit);
        if (requested.HasValue && requested.Value != effective)
            adjustments.AddClamped("limit", requested.Value, effective, 1, MaxLimit);
        return effective;
    }

    private static int ReadOffset(JsonNode? args, ArgumentAdjustmentCollector adjustments)
    {
        var requested = ReadOptionalIntArgument(args, "offset");
        var effective = Math.Clamp(requested ?? 0, 0, MaxMcpPaginationOffset);
        if (requested.HasValue && requested.Value != effective)
            adjustments.AddClamped("offset", requested.Value, effective, 0, MaxMcpPaginationOffset);
        return effective;
    }

    private static int ReadSnippetLines(JsonNode? args, int defaultSnippetLines, ArgumentAdjustmentCollector adjustments)
    {
        var requested = ReadOptionalIntArgument(args, "snippetLines");
        var effective = SearchSnippetFormatter.ClampSnippetLines(requested ?? defaultSnippetLines);
        if (requested.HasValue && requested.Value != effective)
            adjustments.AddClamped("snippetLines", requested.Value, effective, 1, SearchSnippetFormatter.MaxSnippetLines);
        return effective;
    }

    private static int? ReadMapDepth(JsonNode? args, ArgumentAdjustmentCollector adjustments)
    {
        var requested = ReadOptionalIntArgument(args, "depth");
        if (!requested.HasValue)
            return null;
        if (requested.Value < 0)
        {
            adjustments.AddIgnored("depth", requested.Value, "depth must be greater than or equal to 0.");
            return null;
        }

        var effective = Math.Min(requested.Value, MaxMcpMapDepth);
        if (effective != requested.Value)
            adjustments.AddClamped("depth", requested.Value, effective, 0, MaxMcpMapDepth);
        return effective;
    }

    private static int? ReadOptionalIntArgument(JsonNode? args, string propertyName)
        => args?[propertyName] is JsonValue value && value.TryGetValue<int>(out var parsed)
            ? parsed
            : null;

    private static string ReadResponseFormat(JsonNode? args)
        => args?["format"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "full";

    private static string? ValidateResponseFormat(string format)
        => format is "full" or "count" or "compact"
            ? null
            : "format must be one of full, count, compact";

    private static void ApplyCompactResults<T>(
        JsonObject payload,
        IEnumerable<T> results,
        Func<T, string> pathSelector,
        Func<T, int> lineSelector,
        Func<T, int?>? columnSelector = null)
    {
        var compact = new JsonArray();
        foreach (var result in results)
        {
            var row = new JsonObject
            {
                ["file"] = pathSelector(result),
                ["line"] = lineSelector(result),
            };
            var column = columnSelector?.Invoke(result);
            if (column.HasValue)
                row["column"] = column.Value;
            compact.Add(row);
        }
        payload["results"] = compact;
        payload["format"] = "compact";
    }

    /// <summary>
    /// Return true when the requested reference kind is NOT a call-graph kind (i.e. metadata
    /// `attribute` / `annotation`, compile-time `type_reference`, narrowing `type_tag`, or structural `import`) —
    /// these are valid on the `references` tool but must be rejected on `callers` / `callees`, whose data model
    /// cannot answer those queries correctly. Metadata rows are attributed to the enclosing
    /// body-range symbol rather than the annotated target (so file-level targets drop
    /// entirely and method-level metadata appears under the enclosing class); `type_reference`
    /// rows are compile-time type-position edges (declaration types, generic constraints,
    /// `is`/`as`/`instanceof`, XML-doc `cref`), not runtime calls, so they misreport type
    /// mentions as caller/callee edges; JavaScript/TypeScript `type_tag` rows describe
    /// discriminant narrowing rather than calls; `import` rows are structural dependency edges.
    /// `references` では有効だが `callers` / `callees` では構造的に誤答するため弾くべき kind
    /// （metadata: `attribute` / `annotation`、型位置: `type_reference`、narrowing: `type_tag`、構造 dependency: `import`）かを返す。metadata 行は
    /// 注釈対象ではなく body-range 上の外側シンボルに帰属し、`type_reference` は実行時呼び出し
    /// ではなく compile-time な型言及（宣言型、generic 制約、`is`/`as`/`instanceof`、XML-doc
    /// `cref` など）で、`type_tag` は JavaScript / TypeScript の discriminant narrowing metadata、
    /// `import` は構造的な dependency edge なので、`callers` / `callees` は
    /// いずれの kind にも正しく答えられない。
    /// </summary>
    private static bool IsNonCallGraphReferenceKind(string? kind) =>
        kind == "attribute" || kind == "annotation" || kind == "type_reference" || kind == "type_tag" || kind == "import";

    /// <summary>
    /// Build the CLI / MCP error message for a non-call-graph reference kind rejected on
    /// `callers` / `callees`. The message explains why the kind is structurally wrong on
    /// the command and redirects users to `references`.
    /// `callers` / `callees` で弾いた非 call-graph kind のエラーメッセージを組み立てる。
    /// 構造的に誤答する理由を説明し、`references` に誘導する。
    /// </summary>
    private static string BuildNonCallGraphKindRejectionMessage(string command, string kind) =>
        kind == "type_reference"
            ? $"'kind: type_reference' is not supported on '{command}'. Type-position references are compile-time edges (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`), not runtime calls, so `{command}` cannot return accurate rows for kind 'type_reference'. Use the 'references' tool with kind 'type_reference' instead."
            : kind == "type_tag"
                ? $"'kind: type_tag' is not supported on '{command}'. JavaScript/TypeScript discriminant tags are narrowing metadata, not runtime calls, so `{command}` cannot return accurate rows for kind 'type_tag'. Use the 'references' tool with kind 'type_tag' instead."
            : kind == "import"
                ? $"'kind: import' is not supported on '{command}'. Import references are structural dependency edges, not runtime calls, so `{command}` cannot return accurate rows for kind 'import'. Use the 'references' tool with kind 'import' instead."
            : $"'kind: {kind}' is not supported on '{command}'. Metadata references are attributed to the enclosing body-range symbol, so `{command}` cannot return accurate rows for kind '{kind}'. Use the 'references' tool with kind '{kind}' for metadata enumeration.";

    private JsonNode? TryGetValidatedMaxLineWidth(JsonNode? id, JsonNode? args, out int maxLineWidth, string propertyName = "maxLineWidth")
    {
        var maxLineWidthValue = ReadOptionalIntArgument(args, propertyName);
        if (maxLineWidthValue.HasValue && maxLineWidthValue.Value < 0)
        {
            maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth;
            return CreateToolErrorResponse(id, "maxLineWidth must be greater than or equal to 0");
        }

        if (maxLineWidthValue.HasValue && maxLineWidthValue.Value > LineWidthFormatter.MaxAllowedLineWidth)
        {
            maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth;
            return CreateToolErrorResponse(id, $"maxLineWidth must be less than or equal to {LineWidthFormatter.MaxAllowedLineWidth}");
        }

        maxLineWidth = maxLineWidthValue ?? LineWidthFormatter.DefaultMaxLineWidth;
        return null;
    }

    private static List<string> ReadStringList(JsonNode? args, string propertyName)
    {
        var node = args?[propertyName];
        if (node is JsonArray array)
        {
            return array.Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
        }

        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var scalarText) && !string.IsNullOrWhiteSpace(scalarText))
            return [scalarText];

        return [];
    }

    private static List<string> ReadStringOrArrayList(JsonNode? args, string propertyName)
    {
        var node = args?[propertyName];
        if (node is JsonArray array)
        {
            return array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
        }

        return node is JsonValue scalar && scalar.TryGetValue<string>(out var scalarText) && !string.IsNullOrWhiteSpace(scalarText)
            ? [scalarText]
            : [];
    }

    private static List<string> ReadStringOrCommaSeparatedList(JsonNode? args, string propertyName)
        => ReadStringOrArrayList(args, propertyName)
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private JsonNode? TryReadSearchGuardFilters(JsonNode? id, JsonNode? args, out List<SearchGuardFilter> filters)
    {
        filters = [];
        var collected = new List<SearchGuardFilter>();

        JsonNode? AddFilter(string propertyName, SearchGuardRole role, SearchGuardDirection direction, string value)
        {
            if (collected.Count >= DbReader.MaxSearchGuardFilters)
                return CreateToolErrorResponse(id, $"search accepts at most {DbReader.MaxSearchGuardFilters} guard filters; got {collected.Count + 1}.");

            if (string.IsNullOrWhiteSpace(value))
                return CreateToolErrorResponse(id, $"'{propertyName}' entries must be non-empty strings.");
            if (value.Length > QueryLimits.MaxQueryLength)
                return CreateToolErrorResponse(id, $"'{propertyName}' query too long (max {QueryLimits.MaxQueryLength} characters).");

            collected.Add(new SearchGuardFilter(role, direction, value));
            return null;
        }

        JsonNode? AddFilters(string propertyName, SearchGuardRole role, SearchGuardDirection direction)
        {
            var node = args?[propertyName];
            if (node is null)
                return null;

            if (node is JsonValue singleValue && singleValue.TryGetValue<string>(out var singleText))
                return AddFilter(propertyName, role, direction, singleText);

            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
                        return CreateToolErrorResponse(id, $"'{propertyName}' entries must be strings.");
                    if (AddFilter(propertyName, role, direction, text) is JsonNode addError)
                        return addError;
                }
                return null;
            }

            return CreateToolErrorResponse(id, $"'{propertyName}' must be a string or string array.");
        }

        if (AddFilters("requireBefore", SearchGuardRole.Require, SearchGuardDirection.Before) is JsonNode requireBeforeError)
            return requireBeforeError;
        if (AddFilters("requireAfter", SearchGuardRole.Require, SearchGuardDirection.After) is JsonNode requireAfterError)
            return requireAfterError;
        if (AddFilters("rejectBefore", SearchGuardRole.Reject, SearchGuardDirection.Before) is JsonNode rejectBeforeError)
            return rejectBeforeError;
        if (AddFilters("rejectAfter", SearchGuardRole.Reject, SearchGuardDirection.After) is JsonNode rejectAfterError)
            return rejectAfterError;

        filters = collected;
        return null;
    }

    private JsonNode? TryReadSearchGuardScope(JsonNode? id, JsonNode? args, out SearchGuardScope guardScope)
    {
        guardScope = SearchGuardScope.Window;
        var node = args?["guardScope"];
        if (node is null)
            return null;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var rawScope))
            return CreateToolErrorResponse(id, "'guardScope' must be a string: window or same-line.");

        switch (rawScope.Trim().ToLowerInvariant().Replace("_", "-"))
        {
            case "window":
                guardScope = SearchGuardScope.Window;
                return null;
            case "same-line":
            case "sameline":
                guardScope = SearchGuardScope.SameLine;
                return null;
            default:
                return CreateToolErrorResponse(id, $"'guardScope' must be window or same-line; got '{rawScope}'.");
        }
    }

    private JsonArray ToJsonArray<T>(IEnumerable<T> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(JsonSerializer.SerializeToNode(item, _jsonOptions));
        return array;
    }

    private JsonArray ToJsonArray<TSource, TResult>(IEnumerable<TSource> items, Func<TSource, TResult> selector)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(JsonSerializer.SerializeToNode(selector(item), _jsonOptions));
        return array;
    }

    private const int SearchEnvelopeMinCandidates = 200;
    private const int SearchEnvelopeOverFetchFactor = 50;
    private const int SearchEnvelopeMaxCandidates = 10_000;
    internal const int MaxMcpEnvelopeFetchLimit = MaxLimit + 1;

    private static int FetchLimitForEnvelope(int limit)
    {
        if (limit <= 0)
            return 1;

        var requested = (long)limit + 1;
        return (int)Math.Min(MaxMcpEnvelopeFetchLimit, requested);
    }

    internal static int FetchLimitForEnvelopeForTests(int limit) => FetchLimitForEnvelope(limit);

    private static int FetchLimitForSearchRecipeEnvelope(int limit)
    {
        if (limit <= 0)
            return 1;

        var requested = (long)limit + 1;
        var overFetched = requested * SearchEnvelopeOverFetchFactor;
        var candidateLimit = Math.Max(SearchEnvelopeMinCandidates, Math.Max(requested, overFetched));
        return (int)Math.Min(SearchEnvelopeMaxCandidates, candidateLimit);
    }

    private static bool TrimToRequestedLimit<T>(List<T> results, int limit)
    {
        if (results.Count <= limit)
            return false;

        results.RemoveRange(limit, results.Count - limit);
        return true;
    }

    private static void AddResultEnvelope(JsonObject payload, int returnedCount, int? total, bool truncated)
    {
        payload["count"] = returnedCount;
        payload["truncated"] = truncated;
        payload["more_available"] = truncated;
        payload["total"] = total.HasValue ? JsonValue.Create(total.Value) : null;
        if (truncated)
        {
            payload["pagination_hint"] = new JsonObject
            {
                ["suggested_action"] = "More rows are available; continue with the provided cursor/next_offset when you need breadth, or narrow with path/lang/kind/excludeTests/format filters before reading details.",
            };
        }
    }

    private static void AddPaginatedResultEnvelope(JsonObject payload, int returnedCount, int? total, bool truncated, int offset)
    {
        AddResultEnvelope(payload, returnedCount, total, truncated);
        payload["offset"] = offset;
        if (truncated)
            payload["next_offset"] = offset + returnedCount;
    }

    private static bool ReadCountOnly(JsonNode? args) => args?["countOnly"]?.GetValue<bool>() ?? args?["count_only"]?.GetValue<bool>() ?? false;

    private JsonArray BuildTopFileHistogram<T>(IEnumerable<T> results, Func<T, string?> pathSelector)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            var path = pathSelector(result);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            counts[path] = counts.TryGetValue(path, out var count) ? count + 1 : 1;
        }

        var histogram = new JsonArray();
        foreach (var (path, count) in counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(5))
        {
            histogram.Add(new JsonObject
            {
                ["path"] = path,
                ["count"] = count,
            });
        }

        return histogram;
    }

    private static bool MatchesRecipeFacetMetadata(CompactSearchResult result, SearchAuditRecipeQuery recipeQuery)
    {
        if (recipeQuery.MatchOrigins.Count > 0 &&
            !result.MatchOrigins.Any(origin => recipeQuery.MatchOrigins.Contains(origin, StringComparer.Ordinal)))
        {
            return false;
        }

        if (recipeQuery.ExcludeOrigins.Count > 0 &&
            result.MatchOrigins.Count > 0 &&
            result.MatchOrigins.All(origin => recipeQuery.ExcludeOrigins.Contains(origin, StringComparer.Ordinal)))
        {
            return false;
        }

        return recipeQuery.ResultKinds.Count == 0 ||
               result.ResultKinds.Any(kind => recipeQuery.ResultKinds.Contains(kind, StringComparer.Ordinal));
    }

    private JsonObject BuildCountOnlyPayload<T>(int count, int? total, bool truncated, IEnumerable<T> histogramSource, Func<T, string?> pathSelector)
    {
        var payload = new JsonObject
        {
            ["count_only"] = true,
            ["top_files"] = BuildTopFileHistogram(histogramSource, pathSelector),
            ["results"] = new JsonArray(),
        };
        AddResultEnvelope(payload, count, total, truncated);
        return payload;
    }

    private static JsonArray ToJsonStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static void AddVisibilityFilterEcho(JsonObject payload, IReadOnlyList<string> visibilityFilters, IReadOnlyList<string> excludeVisibilityFilters)
    {
        payload["visibility"] = ToJsonStringArray(visibilityFilters);
        payload["excludeVisibility"] = ToJsonStringArray(excludeVisibilityFilters);
    }

    private static JsonArray BuildCompactSymbolRows(IEnumerable<SymbolResult> results)
    {
        var rows = new JsonArray();
        foreach (var result in results)
            rows.Add(BuildCompactSymbolRow(result));
        return rows;
    }

    private static JsonObject BuildCompactSymbolRow(SymbolResult result)
    {
        var row = new JsonObject
        {
            ["file"] = result.Path,
            ["line"] = result.Line,
            ["kind"] = result.Kind,
            ["name"] = result.Name,
        };
        if (result.Lang != null)
            row["lang"] = result.Lang;
        if (result.Visibility != null)
            row["visibility"] = result.Visibility;
        if (result.ContainerName != null)
            row["container"] = result.ContainerName;
        return row;
    }

    private static JsonArray BuildCompactReferenceRows(IEnumerable<ReferenceResult> results)
    {
        var rows = new JsonArray();
        foreach (var result in results)
        {
            rows.Add(new JsonObject
            {
                ["file"] = result.Path,
                ["line"] = result.Line,
                ["column"] = result.Column,
                ["symbol"] = result.SymbolName,
                ["kind"] = result.ReferenceKind,
            });
        }
        return rows;
    }

    private static JsonArray BuildCompactCallerRows(IEnumerable<CallerResult> results)
    {
        var rows = new JsonArray();
        foreach (var result in results)
        {
            rows.Add(new JsonObject
            {
                ["file"] = result.Path,
                ["line"] = result.FirstLine,
                ["caller_kind"] = result.CallerKind,
                ["caller"] = result.CallerName,
                ["callee"] = result.CalleeName,
                ["reference_kind"] = result.ReferenceKind,
                ["reference_count"] = result.ReferenceCount,
            });
        }
        return rows;
    }

    private static JsonArray BuildCompactCalleeRows(IEnumerable<CalleeResult> results)
    {
        var rows = new JsonArray();
        foreach (var result in results)
        {
            rows.Add(new JsonObject
            {
                ["file"] = result.Path,
                ["line"] = result.FirstLine,
                ["column"] = result.FirstColumn,
                ["length"] = result.FirstLength,
                ["caller_kind"] = result.CallerKind,
                ["caller"] = result.CallerName,
                ["callee"] = result.CalleeName,
                ["reference_kind"] = result.ReferenceKind,
                ["reference_count"] = result.ReferenceCount,
            });
        }
        return rows;
    }

    private static JsonObject BuildUnusedSymbolsByBucket(IEnumerable<UnusedSymbolResult> results)
    {
        var resultList = results as IReadOnlyList<UnusedSymbolResult> ?? results.ToList();
        var buckets = new JsonObject();
        foreach (var bucket in QueryCommandRunner.OrderedUnusedBuckets)
            buckets[bucket] = new JsonArray();

        for (var index = 0; index < resultList.Count; index++)
        {
            var result = resultList[index];
            if (!buckets.TryGetPropertyValue(result.UnusedBucket, out var bucketNode)
                || bucketNode is not JsonArray bucketRows)
            {
                bucketRows = new JsonArray();
                buckets[result.UnusedBucket] = bucketRows;
            }
            bucketRows.Add(QueryCommandRunner.BuildUnusedBucketMembershipJson(result, index));
        }

        return buckets;
    }

    private JsonObject BuildAnalyzeSymbolCountPayload(SymbolAnalysisResult analysis, string? lang, JsonNode? pathEcho, bool excludeTests, int maxLineWidth)
    {
        var graphPaths = analysis.CandidateBundles is { Count: > 0 }
            ? analysis.CandidateBundles
                .SelectMany(bundle => bundle.References.Select(reference => reference.Path)
                    .Concat(bundle.Callers.Select(caller => caller.Path))
                    .Concat(bundle.Callees.Select(callee => callee.Path)))
            : analysis.References.Select(reference => reference.Path)
                .Concat(analysis.Callers.Select(caller => caller.Path))
                .Concat(analysis.Callees.Select(callee => callee.Path));
        var paths = analysis.Definitions.Select(definition => definition.Path).Concat(graphPaths);
        var payload = new JsonObject
        {
            ["format"] = "count",
            ["count_only"] = true,
            ["query"] = analysis.Query,
            ["lang"] = lang,
            ["path"] = pathEcho?.DeepClone(),
            ["excludeTests"] = excludeTests,
            ["maxLineWidth"] = maxLineWidth,
            ["file_found"] = analysis.File != null,
            ["definition_count"] = analysis.Definitions.Count,
            ["nearby_symbol_count"] = analysis.NearbySymbols.Count,
            ["reference_count"] = analysis.References.Count,
            ["caller_count"] = analysis.Callers.Count,
            ["callee_count"] = analysis.Callees.Count,
            ["graph_sections"] = JsonSerializer.SerializeToNode(analysis.GraphSections, _jsonOptions),
            ["candidate_count"] = analysis.CandidateCount,
            ["candidate_reference_count"] = analysis.CandidateBundles?.Sum(bundle => bundle.References.Count) ?? 0,
            ["candidate_caller_count"] = analysis.CandidateBundles?.Sum(bundle => bundle.Callers.Count) ?? 0,
            ["candidate_callee_count"] = analysis.CandidateBundles?.Sum(bundle => bundle.Callees.Count) ?? 0,
            ["candidate_bundles"] = BuildCompactCandidateBundles(analysis.CandidateBundles),
            ["graph_scope"] = analysis.GraphScope,
            ["selection_required"] = analysis.SelectionRequired,
            ["graph_language"] = analysis.GraphLanguage,
            ["graph_language_source"] = analysis.GraphLanguageSource,
            ["graph_language_confidence"] = analysis.GraphLanguageConfidence,
            ["graph_language_candidates"] = new JsonArray(analysis.GraphLanguageCandidates.Select(candidate => JsonValue.Create(candidate)).ToArray<JsonNode?>()),
            ["graph_language_conflict"] = analysis.GraphLanguageConflict,
            ["graph_supported"] = analysis.GraphSupported,
            ["graph_support_reason"] = analysis.GraphSupportReason,
            ["graph_table_available"] = analysis.GraphTableAvailable,
            ["workspace_indexed_at"] = JsonSerializer.SerializeToNode(analysis.WorkspaceIndexedAt, _jsonOptions),
            ["workspace_latest_modified"] = JsonSerializer.SerializeToNode(analysis.WorkspaceLatestModified, _jsonOptions),
            ["top_files"] = BuildTopFileHistogram(paths, path => path),
            ["results"] = new JsonArray(),
        };
        if (analysis.ExactIndexAvailable.HasValue)
            payload["exact_index_available"] = analysis.ExactIndexAvailable.Value;
        if (analysis.DegradedReason != null)
            payload["degraded_reason"] = analysis.DegradedReason;
        AddAnalyzeSymbolProvenance(payload, analysis);
        return payload;
    }

    private JsonObject BuildAnalyzeSymbolCompactPayload(SymbolAnalysisResult analysis, string? lang, JsonNode? pathEcho, bool excludeTests, int maxLineWidth)
    {
        var payload = new JsonObject
        {
            ["api_version"] = analysis.ApiVersion,
            ["format"] = "compact",
            ["query"] = analysis.Query,
            ["lang"] = lang,
            ["path"] = pathEcho?.DeepClone(),
            ["excludeTests"] = excludeTests,
            ["maxLineWidth"] = maxLineWidth,
            ["file"] = analysis.File == null
                ? null
                : new JsonObject
                {
                    ["path"] = analysis.File.Path,
                    ["lang"] = analysis.File.Lang,
                    ["lines"] = analysis.File.Lines,
                    ["size"] = analysis.File.Size,
                },
            ["workspace_indexed_at"] = JsonSerializer.SerializeToNode(analysis.WorkspaceIndexedAt, _jsonOptions),
            ["workspace_latest_modified"] = JsonSerializer.SerializeToNode(analysis.WorkspaceLatestModified, _jsonOptions),
            ["graph_language"] = analysis.GraphLanguage,
            ["graph_language_source"] = analysis.GraphLanguageSource,
            ["graph_language_confidence"] = analysis.GraphLanguageConfidence,
            ["graph_language_candidates"] = new JsonArray(analysis.GraphLanguageCandidates.Select(candidate => JsonValue.Create(candidate)).ToArray<JsonNode?>()),
            ["graph_language_conflict"] = analysis.GraphLanguageConflict,
            ["graph_supported"] = analysis.GraphSupported,
            ["graph_support_reason"] = analysis.GraphSupportReason,
            ["graph_table_available"] = analysis.GraphTableAvailable,
            ["definition_count"] = analysis.Definitions.Count,
            ["nearby_symbol_count"] = analysis.NearbySymbols.Count,
            ["reference_count"] = analysis.References.Count,
            ["caller_count"] = analysis.Callers.Count,
            ["callee_count"] = analysis.Callees.Count,
            ["graph_sections"] = JsonSerializer.SerializeToNode(analysis.GraphSections, _jsonOptions),
            ["candidate_count"] = analysis.CandidateCount,
            ["graph_scope"] = analysis.GraphScope,
            ["selection_required"] = analysis.SelectionRequired,
            ["definitions"] = BuildCompactSymbolRows(analysis.Definitions),
            ["nearby_symbols"] = BuildCompactSymbolRows(analysis.NearbySymbols),
            ["references"] = BuildCompactReferenceRows(analysis.References),
            ["callers"] = BuildCompactCallerRows(analysis.Callers),
            ["callees"] = BuildCompactCalleeRows(analysis.Callees),
            ["candidate_bundles"] = BuildCompactCandidateBundles(analysis.CandidateBundles),
        };
        if (analysis.ExactIndexAvailable.HasValue)
            payload["exact_index_available"] = analysis.ExactIndexAvailable.Value;
        if (analysis.DegradedReason != null)
            payload["degraded_reason"] = analysis.DegradedReason;
        AddAnalyzeSymbolProvenance(payload, analysis);
        return payload;
    }

    private JsonObject ToAnalyzeSymbolJsonObject(SymbolAnalysisResult analysis)
    {
        var payload = new JsonObject
        {
            ["api_version"] = analysis.ApiVersion,
            ["query"] = analysis.Query,
            ["file"] = JsonSerializer.SerializeToNode(analysis.File, _jsonOptions),
            ["workspace_indexed_at"] = JsonSerializer.SerializeToNode(analysis.WorkspaceIndexedAt, _jsonOptions),
            ["workspace_latest_modified"] = JsonSerializer.SerializeToNode(analysis.WorkspaceLatestModified, _jsonOptions),
            ["graph_language"] = analysis.GraphLanguage,
            ["graph_language_source"] = analysis.GraphLanguageSource,
            ["graph_language_confidence"] = analysis.GraphLanguageConfidence,
            ["graph_language_candidates"] = new JsonArray(analysis.GraphLanguageCandidates.Select(candidate => JsonValue.Create(candidate)).ToArray<JsonNode?>()),
            ["graph_language_conflict"] = analysis.GraphLanguageConflict,
            ["graph_supported"] = analysis.GraphSupported,
            ["graph_support_reason"] = analysis.GraphSupportReason,
            ["definitions"] = ToJsonArray(analysis.Definitions),
            ["nearby_symbols"] = ToJsonArray(analysis.NearbySymbols),
            ["references"] = ToJsonArray(analysis.References),
            ["callers"] = ToJsonArray(analysis.Callers),
            ["callees"] = ToJsonArray(analysis.Callees),
            ["graph_sections"] = JsonSerializer.SerializeToNode(analysis.GraphSections, _jsonOptions),
            ["candidate_count"] = analysis.CandidateCount,
            ["graph_scope"] = analysis.GraphScope,
            ["selection_required"] = analysis.SelectionRequired,
            ["candidate_bundles"] = JsonSerializer.SerializeToNode(analysis.CandidateBundles, _jsonOptions),
            ["graph_table_available"] = analysis.GraphTableAvailable,
        };
        AddAnalyzeSymbolProvenance(payload, analysis);
        if (analysis.GraphDegraded.HasValue)
            payload["graph_degraded"] = analysis.GraphDegraded.Value;
        if (analysis.UnsupportedSymbolKind != null)
            payload["unsupported_symbol_kind"] = analysis.UnsupportedSymbolKind;
        if (analysis.SqlGraphContractReady.HasValue)
            payload["sql_graph_contract_ready"] = analysis.SqlGraphContractReady.Value;
        if (analysis.SqlGraphContractDegradedReason != null)
            payload["sql_graph_contract_degraded_reason"] = analysis.SqlGraphContractDegradedReason;
        if (analysis.ExactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(analysis.ExactZeroHint, _jsonOptions);
        if (analysis.ExactIndexAvailable.HasValue)
            payload["exact_index_available"] = analysis.ExactIndexAvailable.Value;
        if (analysis.DegradedReason != null)
            payload["degraded_reason"] = analysis.DegradedReason;
        return payload;
    }

    private static void AddAnalyzeSymbolProvenance(
        JsonObject payload,
        SymbolAnalysisResult analysis)
    {
        if (analysis.ProjectRoot != null)
            payload["project_root"] = analysis.ProjectRoot;
        if (analysis.GitHead != null)
            payload["git_head"] = analysis.GitHead;
        if (analysis.GitIsDirty.HasValue)
            payload["git_is_dirty"] = analysis.GitIsDirty.Value;
        if (analysis.IndexedHeadCommit != null)
            payload["indexed_head_commit"] = analysis.IndexedHeadCommit;
        if (analysis.WorkspaceVerifiedHeadSha != null)
            payload["workspace_verified_head_sha"] = analysis.WorkspaceVerifiedHeadSha;
        if (analysis.IndexedHeadSha != null)
            payload["indexed_head_sha"] = analysis.IndexedHeadSha;
        if (analysis.WorktreeHeadChanged.HasValue)
            payload["worktree_head_changed"] = analysis.WorktreeHeadChanged.Value;
    }

    private JsonArray BuildCompactCandidateBundles(List<SymbolCandidateBundle>? bundles)
    {
        var result = new JsonArray();
        if (bundles == null)
            return result;

        foreach (var bundle in bundles)
        {
            result.Add(new JsonObject
            {
                ["selector"] = JsonSerializer.SerializeToNode(bundle.Selector, _jsonOptions),
                ["definition"] = BuildCompactSymbolRow(bundle.Definition),
                ["identity_scoped"] = bundle.IdentityScoped,
                ["identity_scope_reason"] = bundle.IdentityScopeReason,
                ["graph_supported"] = bundle.GraphSupported,
                ["graph_support_reason"] = bundle.GraphSupportReason,
                ["nearby_symbol_count"] = bundle.NearbySymbols.Count,
                ["reference_count"] = bundle.References.Count,
                ["caller_count"] = bundle.Callers.Count,
                ["callee_count"] = bundle.Callees.Count,
                ["graph_sections"] = JsonSerializer.SerializeToNode(bundle.GraphSections, _jsonOptions),
            });
        }
        return result;
    }

    /// <summary>
    /// Read a path filter argument that accepts either a scalar string or an array of strings.
    /// Returns null when the value is missing or empty so downstream SQL omits the filter.
    /// スカラー文字列と文字列配列の両方を受け付けるパスフィルタを読み取る。
    /// 値が無い/空なら null を返し下流 SQL でフィルタを省略する。
    /// </summary>
    private static List<string>? ReadPathList(JsonNode? args, string propertyName)
    {
        var node = args?[propertyName];
        if (node is null)
            return null;
        if (node is JsonArray array)
        {
            var list = array.Select(n => n?.GetValue<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Cast<string>()
                .ToList();
            return list.Count > 0 ? list : null;
        }
        // Scalar string (backward compat) / スカラー文字列（後方互換）
        var value = node.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : new List<string> { value };
    }

    private List<string>? ReadScopedPathList(JsonNode? args)
    {
        _projectFilterRootResolutionForCurrentToolCall = null;
        var paths = ReadPathList(args, "path") ?? [];
        var projects = ReadPathList(args, "project") ?? [];
        if (projects.Count == 0)
            return paths.Count == 0 ? null : paths;

        var solution = args?["solution"]?.GetValue<string>();
        var projectRoot = ResolveProjectFilterRoot();
        _projectFilterRootResolutionForCurrentToolCall = projectRoot;
        foreach (var glob in SolutionProjectResolver.ResolveProjectDirectoryGlobs(projectRoot.Root, projects, solution))
            paths.Add(glob);
        return paths.Count == 0 ? null : paths;
    }

    private JsonObject? ValidateProjectFilterArguments(JsonNode? args)
    {
        var projects = ReadPathList(args, "project") ?? [];
        if (projects.Count == 0)
            return null;

        var solution = args?["solution"]?.GetValue<string>();
        var projectRoot = ResolveProjectFilterRoot();
        try
        {
            _ = SolutionProjectResolver.ResolveProjectDirectoryGlobs(projectRoot.Root, projects, solution);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var diagnostic = FormatMcpCaughtExceptionDiagnostic(ex);
            var error = new JsonObject
            {
                ["message"] = $"Project filter could not be resolved: {diagnostic.Text}",
                ["parameter"] = "project",
                ["diagnostic"] = diagnostic.Text,
                ["project_filter_root"] = projectRoot.Root,
            };
            if (!string.IsNullOrWhiteSpace(projectRoot.FallbackReason))
                error["project_filter_root_fallback_reason"] = projectRoot.FallbackReason;
            diagnostic.AddMetadata(error, "diagnostic");
            return error;
        }
    }

    private QueryCommandRunner.ProjectFilterRootResolution ResolveProjectFilterRoot()
        => QueryCommandRunner.ResolveProjectFilterRoot(_dbPath, _dbPathExplicit);

    private void AddProjectFilterRootDiagnostics(JsonObject payload)
    {
        var projectRoot = _projectFilterRootResolutionForCurrentToolCall;
        _projectFilterRootResolutionForCurrentToolCall = null;
        if (!projectRoot.HasValue)
            return;

        payload["project_filter_root"] = projectRoot.Value.Root;
        if (!string.IsNullOrWhiteSpace(projectRoot.Value.FallbackReason))
            payload["project_filter_root_fallback_reason"] = projectRoot.Value.FallbackReason;
    }

    private void ClearProjectFilterRootDiagnostics()
        => _projectFilterRootResolutionForCurrentToolCall = null;

    private static bool TryReadSinceArgument(JsonNode? args, out DateTime? since, out string? error)
    {
        var sinceStr = args?["since"]?.GetValue<string>();
        if (sinceStr == null)
        {
            since = null;
            error = null;
            return true;
        }

        if (QueryCommandRunner.TryParseIso8601Since(sinceStr, out var parsedSince))
        {
            since = parsedSince;
            error = null;
            return true;
        }

        since = null;
        error = $"Invalid 'since' timestamp: '{sinceStr}'. Use ISO 8601 format (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).";
        return false;
    }

    private static bool TryReadRequiredStringParameter(JsonNode? args, string propertyName, out string value, out string? error)
    {
        var node = args?[propertyName];
        if (node is null)
        {
            value = string.Empty;
            error = $"Missing required parameter: {propertyName}";
            return false;
        }

        value = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"Parameter \"{propertyName}\" cannot be empty or whitespace-only";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadRequiredPathParameter(JsonNode? args, string propertyName, out string value, out string? error)
    {
        if (!TryReadRequiredStringParameter(args, propertyName, out value, out error))
            return false;

        return McpPathBoundary.TryValidateWorkspaceRelativePath(value, MaxMcpArrayFilterStringLength, propertyName, out error);
    }

    private static bool TryReadRequiredIndexPathParameter(JsonNode? args, string propertyName, out string value, out string? error)
    {
        if (!TryReadRequiredStringParameter(args, propertyName, out value, out error))
            return false;

        if (value.Length > MaxMcpArrayFilterStringLength)
        {
            error = $"Parameter \"{propertyName}\" must be no longer than {MaxMcpArrayFilterStringLength} characters.";
            return false;
        }

        if (value.IndexOf("\0", StringComparison.Ordinal) >= 0)
        {
            error = $"Parameter \"{propertyName}\" must not contain NUL bytes.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool HasBlankPathFilter(JsonNode? args)
    {
        var node = args?["path"];
        if (node is null)
            return false;

        if (node is JsonArray array)
            return array.Count > 0 && array.All(item => string.IsNullOrWhiteSpace(item?.GetValue<string>()));

        return string.IsNullOrWhiteSpace(node.GetValue<string>());
    }

    /// <summary>
    /// Serialize a path filter list back into a JSON echo value.
    /// Null/empty → JSON null; single element → string; multiple → array.
    /// パスフィルタリストをJSONエコー値として直列化。
    /// null/空 → JSON null、1要素 → 文字列、複数 → 配列。
    /// </summary>
    private static JsonNode? PathEcho(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
            return null;
        if (paths.Count == 1)
            return JsonValue.Create(paths[0]);
        var arr = new JsonArray();
        foreach (var p in paths)
            arr.Add(JsonValue.Create(p));
        return arr;
    }

    private static string BuildGraphSummary(string singular, string plural, int count, string? lang, bool? graphSupported, string? graphSupportReason = null)
    {
        if (count > 0)
            return $"Found {ConsoleUi.Counted(count, singular, plural)}.";

        if (graphSupported == false && lang != null)
            return $"No {plural} found. Call-graph queries are not indexed for '{lang}'.";

        return $"No {plural} found.";
    }

    private static (string? GraphLanguage, bool? GraphSupported, string? GraphSupportReason)
        ResolveGraphSupport(DbReader reader, bool exact, string query, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string> excludePaths, bool excludeTests)
    {
        var graphLanguage = lang ?? (exact
            ? reader.GetExactGraphSupportedDefinitionLanguage(query, lang, pathPatterns, excludePaths, excludeTests)
            : null);
        var graphSupported = graphLanguage == null ? (bool?)null : reader.SupportsReferenceLanguage(graphLanguage);
        var graphSupportReason = ReferenceExtractor.BuildGraphSupportReason(graphLanguage, graphSupported);
        return (
            GraphLanguage: graphLanguage,
            GraphSupported: graphSupported,
            GraphSupportReason: graphSupportReason);
    }

    private static bool TryReadReferenceRankMode(JsonNode? args, out ReferenceRankMode rankMode, out string? error)
    {
        var value = args?["rankBy"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            rankMode = ReferenceRankMode.Weighted;
            error = null;
            return true;
        }

        if (QueryCommandRunner.TryParseReferenceRankMode(value, out rankMode))
        {
            error = null;
            return true;
        }

        error = $"rankBy must be one of weighted, count, kind; got '{value}'.";
        return false;
    }

    private static string FormatLiteralSearchQueryLimitError()
        => $"literal search query is too long; maximum is {DbReader.MaxLiteralSearchQueryLength} characters. Split generated input into smaller queries.";

    private static string FormatSearchGuardCandidateLimitError(SearchGuardCandidateLimitException ex)
        => $"guarded search is too broad: {FormatSearchGuardCandidateLimitDetail(ex)} Narrow the search with more specific query text, lang/path filters, or a smaller cursor offset.";

    private static string FormatSearchRecipeGuardCandidateLimitError(string recipeName, string queryName, SearchGuardCandidateLimitException ex)
    {
        var recipeDisplay = McpBoundedText.ForDisplay(recipeName).Text;
        var queryDisplay = McpBoundedText.ForDisplay(queryName).Text;
        return $"guarded search is too broad for recipe '{recipeDisplay}' query '{queryDisplay}': {FormatSearchGuardCandidateLimitDetail(ex)} Narrow the search with more specific path/lang filters or guards.";
    }

    private static string FormatSearchGuardCandidateLimitDetail(SearchGuardCandidateLimitException ex)
    {
        var detail = $"inspected the maximum {ex.CandidateLimit} candidate chunks before satisfying the requested page (limit {ex.RequestedLimit}, offset {ex.RequestedOffset}).";
        if (ex.CandidatePreviewPaths.Count > 0)
            detail += $" Candidate files sampled before refusal: {string.Join(", ", ex.CandidatePreviewPaths)}.";
        if (ex.CandidatePreviewLanguages.Count > 0)
            detail += $" Candidate languages sampled before refusal: {string.Join(", ", ex.CandidatePreviewLanguages)}.";
        detail += " Use count/count-by style search without guard filters to size the broad query before retrying.";
        return detail;
    }

    private static BoundedMcpText FormatMcpCaughtExceptionDiagnostic(Exception ex)
        => McpBoundedText.ForDisplay(CommandErrorWriter.FormatSanitizedException(ex));

    private static void AddImpactFailureFields(JsonObject payload, ImpactAnalysisResult analysis)
    {
        if (analysis.ImpactFailureChain is { Count: > 0 })
        {
            var chain = new JsonArray();
            foreach (var code in analysis.ImpactFailureChain)
                chain.Add(JsonValue.Create(code));
            payload["impact_failure_chain"] = chain;
        }

        if (analysis.SuggestionType != null)
            payload["suggestion_type"] = analysis.SuggestionType;
    }

    private JsonNode ExecuteValidate(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var severity = args?["severity"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (severity is not (null or "error" or FileIssue.SeverityWarning or FileIssue.SeverityInfo))
            return CreateToolErrorResponse(id, "severity must be one of error, warning, info");
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var rawCursor = args?["cursor"]?.GetValue<string>();
        if (countOnly && rawCursor is not null)
            return CreateToolErrorResponse(id, "cursor is not supported when validate uses countOnly or format=count.");
        McpQueryCursor? cursor = null;
        if (rawCursor is not null && !TryParseMcpQueryCursor(rawCursor, out cursor))
        {
            return CreateMcpCursorError(
                id,
                "validate",
                "cursor_malformed",
                "cursor must be an opaque response:v2 next_cursor returned by validate.",
                stale: false);
        }

        return WithDbReader(id, args, reader => reader.RunInReadSnapshot(() =>
        {
            var queryFingerprint = BuildMcpQueryFingerprint(
                "validate",
                limit,
                format,
                new Dictionary<string, string?>
                {
                    ["kind"] = kind,
                    ["severity"] = severity,
                    ["exclude-tests"] = excludeTests ? "true" : "false",
                },
                ("path", pathPatterns, PreserveOrder: false),
                ("exclude-path", excludePaths, PreserveOrder: false));
            var generation = BuildMcpGenerationFingerprint(reader, includeIssueState: true);
            var issuesTableAvailable = reader._hasIssuesPhysicalTable;
            var issuesDataCurrent = reader.IsIssueDataCurrentInSnapshot();
            var total = issuesDataCurrent
                ? reader.CountIssues(
                    kind,
                    pathPatterns,
                    excludePaths,
                    excludeTests,
                    severity)
                : 0;
            if (ValidateMcpQueryCursor(
                    id,
                    "validate",
                    cursor,
                    queryFingerprint,
                    generation.Fingerprint,
                    total) is JsonObject cursorError)
            {
                return cursorError;
            }
            var offset = cursor?.Offset ?? 0;
            var issues = issuesDataCurrent
                ? reader.GetIssues(
                    kind,
                    pathPatterns,
                    excludePaths,
                    excludeTests,
                    limit: countOnly ? null : limit,
                    severity: severity,
                    offset: offset)
                : [];
            QueryCommandRunner.AnnotateValidateIssues(issues);
            var pathFilterArray = new JsonArray();
            if (pathPatterns is not null)
            {
                foreach (var path in pathPatterns)
                    pathFilterArray.Add(path);
            }
            var issueSummary = QueryCommandRunner.BuildValidateIssueSummary(issues);
            issueSummary["authoritative"] = issuesDataCurrent;
            if (!issuesDataCurrent)
                issueSummary["actionability"] = "unknown";
            var payload = new JsonObject
            {
                ["count"] = countOnly ? total : issues.Count,
                ["filters"] = new JsonObject
                {
                    ["kind"] = kind,
                    ["severity"] = severity,
                    ["path"] = pathFilterArray,
                    ["exclude_paths"] = JsonSerializer.SerializeToNode(excludePaths),
                    ["exclude_tests"] = excludeTests,
                },
                ["summary"] = issueSummary,
                ["top_files"] = BuildTopFileHistogram(issues, issue => issue.Path),
                ["issues_table_available"] = issuesTableAvailable,
                ["file_issues_data_current"] = issuesDataCurrent,
            };
            if (countOnly)
            {
                payload["format"] = "count";
                payload["truncated"] = false;
                payload["more_available"] = false;
            }
            else if (format == "compact")
            {
                payload["format"] = "compact";
                payload["issues"] = BuildCompactValidateIssues(issues);
            }
            else
            {
                payload["issues"] = JsonSerializer.SerializeToNode(issues, _jsonOptions);
            }
            if (!countOnly)
            {
                AddMcpPaginationEnvelope(
                    payload,
                    total,
                    issues.Count,
                    offset,
                    limit,
                    queryFingerprint,
                    generation,
                    totalCountAuthoritative: issuesDataCurrent);
            }
            var summary = !issuesDataCurrent
                ? issuesTableAvailable
                    ? "Validation issue data is not current; results are non-authoritative."
                    : "Validation issue data is unavailable; results are non-authoritative."
                : issues.Count > 0
                    ? $"Found {issues.Count} encoding issue(s)."
                    : total > 0
                        ? "No more encoding issues found."
                        : "No encoding issues found.";
            adjustments.ApplyTo(payload);
            return CreateToolResult(id, summary, payload);
        }));
    }

    private static JsonArray BuildCompactValidateIssues(IEnumerable<FileIssue> issues)
    {
        var compact = new JsonArray();
        foreach (var issue in issues)
        {
            compact.Add(new JsonObject
            {
                ["path"] = issue.Path,
                ["line"] = issue.Line,
                ["kind"] = issue.Kind,
                ["severity"] = issue.Severity,
                ["origin"] = issue.Origin,
                ["category"] = issue.Category,
                ["actionable"] = issue.Actionable,
                ["message"] = issue.Message,
            });
        }
        return compact;
    }

    private JsonNode ExecutePing(JsonNode? id)
    {
        var payload = new JsonObject
        {
            ["version"] = _version,
            ["timestamp"] = GetUtcNow().ToString("O"),
            ["db_path"] = _dbPath,
            ["db_exists"] = File.Exists(LongPath.EnsureWindowsPrefix(_dbPath)),
        };
        return CreateToolResult(id, $"cdidx v{_version} is ready.", payload);
    }

    private static bool IsKnownLanguageCapability(string capability) =>
        LanguageCapabilityCatalog.IsKnownCapability(capability);

    private static bool LanguageMatchesCapability(bool symbols, bool references, bool graph, string capability) =>
        capability switch
        {
            "symbols" => symbols,
            "references" => references,
            "graph" => graph,
            _ => false,
        };

    private sealed record McpIndexUnsupportedMode(string Name, string Reason, bool BlocksIndexing);

    private static FileIssue BuildMcpSymbolCountExceededIssue(string path, int symbolCount, int maxSymbolsPerFile) =>
        new()
        {
            Path = path,
            Kind = "symbol_count_exceeded",
            Line = 0,
            Message = $"Symbol extraction produced {symbolCount:N0} symbols, exceeding the maxSymbolsPerFile limit of {maxSymbolsPerFile:N0}; file content, symbols, and references were not indexed. Exclude the generated/pathological file or raise maxSymbolsPerFile if this is expected.",
        };

    private static FileIssue BuildMcpReferenceCountExceededIssue(string path, int referenceCount, int maxReferencesPerFile) =>
        new()
        {
            Path = path,
            Kind = "reference_count_exceeded",
            Line = 0,
            Message = $"Reference extraction produced {referenceCount:N0} references, exceeding the maxReferencesPerFile limit of {maxReferencesPerFile:N0}; references were not indexed for this file. Exclude the generated/pathological file or raise maxReferencesPerFile if this is expected.",
        };

    private static bool TryReadMcpIndexSymlinkPolicy(JsonNode? args, out FileIndexer.SymlinkPolicy symlinkPolicy, out string? error)
    {
        symlinkPolicy = FileIndexer.SymlinkPolicy.None;
        error = null;
        var value = args?["followSymlinks"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value == "none")
            return true;
        if (value == "internal")
        {
            symlinkPolicy = FileIndexer.SymlinkPolicy.Internal;
            return true;
        }
        if (value == "all")
        {
            symlinkPolicy = FileIndexer.SymlinkPolicy.All;
            return true;
        }

        error = "followSymlinks must be one of none, internal, all";
        return false;
    }

    private static string FormatMcpIndexSymlinkPolicy(FileIndexer.SymlinkPolicy symlinkPolicy)
        => symlinkPolicy switch
        {
            FileIndexer.SymlinkPolicy.Internal => "internal",
            FileIndexer.SymlinkPolicy.All => "all",
            _ => "none",
        };

    private static List<McpIndexUnsupportedMode> BuildMcpIndexUnsupportedModes(JsonNode? args, int? requestedParallelism, int? requestedDebounce)
    {
        var modes = new List<McpIndexUnsupportedMode>();
        if (requestedParallelism.HasValue && requestedParallelism.Value != 1)
            modes.Add(new McpIndexUnsupportedMode("parallelism", "MCP index currently runs serially; requested parallelism is reported but effective_parallelism remains 1.", false));
        if (ReadStringOrArrayList(args, "commits").Count > 0)
            modes.Add(new McpIndexUnsupportedMode("commits", "Commit-scoped MCP indexing is not implemented; use CLI `cdidx index --commits ...` for this scope.", true));
        if (ReadStringOrArrayList(args, "changedBetween").Count > 0)
            modes.Add(new McpIndexUnsupportedMode("changedBetween", "Changed-between MCP indexing is not implemented; use CLI `cdidx index --changed-between ...` for this scope.", true));
        if (ReadStringOrArrayList(args, "files").Count > 0)
            modes.Add(new McpIndexUnsupportedMode("files", "File-scoped MCP indexing is not implemented; use CLI `cdidx index --files ...` for this scope.", true));

        var watchRequested = args?["watch"]?.GetValue<bool>() ?? false;
        if (watchRequested)
        {
            modes.Add(new McpIndexUnsupportedMode("watch", "Long-running watch mode is intentionally disabled for MCP tool calls; use the CLI watch command instead.", true));
        }
        else if (requestedDebounce.HasValue)
        {
            modes.Add(new McpIndexUnsupportedMode("debounce", "debounce only applies to watch mode, which is disabled for MCP tool calls.", false));
        }
        return modes;
    }

    private static JsonArray BuildMcpIndexUnsupportedModesJson(IEnumerable<McpIndexUnsupportedMode> unsupportedModes)
    {
        var array = new JsonArray();
        foreach (var mode in unsupportedModes)
        {
            array.Add(new JsonObject
            {
                ["name"] = mode.Name,
                ["reason"] = mode.Reason,
                ["blocks_indexing"] = mode.BlocksIndexing,
            });
        }
        return array;
    }

    private static bool HasBlockingMcpIndexUnsupportedMode(IEnumerable<McpIndexUnsupportedMode> unsupportedModes)
        => unsupportedModes.Any(mode => mode.BlocksIndexing);

    private static JsonObject CaptureMcpIndexMemorySample(string stage, Stopwatch stopwatch)
    {
        var snapshot = ProcessMemorySnapshot.Capture();
        return new JsonObject
        {
            ["stage"] = stage,
            ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
            ["managed_bytes"] = snapshot.HeapBytes,
            ["working_set_bytes"] = snapshot.WorkingSetBytes,
            ["private_bytes"] = snapshot.PrivateBytes,
        };
    }

    private static JsonObject BuildMcpIndexOptionsPayload(
        bool dryRun,
        bool rebuild,
        long? maxFileBytes,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        FileIndexer.SymlinkPolicy symlinkPolicy,
        IReadOnlyList<string> includeSymbolKinds,
        IReadOnlyList<string> excludeSymbolKinds,
        bool memoryTrace,
        int? requestedParallelism,
        int? requestedDebounce,
        JsonNode? args)
        => new()
        {
            ["dryRun"] = dryRun,
            ["rebuild"] = rebuild,
            ["maxFileBytes"] = maxFileBytes.HasValue ? JsonValue.Create(maxFileBytes.Value) : null,
            ["maxSymbolsPerFile"] = maxSymbolsPerFile,
            ["maxReferencesPerFile"] = maxReferencesPerFile,
            ["followSymlinks"] = FormatMcpIndexSymlinkPolicy(symlinkPolicy),
            ["includeSymbolKind"] = ToJsonStringArray(includeSymbolKinds),
            ["excludeSymbolKind"] = ToJsonStringArray(excludeSymbolKinds),
            ["memoryTrace"] = memoryTrace,
            ["parallelism_requested"] = requestedParallelism.HasValue ? JsonValue.Create(requestedParallelism.Value) : null,
            ["effective_parallelism"] = 1,
            ["watch_requested"] = args?["watch"]?.GetValue<bool>() ?? false,
            ["debounce"] = requestedDebounce.HasValue ? JsonValue.Create(requestedDebounce.Value) : null,
        };

    private async Task RefreshClientRootsIfNeededAsync()
    {
        var expectedState = CurrentInitializeState;
        if (expectedState.Phase != McpSessionPhase.Initialized
            || !expectedState.ClientRootsStale
            || !SupportsClientRootsNegotiation(expectedState.NegotiatedProtocolVersion)
            || !HasClientCapability(expectedState, "roots"))
            return;

        var result = await SendClientRequestAsync("roots/list", null, _currentRequestToken.Value).ConfigureAwait(false);
        if (result?["roots"] is not JsonArray roots)
            return;

        var rootUris = new List<string>();
        foreach (var root in roots)
        {
            var uri = TryReadStringValue(root?["uri"]) ?? TryReadStringValue(root);
            if (!string.IsNullOrWhiteSpace(uri))
                rootUris.Add(uri);
        }

        var refreshedRoots = BuildClientRootSnapshot(rootUris);
        lock (_initializeStateGate)
        {
            var frameInitializeState = _frameInitializeState.Value;
            if (frameInitializeState?.IsProvisionalGeneration == true)
            {
                _ = frameInitializeState.TryRefreshClientRoots(expectedState, refreshedRoots);
                return;
            }

            var current = PublishedInitializeState;
            if (!ReferenceEquals(current, expectedState))
                return;

            Volatile.Write(
                ref _initializeState,
                current with
                {
                    ClientRoots = refreshedRoots.Roots,
                    ClientRootDiagnostics = refreshedRoots.Diagnostics,
                    ClientRootsTruncated = refreshedRoots.Truncated,
                    ClientRootsStale = false,
                });
            _ = frameInitializeState?.TryRefreshClientRoots(expectedState, refreshedRoots);
        }
    }

    private void StartClientRootsRefreshAfterHandshake()
    {
        lock (_clientRootsHandshakeRefreshGate)
        {
            if (_clientRootsHandshakeRefreshStarted)
                return;

            _clientRootsHandshakeRefreshStarted = true;
            _clientRootsHandshakeRefreshTask = RefreshClientRootsAfterHandshakeAsync();
        }
    }

    private Task? GetClientRootsHandshakeRefreshTask()
    {
        lock (_clientRootsHandshakeRefreshGate)
            return _clientRootsHandshakeRefreshTask;
    }

    private async Task RefreshClientRootsAfterHandshakeAsync()
    {
        // Yield before client I/O so the task is published under the coalescing gate before a
        // synchronous test/client adapter can block. The execution context retains the active
        // out-of-band writer and cancellation token.
        // client I/O の前に yield し、同期 adapter が block しても task を coalescing gate 配下へ
        // 先に公開する。execution context は out-of-band writer と cancellation token を保持する。
        await Task.Yield();
        try
        {
            await RefreshClientRootsIfNeededAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            WriteMcpLogLine(
                $"[cdidx-mcp] Client roots negotiation failed ({ex.GetType().Name}); retaining the stale roots boundary.");
        }
    }

    private bool IsPathWithinClientRoots(string path)
    {
        var state = CurrentInitializeState;
        if (!HasClientCapability(state, "roots"))
            return true;

        // A completed roots/list_changed notification invalidates the previous authorization
        // boundary immediately. Fail closed until a refresh publishes a non-stale snapshot.
        // roots/list_changed notification の完了時点で以前の認可境界を直ちに無効化し、
        // refresh が non-stale snapshot を公開するまでは fail closed にする。
        if (state.ClientRootsStale)
            return false;

        var rootPaths = state.ClientRoots
            .Select(McpPathBoundary.TryResolveRootPath)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Cast<string>()
            .ToArray();
        if (rootPaths.Length == 0)
            return false;

        var fullPath = Path.GetFullPath(path);
        return rootPaths.Any(root => McpPathBoundary.IsPathWithinDirectory(root, fullPath));
    }

    private static string BuildSanitizedIndexFileFailureMessage(string stage, string exceptionType, out bool messageTruncated)
    {
        var safeStage = SanitizeMcpIndexFailureToken(stage, "unknown_stage");
        var safeExceptionType = SanitizeMcpIndexFailureToken(exceptionType, "Exception");
        return SanitizeAndCapMcpIndexFailureMessage(
            $"File indexing failed during {safeStage} ({safeExceptionType}). See cdidx server stderr for details.",
            out messageTruncated);
    }

    private static string SanitizeMcpIndexFailureToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')
                builder.Append(ch);
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    private static string SanitizeAndCapMcpIndexFailureMessage(string? message, out bool messageTruncated)
    {
        var collapsed = CollapseMcpIndexFailureWhitespace(string.IsNullOrWhiteSpace(message)
            ? "File indexing failed. See cdidx server stderr for details."
            : message);
        const string suffix = "...(truncated)";
        if (collapsed.Length <= MaxMcpIndexFailureMessageLength)
        {
            messageTruncated = false;
            return collapsed;
        }

        messageTruncated = true;
        return collapsed[..(MaxMcpIndexFailureMessageLength - suffix.Length)] + suffix;
    }

    private static string CollapseMcpIndexFailureWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\t' || char.IsControl(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            builder.Append(ch);
            lastWasSpace = ch == ' ';
        }

        return builder.ToString().Trim();
    }

    private static string QuoteMcpIndexFailureLogValue(string value)
    {
        var message = SanitizeAndCapMcpIndexFailureMessage(value, out _);
        return JsonSerializer.Serialize(message);
    }

    private sealed record IndexFileFailure(string Path, string Stage, string ExceptionType, string Message, bool MessageTruncated);
    private sealed record McpIndexDiagnostic(string Code, string Category, string Path, string Stage, string ExceptionType, string Message, bool MessageTruncated);


}
