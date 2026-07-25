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

    private JsonNode ExecuteOutline(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredPathParameter(args, "path", out var path, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        return WithDbReader(id, args, reader =>
        {
            var outline = reader.GetOutline(path);
            if (outline == null)
            {
                var emptyPayload = new JsonObject
                {
                    ["path"] = path,
                    ["error"] = "file not found in index"
                };
                AddFreshnessHint(emptyPayload, reader);
                return CreateToolResult(id, "File not found in index.", emptyPayload);
            }

            var structured = JsonSerializer.SerializeToNode(outline, _jsonOptions)!.AsObject();
            AddNextStepSuggestion(
                structured,
                "excerpt",
                new JsonObject { ["path"] = path, ["startLine"] = 1, ["endLine"] = Math.Min(outline.TotalLines, 80) },
                "Use excerpt for only the relevant outline range instead of reading the whole file.");
            return CreateToolResult(id, $"Outline: {ConsoleUi.Counted(outline.SymbolCount, "symbol")} in {ConsoleUi.Counted(outline.TotalLines, "line")}.", structured);
        });
    }

    private JsonNode ExecuteExcerpt(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredPathParameter(args, "path", out var path, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        var startLine = ReadOptionalIntArgument(args, "startLine");
        if (startLine == null || startLine <= 0)
            return CreateToolErrorResponse(id, "Missing or invalid required parameter: startLine");

        var endLine = ReadOptionalIntArgument(args, "endLine") ?? startLine.Value;
        if (endLine < startLine.Value)
            return CreateToolErrorResponse(id, "endLine must be greater than or equal to startLine");

        var beforeValue = ReadOptionalIntArgument(args, "before");
        if (beforeValue.HasValue && beforeValue.Value < 0)
            return CreateToolErrorResponse(id, $"before must be in [0, {MaxContextLines}]");
        var before = ClampContextLines(beforeValue ?? 0);

        var afterValue = ReadOptionalIntArgument(args, "after");
        if (afterValue.HasValue && afterValue.Value < 0)
            return CreateToolErrorResponse(id, $"after must be in [0, {MaxContextLines}]");
        var after = ClampContextLines(afterValue ?? 0);
        var contextTruncated = beforeValue > MaxContextLines || afterValue > MaxContextLines;

        var focusLine = ReadOptionalIntArgument(args, "focusLine");
        var focusColumn = ReadOptionalIntArgument(args, "focusColumn");
        var focusLengthValue = ReadOptionalIntArgument(args, "focusLength");
        if (focusLengthValue.HasValue && focusLengthValue.Value <= 0)
            return CreateToolErrorResponse(id, "focusLength must be greater than or equal to 1");
        var focusLength = focusLengthValue ?? 1;
        var explicitFocusLength = args?["focusLength"] != null;
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        if (!TryReadMaxOutputBytes(args, out var maxOutputBytes, out var maxOutputBytesError))
            return CreateToolErrorResponse(id, maxOutputBytesError!);

        if (focusLine.HasValue && focusLine.Value <= 0)
            return CreateToolErrorResponse(id, "focusLine must be greater than or equal to 1");
        if (focusColumn.HasValue && focusColumn.Value <= 0)
            return CreateToolErrorResponse(id, "focusColumn must be greater than or equal to 1");
        if (!focusColumn.HasValue && explicitFocusLength)
            return CreateToolErrorResponse(id, "focusLength requires focusColumn");

        return WithDbReader(id, args, reader =>
        {
            if (focusLine.HasValue)
            {
                var file = reader.GetFileByPath(path);
                if (file != null)
                {
                    // `before` is bounded by MaxContextLines and `startLine` by `int.MaxValue`, but
                    // `endLine` is caller-supplied: int + int can still overflow when endLine is
                    // close to `int.MaxValue`. Use long intermediates so the clamp sees the real
                    // window before narrowing back to int (#1528).
                    // `before` は MaxContextLines、`startLine` は `int.MaxValue` で押さえているが、
                    // `endLine` は呼び出し側入力で `int.MaxValue` 近傍なら int 同士の加算が overflow し得る。
                    // long 中間変数で実窓を確定させてから int に戻す（#1528）。
                    var requestedStart = (int)Math.Max(1L, (long)startLine.Value - before);
                    var requestedEnd = (int)Math.Min(file.Lines, (long)endLine + after);
                    if (focusLine.Value < requestedStart || focusLine.Value > requestedEnd)
                        return CreateToolErrorResponse(id, $"focusLine ({focusLine.Value}) must be within the returned excerpt range ({requestedStart}-{requestedEnd})");
                }
            }
            if (focusColumn.HasValue)
            {
                var focusLineLength = reader.GetExcerptFocusLineLength(
                    path,
                    startLine.Value,
                    endLine,
                    before,
                    after,
                    focusLine ?? startLine.Value);
                if (focusLineLength.HasValue && focusColumn.Value > focusLineLength.Value)
                    return CreateToolErrorResponse(id, $"focusColumn ({focusColumn.Value}) must be within the focused line length ({focusLineLength.Value})");
            }

            var excerpt = reader.GetExcerpt(path, startLine.Value, endLine, before, after, maxLineWidth, focusLine ?? startLine.Value, focusColumn, focusLength);
            if (excerpt == null)
            {
                var emptyPayload = new JsonObject
                {
                    ["path"] = path,
                    ["count"] = 0
                };
                AddRecoveryHint(
                    emptyPayload,
                    "file_or_range_not_indexed",
                    "excerpt found no indexed content for the requested range; verify the path with files or outline, then retry with an indexed line range.",
                    "outline",
                    new JsonObject { ["path"] = path });
                AddFreshnessHint(emptyPayload, reader);
                return CreateToolResult(id, "No excerpt found.", emptyPayload);
            }

            ExcerptRecoveryCommandFormatter.ApplyDbPath(excerpt, _dbPath);
            var payload = JsonSerializer.SerializeToNode(excerpt, _jsonOptions)!.AsObject();
            ApplyExcerptOutputBudget(payload, maxOutputBytes);
            payload["maxOutputBytes"] = maxOutputBytes;
            payload["before"] = before;
            payload["after"] = after;
            payload["contextTruncated"] = contextTruncated;
            payload["maxLineWidth"] = maxLineWidth;
            if (focusLine.HasValue)
                payload["focusLine"] = focusLine.Value;
            if (focusColumn.HasValue)
                payload["focusColumn"] = focusColumn.Value;
            payload["focusLength"] = focusLength;
            AddNextStepSuggestion(
                payload,
                "outline",
                new JsonObject { ["path"] = excerpt.Path },
                "Use outline to navigate neighboring symbols before requesting more ranges from the same file.");
            return CreateToolResult(id, "Excerpt returned.", payload);
        });
    }

    private static bool TryReadMaxOutputBytes(JsonNode? args, out int maxOutputBytes, out string? error)
    {
        maxOutputBytes = DefaultExcerptOutputByteLimit;
        error = null;
        if (args?["maxOutputBytes"] is not JsonNode node)
            return true;
        if (node is not JsonValue value || !value.TryGetValue<int>(out var requested))
        {
            error = "maxOutputBytes must be an integer";
            return false;
        }
        if (requested <= 0)
        {
            error = "maxOutputBytes must be greater than or equal to 1";
            return false;
        }
        maxOutputBytes = Math.Min(requested, DefaultExcerptOutputByteLimit);
        return true;
    }

    internal static void ApplyExcerptOutputBudget(JsonObject payload, int maxOutputBytes)
    {
        var contentKey = payload.ContainsKey("content") ? "content" : "Content";
        if (payload[contentKey]?.GetValue<string>() is not string content)
            return;
        if (Encoding.UTF8.GetByteCount(content) <= maxOutputBytes)
            return;

        var builder = new StringBuilder();
        var retainedLineCount = 0;
        var firstRetainedLine = true;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var candidate = firstRetainedLine ? line : builder.ToString() + "\n" + line;
            if (Encoding.UTF8.GetByteCount(candidate) > maxOutputBytes)
                break;
            builder.Clear();
            builder.Append(candidate);
            retainedLineCount++;
            firstRetainedLine = false;
        }
        payload[contentKey] = builder.ToString();
        TrimExcerptCoordinatePayload(payload, retainedLineCount);
        payload["contentTruncated"] = true;
        payload["truncated"] = true;
        payload["truncation_reason"] = "output_size_cap";
    }

    private static void TrimExcerptCoordinatePayload(JsonObject payload, int retainedLineCount)
    {
        var spansKey = FirstPayloadKey(payload, "contentLineSpans", "content_line_spans", "ContentLineSpans");
        var retainedSpans = new List<ExcerptPayloadSpan>();
        var hasSpanMapping = false;
        if (spansKey is not null && payload[spansKey] is JsonArray spans)
        {
            hasSpanMapping = true;
            var trimmedSpans = new JsonArray();
            foreach (var spanNode in spans)
            {
                if (spanNode is not JsonObject span)
                    continue;
                var contentLine = GetPayloadInt(span, "contentLine", "content_line", "ContentLine");
                if (!contentLine.HasValue || contentLine.Value > retainedLineCount)
                    continue;

                trimmedSpans.Add(span.DeepClone());
                var sourceLine = GetPayloadInt(span, "sourceLine", "source_line", "SourceLine");
                var sourceStartColumn = GetPayloadInt(span, "sourceStartColumn", "source_start_column", "SourceStartColumn");
                var sourceEndColumn = GetPayloadInt(span, "sourceEndColumn", "source_end_column", "SourceEndColumn");
                if (sourceLine.HasValue && sourceStartColumn.HasValue && sourceEndColumn.HasValue)
                    retainedSpans.Add(new ExcerptPayloadSpan(sourceLine.Value, sourceStartColumn.Value, sourceEndColumn.Value));
            }

            payload[spansKey] = trimmedSpans;
        }

        var tokensKey = FirstPayloadKey(payload, "semanticTokens", "semantic_tokens", "SemanticTokens");
        if (tokensKey is null || payload[tokensKey] is not JsonArray tokens)
            return;
        if (!hasSpanMapping)
        {
            if (retainedLineCount == 0)
                payload[tokensKey] = new JsonArray();
            return;
        }

        var trimmedTokens = new JsonArray();
        if (retainedLineCount > 0 && retainedSpans.Count > 0)
        {
            foreach (var tokenNode in tokens)
            {
                if (tokenNode is not JsonObject token)
                    continue;
                var startLine = GetPayloadInt(token, "startLine", "start_line", "StartLine");
                var endLine = GetPayloadInt(token, "endLine", "end_line", "EndLine");
                var startColumn = GetPayloadInt(token, "startColumn", "start_column", "StartColumn");
                var endColumn = GetPayloadInt(token, "endColumn", "end_column", "EndColumn");
                if (!startLine.HasValue || !endLine.HasValue || !startColumn.HasValue || !endColumn.HasValue)
                    continue;
                if (retainedSpans.Any(span =>
                    startLine.Value == span.SourceLine &&
                    endLine.Value == span.SourceLine &&
                    startColumn.Value >= span.SourceStartColumn &&
                    endColumn.Value <= span.SourceEndColumn))
                {
                    trimmedTokens.Add(token.DeepClone());
                }
            }
        }

        payload[tokensKey] = trimmedTokens;
    }

    private static string? FirstPayloadKey(JsonObject payload, params string[] keys)
        => keys.FirstOrDefault(payload.ContainsKey);

    private static int? GetPayloadInt(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj[key] is JsonNode node)
                return node.GetValue<int>();
        }

        return null;
    }

    private readonly record struct ExcerptPayloadSpan(int SourceLine, int SourceStartColumn, int SourceEndColumn);

    private JsonNode ExecuteFindInFile(JsonNode? id, JsonNode? args)
    {
        if (!TryReadRequiredStringParameter(args, "query", out var query, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);
        if (query.Length > QueryLimits.MaxQueryLength)
            return CreateToolErrorResponse(id, QueryLimits.FormatQueryTooLongError());

        var pathPatterns = ReadScopedPathList(args);
        if (pathPatterns == null || pathPatterns.Count == 0)
            return CreateToolErrorResponse(id, HasBlankPathFilter(args)
                ? "Parameter \"path\" cannot be empty or whitespace-only"
                : "Missing required parameter: path");

        var adjustments = new ArgumentAdjustmentCollector();
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);
        var lang = args?["lang"]?.GetValue<string>()?.ToLowerInvariant();
        var excludePaths = ReadStringList(args, "excludePaths");
        var excludeTests = args?["excludeTests"]?.GetValue<bool>() ?? false;
        var beforeValue = ReadOptionalIntArgument(args, "before");
        if (beforeValue.HasValue && beforeValue.Value < 0)
            return CreateToolErrorResponse(id, "before must be greater than or equal to 0");
        var before = ClampContextLines(beforeValue ?? 0);

        var afterValue = ReadOptionalIntArgument(args, "after");
        if (afterValue.HasValue && afterValue.Value < 0)
            return CreateToolErrorResponse(id, "after must be greater than or equal to 0");
        var after = ClampContextLines(afterValue ?? 0);
        var contextTruncated = beforeValue > MaxContextLines || afterValue > MaxContextLines;
        var snippetLinesValue = ReadOptionalIntArgument(args, "snippetLines");
        if (snippetLinesValue.HasValue && (snippetLinesValue.Value <= 0 || snippetLinesValue.Value > SearchSnippetFormatter.MaxSnippetLines))
            return CreateToolErrorResponse(id, $"snippetLines must be in [1, {SearchSnippetFormatter.MaxSnippetLines}]");
        if (snippetLinesValue.HasValue)
        {
            var surroundingLines = snippetLinesValue.Value - 1;
            if (!beforeValue.HasValue)
                before = surroundingLines / 2;
            if (!afterValue.HasValue)
                after = surroundingLines - before;
        }
        var focusLine = args?["focusLine"]?.GetValue<int>();
        if (focusLine.HasValue && focusLine.Value <= 0)
            return CreateToolErrorResponse(id, "focusLine must be greater than or equal to 1");
        var focusColumn = args?["focusColumn"]?.GetValue<int>();
        if (focusColumn.HasValue && focusColumn.Value <= 0)
            return CreateToolErrorResponse(id, "focusColumn must be greater than or equal to 1");
        if (TryGetValidatedMaxLineWidth(id, args, out var maxLineWidth) is JsonNode maxLineWidthError)
            return maxLineWidthError;
        var exact = args?["exact"]?.GetValue<bool>() ?? false;
        var regex = args?["regex"]?.GetValue<bool>() ?? false;

        return WithDbReader(id, args, reader =>
        {
            List<FileFindResult> results;
            try
            {
                results = reader.FindInFiles(query, limit, lang, pathPatterns, excludePaths, excludeTests, before, after, exact, maxLineWidth, focusLine, focusColumn, regex).Results;
            }
            catch (RegexMatchTimeoutException ex) when (regex)
            {
                return CreateToolErrorResponse(
                    id,
                    RegexTimeoutPolicy.FormatFindTimeout(ex),
                    category: RegexTimeoutPolicy.RegexTimeoutCategory,
                    suggestion: RegexTimeoutPolicy.McpFindTimeoutSuggestion,
                    retrySafe: true,
                    extraData: new JsonObject
                    {
                        ["error_code"] = CommandErrorCodes.RegexMatchTimeout,
                        ["timeout_ms"] = ex.MatchTimeout.TotalMilliseconds,
                    });
            }
            catch (ArgumentException) when (regex)
            {
                return CreateToolErrorResponse(id, "invalid regular expression. Check regex syntax and retry.");
            }
            var structured = new JsonObject
            {
                ["query"] = query,
                ["path"] = PathEcho(pathPatterns),
                ["excludeTests"] = excludeTests,
                ["before"] = before,
                ["after"] = after,
                ["contextTruncated"] = contextTruncated,
                ["maxLineWidth"] = maxLineWidth,
                ["exact"] = exact,
                ["regex"] = regex,
                ["count"] = results.Count,
                ["fileCount"] = results.Select(r => r.Path).Distinct().Count(),
                ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions),
            };
            if (snippetLinesValue.HasValue)
                structured["snippetLines"] = snippetLinesValue.Value;
            if (focusLine.HasValue)
                structured["focusLine"] = focusLine.Value;
            if (focusColumn.HasValue)
                structured["focusColumn"] = focusColumn.Value;
            if (results.Count == 0)
            {
                AddFreshnessHint(structured, reader);
                adjustments.ApplyTo(structured);
                return CreateToolResult(id, "No matches found.", structured);
            }

            var fileCount = structured["fileCount"]!.GetValue<int>();
            adjustments.ApplyTo(structured);
            return CreateToolResult(id, $"Found {ConsoleUi.Counted(results.Count, "in-file match", "in-file matches")} across {ConsoleUi.Counted(fileCount, "file")}.", structured);
        });
    }

    private static int ClampContextLines(int value)
    {
        return Math.Min(value, MaxContextLines);
    }

}
