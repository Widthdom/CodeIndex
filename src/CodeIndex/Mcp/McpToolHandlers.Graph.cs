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

public partial class McpServer
{

    private JsonNode ExecuteReferences(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        if (!TryReadLspCompatibleArgument(args, out var lspCompatible, out var lspCompatibleError))
            return CreateToolErrorResponse(id, lspCompatibleError!);
        var offset = ReadOffset(args, adjustments);
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        if (!TryResolveNameExactArgument(args, "references", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountSearchReferencesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact).Count;
                var histogramResults = countOnlyTotal > 0
                    ? reader.SearchReferences(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                AddHdlGraphContractSignal(
                    countOnlyPayload,
                    reader.GetHdlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests));
                adjustments.ApplyTo(countOnlyPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "reference")}.", countOnlyPayload);
            }

            var results = reader.SearchReferences(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, maxLineWidth, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountSearchReferencesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact).Count
                : results.Count;
            if (lspCompatible)
                QueryCommandRunner.AttachLspLocations(results);
            var graphSupport = ResolveGraphSupport(reader, exact, query, lang, pathPatterns, excludePaths, excludeTests);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                results.Select(result => result.Lang),
                lang,
                graphSupport.GraphLanguage);
            var exactSignal = reader.GetReferencesExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountSearchReferences(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false) > 0,
                () => reader.CountSearchReferences(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false),
                () => reader.SearchReferences(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false),
                r => r.SymbolName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["lang"] = lang,
                ["lspCompatible"] = lspCompatible,
                ["maxLineWidth"] = maxLineWidth,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["graph_language"] = graphSupport.GraphLanguage,
                ["graph_supported"] = graphSupport.GraphSupported,
                ["graph_support_reason"] = graphSupport.GraphSupportReason,
                ["results"] = ToJsonArray(results)
            };
            AddPaginatedResultEnvelope(payload, results.Count, total, truncated, offset);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.Line, result => result.Column);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            AddHdlGraphContractSignal(
                payload,
                reader.GetHdlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests));
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "references", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            else
            {
                var topReference = results[0];
                AddNextStepSuggestion(
                    payload,
                    "excerpt",
                    BuildExcerptArgs(topReference.Path, topReference.Line, topReference.Line),
                    "Use excerpt on representative usage sites before editing; use callers or callees when you need call graph impact.");
            }
            adjustments.ApplyTo(payload);
            return CreateToolResult(id,
                BuildGraphSummary("reference", "references", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteCallers(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        if (IsNonCallGraphReferenceKind(kind))
            return CreateToolErrorResponse(id, BuildNonCallGraphKindRejectionMessage("callers", kind!));
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var offset = ReadOffset(args, adjustments);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "callers", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        if (!TryReadReferenceRankMode(args, out var rankMode, out var rankModeError))
            return CreateToolErrorResponse(id, rankModeError!);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        var rawKinds = args?["rawKinds"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountCallersTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count;
                var histogramResults = countOnlyTotal > 0
                    ? reader.GetCallers(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["rawKinds"] = rawKinds;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                AddReferenceGraphCompletenessSignal(
                    countOnlyPayload,
                    reader,
                    lang,
                    pathPatterns,
                    excludePaths,
                    excludeTests);
                adjustments.ApplyTo(countOnlyPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "caller")}.", countOnlyPayload);
            }

            var results = reader.GetCallers(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountCallersTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count
                : results.Count;
            var graphSupport = ResolveGraphSupport(reader, exact, query, lang, pathPatterns, excludePaths, excludeTests);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                results.Select(result => result.Lang),
                lang,
                graphSupport.GraphLanguage);
            var exactSignal = reader.GetCallersExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallers(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds) > 0,
                () => reader.CountCallers(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds),
                () => reader.GetCallers(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds, rankMode: rankMode),
                r => r.CalleeName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["rawKinds"] = rawKinds,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["rankBy"] = QueryCommandRunner.FormatReferenceRankMode(rankMode),
                ["graph_language"] = graphSupport.GraphLanguage,
                ["graph_supported"] = graphSupport.GraphSupported,
                ["graph_support_reason"] = graphSupport.GraphSupportReason,
                ["results"] = ToJsonArray(results)
            };
            AddPaginatedResultEnvelope(payload, results.Count, total, truncated, offset);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.FirstLine);
            payload["aggregate_truncated"] = results.Any(result => result.AggregateTruncated);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            AddReferenceGraphCompletenessSignal(
                payload,
                reader,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "callers", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            else
            {
                var topCaller = results[0];
                AddNextStepSuggestion(
                    payload,
                    "excerpt",
                    BuildExcerptArgs(topCaller.Path, topCaller.FirstLine, topCaller.FirstLine),
                    "Use excerpt on a caller row to understand the concrete call site before widening impact analysis or editing.");
            }
            adjustments.ApplyTo(payload);
            return CreateToolResult(id,
                BuildGraphSummary("caller", "callers", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteCallees(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var kind = args?["kind"]?.GetValue<string>()?.ToLowerInvariant();
        if (IsNonCallGraphReferenceKind(kind))
            return CreateToolErrorResponse(id, BuildNonCallGraphKindRejectionMessage("callees", kind!));
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var offset = ReadOffset(args, adjustments);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "callees", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        if (!TryReadReferenceRankMode(args, out var rankMode, out var rankModeError))
            return CreateToolErrorResponse(id, rankModeError!);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        var rawKinds = args?["rawKinds"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            if (countOnly)
            {
                var countOnlyTotal = reader.CountCalleesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count;
                var histogramResults = countOnlyTotal > 0
                    ? reader.GetCallees(query, Math.Min(countOnlyTotal, MaxLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode)
                    : [];
                var countOnlyPayload = BuildCountOnlyPayload(countOnlyTotal, countOnlyTotal, truncated: false, histogramResults, result => result.Path);
                countOnlyPayload["query"] = query;
                countOnlyPayload["kind"] = kind;
                countOnlyPayload["rawKinds"] = rawKinds;
                countOnlyPayload["lang"] = lang;
                countOnlyPayload["path"] = PathEcho(pathPatterns);
                countOnlyPayload["excludeTests"] = excludeTests;
                AddReferenceGraphCompletenessSignal(
                    countOnlyPayload,
                    reader,
                    lang,
                    pathPatterns,
                    excludePaths,
                    excludeTests);
                adjustments.ApplyTo(countOnlyPayload);
                return CreateToolResult(id, $"Counted {ConsoleUi.Counted(countOnlyTotal, "callee")}.", countOnlyPayload);
            }

            var results = reader.GetCallees(query, FetchLimitForEnvelope(limit), lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds, rankMode: rankMode, offset: offset);
            var truncated = TrimToRequestedLimit(results, limit);
            var total = truncated || offset > 0
                ? reader.CountCalleesTotal(query, lang, kind, pathPatterns, excludePaths, excludeTests, exact, rawKinds).Count
                : results.Count;
            var graphSupport = ResolveGraphSupport(reader, exact, query, lang, pathPatterns, excludePaths, excludeTests);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignalByLanguages(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                results.Select(result => result.Lang),
                lang,
                graphSupport.GraphLanguage);
            var exactSignal = reader.GetCalleesExactQuerySignal(lang, pathPatterns, excludePaths, excludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = QueryCommandRunner.BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallees(query, QueryCommandRunner.ExactZeroHintProbeLimit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds) > 0,
                () => reader.CountCallees(query, limit, lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds),
                () => reader.GetCallees(query, Math.Min(limit, QueryCommandRunner.ExactZeroHintSampleLimit), lang, kind, pathPatterns, excludePaths, excludeTests, exact: false, rawKinds: rawKinds, rankMode: rankMode),
                r => r.CallerName);
            var payload = new JsonObject
            {
                ["query"] = query,
                ["kind"] = kind,
                ["rawKinds"] = rawKinds,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["rankBy"] = QueryCommandRunner.FormatReferenceRankMode(rankMode),
                ["graph_language"] = graphSupport.GraphLanguage,
                ["graph_supported"] = graphSupport.GraphSupported,
                ["graph_support_reason"] = graphSupport.GraphSupportReason,
                ["results"] = ToJsonArray(results)
            };
            AddPaginatedResultEnvelope(payload, results.Count, total, truncated, offset);
            if (format == "compact")
                ApplyCompactResults(payload, results, result => result.Path, result => result.FirstLine, result => result.FirstColumn);
            payload["aggregate_truncated"] = results.Any(result => result.AggregateTruncated);
            if (exact)
                AddExactGraphSignal(payload, exactSignal);
            AddSqlGraphContractSignal(payload, sqlGraphSignal);
            AddReferenceGraphCompletenessSignal(
                payload,
                reader,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests);
            if (results.Count == 0)
            {
                AddExactZeroHint(payload, exactZeroHint);
                AddSymbolRecoveryHint(payload, query, "callees", lang, kind, PathEcho(pathPatterns));
                AddFreshnessHint(payload, reader);
            }
            else
            {
                var topCallee = results[0];
                AddNextStepSuggestion(
                    payload,
                    "excerpt",
                    BuildExcerptArgs(topCallee.Path, topCallee.FirstLine, topCallee.FirstLine),
                    "Use excerpt on a callee row to inspect the concrete dependency before changing the caller or callee.");
            }
            adjustments.ApplyTo(payload);
            return CreateToolResult(id,
                BuildGraphSummary("callee", "callees", results.Count, graphSupport.GraphLanguage, graphSupport.GraphSupported, graphSupport.GraphSupportReason),
                payload);
        });
    }

    private JsonNode ExecuteFiles(JsonNode? id, JsonNode? args)
    {
        var query = args?["query"]?.GetValue<string>();
        if (query != null && query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        var adjustments = new ArgumentAdjustmentCollector();
        var lang = QueryCommandRunner.NormalizeLangFilterValue(args?["lang"]?.GetValue<string>());
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryReadSinceArgument(args, out var since, out var sinceError))
            return CreateToolErrorResponse(id, sinceError!);
        var orderBySize = args?["orderBySize"]?.GetValue<bool>() ?? false;
        var rawBytes = args?["rawBytes"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            var results = reader.ListFiles(query, limit, lang, pathPatterns, excludePaths, excludeTests, since, orderBySize || rawBytes);
            if (results.Count == 0)
            {
                var payload = new JsonObject
                {
                    ["query"] = query,
                    ["lang"] = lang,
                    ["path"] = PathEcho(pathPatterns),
                    ["excludeTests"] = excludeTests,
                    ["orderBySize"] = orderBySize,
                    ["rawBytes"] = rawBytes,
                    ["count"] = 0,
                    ["results"] = new JsonArray()
                };
                if (rawBytes)
                {
                    payload["raw_bytes_payload_supported"] = false;
                    payload["raw_bytes_note"] = "MCP returns indexed file size metadata; raw file bytes are not returned.";
                }
                AddFreshnessHint(payload, reader);
                adjustments.ApplyTo(payload);
                return CreateToolResult(id, "No files found.", payload);
            }

            var structured = new JsonObject
            {
                ["query"] = query,
                ["lang"] = lang,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["orderBySize"] = orderBySize,
                ["rawBytes"] = rawBytes,
                ["count"] = results.Count,
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions)
            };
            if (rawBytes)
            {
                structured["raw_bytes_payload_supported"] = false;
                structured["raw_bytes_note"] = "MCP returns indexed file size metadata; raw file bytes are not returned.";
            }
            adjustments.ApplyTo(structured);
            return CreateToolResult(id, ConsoleUi.FoundSummary(results.Count, "file"), structured);
        });
    }

    private JsonNode ExecuteMap(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultMapLimit, adjustments);
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var sections = ReadStringList(args, "sections").Select(section => section.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var depth = ReadMapDepth(args, adjustments);
        var minEntrypointConfidence = args?["minEntrypointConfidence"]?.GetValue<double>() ?? 0;
        if (minEntrypointConfidence is < 0 or > 1)
            return CreateToolErrorResponse(id, "minEntrypointConfidence must be between 0.0 and 1.0");

        return WithDbReader(id, args, reader =>
        {
            var map = reader.GetRepoMap(
                limit,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests,
                minEntrypointConfidence,
                moduleDepth: depth);
            WorkspaceMetadataEnricher.Enrich(map, _dbPath, _dbPathExplicit);
            var structured = JsonSerializer.SerializeToNode(map, _jsonOptions)!.AsObject();
            if (depth is >= 0)
                structured["depth"] = depth.Value;
            if (sections.Count > 0)
                ApplyMapSectionFilter(structured, sections);
            structured["limit"] = limit;
            structured["lang"] = lang;
            structured["path"] = PathEcho(pathPatterns);
            structured["excludeTests"] = excludeTests;
            structured["minEntrypointConfidence"] = minEntrypointConfidence;
            var hasFilter = (pathPatterns is { Count: > 0 }) || excludePaths.Count > 0 || excludeTests || lang != null;
            if (map.FileCount == 0 && hasFilter)
                AddFreshnessHint(structured, reader);
            adjustments.ApplyTo(structured);
            var summary = map.FileCount > 0
                ? "Repo map returned."
                : hasFilter ? "No files found matching the given filters." : "Repo map returned.";
            return CreateToolResult(id, summary, structured);
        });
    }

    private static void ApplyMapSectionFilter(JsonObject structured, IReadOnlySet<string> sections)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            "api_version", "fileCount", "totalLines", "totalSymbols", "totalReferences",
            "indexedAt", "latestModified", "workspaceIndexedAt", "workspaceLatestModified",
            "projectRoot", "gitHead", "gitIsDirty", "indexed_head_commit", "indexed_head_sha",
            "indexed_head_branch", "indexed_head_timestamp", "commits_ahead_of_indexed_head",
            "worktree_head_changed", "head_freshness",
            "graphTableAvailable", "limit", "lang", "path", "excludeTests", "depth", "minEntrypointConfidence",
        };
        foreach (var section in sections)
            AddMapSectionStructuredProperties(keep, section);
        foreach (var key in structured.Select(property => property.Key).Where(key => !keep.Contains(key)).ToList())
            structured.Remove(key);
        structured["sections"] = new JsonArray(sections.Select(section => JsonValue.Create(section)).ToArray<JsonNode?>());
        structured["sectionProperties"] = BuildMapSectionStructuredProperties(sections);
    }

    private static readonly IReadOnlyDictionary<string, string[]> MapSectionStructuredProperties = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["languages"] = ["languages"],
        ["tree"] = ["modules"],
        ["modules"] = ["modules"],
        ["hotspots"] = ["topFiles", "symbolRichFiles", "referenceRichFiles", "entrypoints"],
        ["metrics"] = ["largestFiles"],
    };

    private static void AddMapSectionStructuredProperties(HashSet<string> keep, string section)
    {
        if (!MapSectionStructuredProperties.TryGetValue(section, out var properties))
            return;

        foreach (var property in properties)
            keep.Add(property);
    }

    private static JsonObject BuildMapSectionStructuredProperties(IReadOnlySet<string> sections)
    {
        var payload = new JsonObject();
        foreach (var section in sections)
        {
            if (!MapSectionStructuredProperties.TryGetValue(section, out var properties))
                continue;

            payload[section] = new JsonArray(properties.Select(property => JsonValue.Create(property)).ToArray<JsonNode?>());
        }

        return payload;
    }

    private JsonNode ExecuteAnalyzeSymbol(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());
        if (IsBareVerbatimQueryToken(query))
            return CreateToolErrorResponse(id, "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");

        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultMapLimit, adjustments);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var includeBody = args?["includeBody"]?.GetValue<bool>() ?? false;
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var pathPatterns = ReadScopedPathList(args);
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        if (!TryResolveNameExactArgument(args, "analyze_symbol", out var exact, out var exactError))
            return CreateToolErrorResponse(id, exactError!);
        var format = ReadResponseFormat(args);
        if (ValidateResponseFormat(format) is string formatError)
            return CreateToolErrorResponse(id, formatError);
        var countOnly = ReadCountOnly(args) || format == "count";
        var rawCursor = args?["cursor"]?.GetValue<string>();
        InspectGraphCursor? graphCursor = null;
        if (rawCursor != null && !InspectGraphCursorCodec.TryParse(rawCursor, out graphCursor))
            return CreateToolErrorResponse(id, "cursor must be an inspect graph next_cursor returned by analyze_symbol.");

        return WithDbReader(id, args, reader =>
        {
            var queryFingerprint = BuildAnalyzeSymbolGraphQueryFingerprint(
                query,
                lang,
                pathPatterns,
                excludePaths,
                excludeTests,
                exact,
                limit);
            var generation = InspectGraphCursorCodec.BuildGenerationFingerprint(reader);
            if (graphCursor != null
                && (!string.Equals(graphCursor.QueryFingerprint, queryFingerprint, StringComparison.Ordinal)
                    || !string.Equals(graphCursor.GenerationFingerprint, generation.Fingerprint, StringComparison.Ordinal)))
            {
                return CreateToolErrorResponse(
                    id,
                    "cursor does not match this analyze_symbol query or index generation; rerun without cursor.");
            }
            var graphPage = graphCursor == null
                ? null
                : new SymbolGraphPageRequest(
                    graphCursor.Section,
                    graphCursor.Offset,
                    graphCursor.CandidateSelector);
            var analysis = reader.AnalyzeSymbol(
                query,
                limit,
                lang,
                includeBody,
                pathPatterns,
                excludePaths,
                excludeTests,
                exact,
                maxLineWidth,
                graphPage: graphPage);
            if (graphCursor?.CandidateSelector != null
                && !(analysis.CandidateBundles?.Any(bundle =>
                    string.Equals(bundle.Selector.Selector, graphCursor.CandidateSelector, StringComparison.Ordinal)) ?? false))
            {
                return CreateToolErrorResponse(
                    id,
                    "cursor candidate is no longer available; rerun analyze_symbol without cursor.");
            }
            QueryCommandRunner.SynchronizeInspectGraphSectionCounts(analysis);
            QueryCommandRunner.ApplyInspectGraphContinuationCursors(
                analysis,
                queryFingerprint,
                generation.Fingerprint);
            var sqlGraphSignal = QueryCommandRunner.NarrowSqlGraphContractSignal(
                reader.GetSqlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests),
                DbReader.IsSqlLanguage(lang)
                    || DbReader.IsSqlLanguage(analysis.GraphLanguage)
                    || DbReader.IsSqlLanguage(analysis.File?.Lang)
                    || DbReader.ContainsSqlLanguage(analysis.Definitions.Select(definition => definition.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.References.Select(reference => reference.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callers.Select(caller => caller.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callees.Select(callee => callee.Lang)));
            analysis.SqlGraphContractReady = sqlGraphSignal.Relevant ? sqlGraphSignal.Ready : null;
            analysis.SqlGraphContractDegradedReason = sqlGraphSignal.Relevant ? sqlGraphSignal.DegradedReason : null;
            WorkspaceMetadataEnricher.Enrich(analysis, _dbPath, _dbPathExplicit);
            ApplyExcerptRecoveryDbPath(analysis.Definitions);
            ApplyExcerptRecoveryDbPath(analysis.References);
            ApplyExcerptRecoveryDbPath(analysis.Callers);
            ApplyExcerptRecoveryDbPath(analysis.Callees);
            var pathEcho = PathEcho(pathPatterns);
            var structured = countOnly
                ? BuildAnalyzeSymbolCountPayload(analysis, lang, pathEcho, excludeTests, maxLineWidth)
                : format == "compact"
                    ? BuildAnalyzeSymbolCompactPayload(analysis, lang, pathEcho, excludeTests, maxLineWidth)
                    : ToAnalyzeSymbolJsonObject(analysis);
            AddSqlGraphContractSignal(structured, sqlGraphSignal);
            AddHdlGraphContractSignal(
                structured,
                reader.GetHdlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests));
            structured.Remove("exactZeroHint");
            AddExactZeroHint(structured, analysis.ExactZeroHint);
            structured["maxLineWidth"] = maxLineWidth;
            structured["lang"] = lang;
            structured["path"] = pathEcho;
            structured["excludeTests"] = excludeTests;
            adjustments.ApplyTo(structured);
            return CreateToolResult(id, BuildAnalyzeSymbolSummary(analysis), structured);
        });
    }

    private static string BuildAnalyzeSymbolGraphQueryFingerprint(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePaths,
        bool excludeTests,
        bool exact,
        int pageLimit)
    {
        var components = new List<string?>
        {
            "mcp:analyze_symbol",
            query,
            lang,
            exact ? "exact" : "substring",
            $"page-limit:{pageLimit.ToString(CultureInfo.InvariantCulture)}",
            excludeTests ? "exclude-tests" : "include-tests",
        };
        components.AddRange((pathPatterns ?? [])
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "path:" + path));
        components.AddRange((excludePaths ?? [])
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "exclude:" + path));
        return InspectGraphCursorCodec.BuildQueryFingerprint(components);
    }

    private static string BuildAnalyzeSymbolSummary(SymbolAnalysisResult analysis)
    {
        if (analysis.ExactZeroHint != null)
        {
            var relaxedCount = analysis.ExactZeroHint.RelaxedCount ?? analysis.ExactZeroHint.SampleNames.Count;
            return $"Symbol analysis returned. Substring would return {ConsoleUi.Counted(relaxedCount, "similarly named symbol")}.";
        }

        return "Symbol analysis returned.";
    }

    private static void AddExactGraphSignal(JsonObject payload, ExactQuerySignal signal)
    {
        payload["exact_index_available"] = signal.ExactIndexAvailable;
        if (signal.DegradedReason != null)
            payload["degraded_reason"] = signal.DegradedReason;
        // MCP uses snake_case response keys consistently; do not add camelCase aliases here.
    }

    private static void AddSqlGraphContractSignal(JsonObject payload, SqlGraphContractSignal signal)
    {
        if (!signal.Relevant)
            return;

        payload["sql_graph_contract_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
            {
                payload["sql_graph_contract_degraded_reason"] = signal.DegradedReason;
            }
        }
    }

    private static void AddHdlGraphContractSignal(JsonObject payload, HdlGraphContractSignal signal)
    {
        if (!signal.Relevant)
            return;

        payload["hdl_graph_contract_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
                payload["hdl_graph_contract_degraded_reason"] = signal.DegradedReason;
        }
    }

    private void AddReferenceGraphCompletenessSignal(
        JsonObject payload,
        DbReader reader,
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePaths = null,
        bool excludeTests = false)
    {
        AddHdlGraphContractSignal(
            payload,
            reader.GetHdlGraphContractSignal(lang, pathPatterns, excludePaths, excludeTests));
        AddReferenceGraphCompletenessSignal(
            payload,
            reader,
            reader.GetReferenceExtractionCapHits());
    }

    private void AddReferenceGraphCompletenessSignal(
        JsonObject payload,
        DbReader reader,
        ReferenceExtractionCapHitSummary capHits)
    {
        var readiness = reader.GetPersistedIndexGenerationReadiness(capHits);
        AddReferenceGraphCompletenessSignal(payload, readiness);
    }

    private void AddReferenceGraphCompletenessSignal(
        JsonObject payload,
        PersistedIndexGenerationReadiness readiness)
    {
        payload["reference_extraction_limits"] = JsonSerializer.SerializeToNode(
            ReferenceExtractor.GetSafetyLimits(),
            _jsonOptions);
        payload["reference_graph_complete"] = readiness.ReferenceGraphComplete;
        payload["reference_extraction_cap_hits"] = JsonSerializer.SerializeToNode(
            readiness.ReferenceExtractionCapHits,
            _jsonOptions);
        if (!readiness.ReferenceGraphComplete)
        {
            payload["reference_graph_incomplete_reasons"] = JsonSerializer.SerializeToNode(
                readiness.ReferenceGraphIncompleteReasons,
                _jsonOptions);
            payload["degraded"] = true;
        }
    }

    private void AddIndexGenerationReadinessSignal(
        JsonObject payload,
        PersistedIndexGenerationReadiness readiness)
    {
        payload["graph_table_available"] = readiness.GraphTableAvailable;
        payload["graph_data_current"] = readiness.GraphDataCurrent;
        payload["index_complete"] = readiness.IndexComplete;
        if (!readiness.IndexComplete)
        {
            payload["index_incomplete_reasons"] = JsonSerializer.SerializeToNode(
                readiness.IndexIncompleteReasons,
                _jsonOptions);
            payload["degraded"] = true;
        }
    }

    private static bool IsBareVerbatimQueryToken(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed.All(ch => ch == '@');
    }

    private static Dictionary<string, string?> GetHotspotFamilyMetaSnapshot(DbContext db, Func<string, string> keyFactory)
    {
        var languages = FileIndexer.GetHotspotFamilyMarkerLanguages();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var keys = new string[languages.Count];
        for (var i = 0; i < languages.Count; i++)
        {
            var lang = languages[i];
            keys[i] = keyFactory(lang);
            values[lang] = null;
        }

        var metaValues = db.GetMetaStrings(keys);
        for (var i = 0; i < languages.Count; i++)
            values[languages[i]] = metaValues.TryGetValue(keys[i], out var value) ? value : null;

        return values;
    }

    private static Dictionary<string, FileIndexer.ProjectMarkerFingerprintResult> GetHotspotFamilyMarkerFingerprints(
        FileIndexer indexer,
        CancellationToken cancellationToken) =>
        indexer.GetProjectMarkerFingerprintResults(cancellationToken);

    private static void RestampHotspotFamilyTrust(
        DbWriter writer,
        IReadOnlySet<string>? reusedLanguages,
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            if (!currentFingerprints.TryGetValue(lang, out var currentFingerprint))
                continue;

            if (!currentFingerprint.IsComplete)
            {
                writer.MarkHotspotFamilyMarkerFingerprintIncomplete(lang, currentFingerprint.Fingerprint);
                continue;
            }

            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            if (reusedLanguages?.Contains(lang) != true || (priorVersion == currentVersion && priorFingerprint == currentFingerprint.Fingerprint))
                writer.MarkHotspotFamilyReady(lang, currentFingerprint.Fingerprint);
        }
    }

    private static Dictionary<string, bool> GetHotspotFamilyTrustMatchesCurrent(
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        var currentVersion = DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            currentFingerprints.TryGetValue(lang, out var currentFingerprint);
            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            values[lang] = currentFingerprint.IsComplete
                && priorVersion == currentVersion
                && priorFingerprint == currentFingerprint.Fingerprint;
        }

        return values;
    }

    private static bool AllowReuseWithCurrentHotspotFamilyTrust(
        string? lang,
        IReadOnlyDictionary<string, bool> hotspotFamilyTrustMatchesCurrent)
    {
        if (!FileIndexer.SupportsHotspotFamilyMarkerLanguage(lang))
            return true;

        return lang != null
            && hotspotFamilyTrustMatchesCurrent.TryGetValue(lang, out var matchesCurrent)
            && matchesCurrent;
    }

    private static void AddHotspotFamilySignal(JsonObject payload, HotspotFamilySignal signal)
    {
        payload["hotspot_family_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
            {
                payload["hotspot_family_degraded_reason"] = signal.DegradedReason;
            }
        }
    }

}
