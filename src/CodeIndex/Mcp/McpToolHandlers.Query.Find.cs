using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private const int DefaultFindMaxBytes = 65_536;

    private JsonNode ExecuteFindPage(JsonNode? id, DbReader reader, QueryCommandOptions options,
        FindSemanticFilters? filters, string? cursor, int? lineScanLimit, int maxBytes,
        bool contextTruncated, int? snippetLines, ArgumentAdjustmentCollector adjustments)
    {
        var cursorArgs = BuildMcpFindCursorArguments(options, filters, cursor);
        try
        {
            var resume = JsonEnvelopeWrapper.GetStandaloneFindResume(cursorArgs, reader);
            var effectiveLimit = options.Limit;
            var byteLimited = false;
            while (true)
            {
                reader.Cancellation.ThrowIfCancellationRequested();
                FindScanSummary scan;
                List<FileFindResult> results;
                int count;
                int fileCount;
                if (options.CountOnly)
                {
                    var counted = reader.CountFindInFiles(options.Query!, options.Lang,
                        options.All ? null : options.PathPatterns, options.ExcludePaths, options.ExcludeTests,
                        options.Exact, options.FocusLine, options.FocusColumn, options.Regex,
                        options.All ? QueryCommandRunner.FindAllCandidateFileLimit : null,
                        options.All ? lineScanLimit ?? QueryCommandRunner.FindAllLineScanLimit : null,
                        useIndexedLiteralCandidates: options.All,
                        resumePath: resume.Path, resumeLine: resume.Line, resumeFileOrdinal: resume.FileOrdinal,
                        resumeMatchOrdinal: resume.MatchOrdinal, resumeByteOffset: resume.ByteOffset,
                        cancellationToken: reader.Cancellation, semanticFilters: filters);
                    scan = counted.Scan;
                    count = counted.Count;
                    fileCount = counted.FileCount;
                    results = [];
                }
                else
                {
                    var found = reader.FindInFiles(options.Query!, effectiveLimit, options.Lang,
                        options.All ? null : options.PathPatterns, options.ExcludePaths, options.ExcludeTests,
                        options.ContextBefore, options.ContextAfter, options.Exact, options.MaxLineWidth,
                        options.FocusLine, options.FocusColumn, options.Regex,
                        options.All ? QueryCommandRunner.FindAllCandidateFileLimit : null,
                        options.All ? lineScanLimit ?? QueryCommandRunner.FindAllLineScanLimit : null,
                        useIndexedLiteralCandidates: options.All,
                        resumePath: resume.Path, resumeLine: resume.Line, resumeFileOrdinal: resume.FileOrdinal,
                        resumeMatchOrdinal: resume.MatchOrdinal, resumeByteOffset: resume.ByteOffset,
                        captureContinuation: true, cancellationToken: reader.Cancellation, semanticFilters: filters);
                    results = found.Results;
                    scan = found.Scan;
                    count = results.Count;
                    fileCount = results.Select(row => row.Path).Distinct(StringComparer.Ordinal).Count();
                }

                var next = QueryCommandRunner.BuildFindResumeCursor(cursorArgs, reader, scan);
                var payload = new JsonObject
                {
                    ["query"] = options.Query,
                    ["path"] = PathEcho(options.PathPatterns),
                    ["excludeTests"] = options.ExcludeTests,
                    ["before"] = options.ContextBefore,
                    ["after"] = options.ContextAfter,
                    ["contextTruncated"] = contextTruncated,
                    ["maxLineWidth"] = options.MaxLineWidth,
                    ["exact"] = options.Exact,
                    ["regex"] = options.Regex,
                    ["count"] = count,
                    ["fileCount"] = fileCount,
                    ["results"] = JsonSerializer.SerializeToNode(results, _jsonOptions),
                    ["max_bytes"] = maxBytes,
                    ["byte_limit_reached"] = byteLimited,
                };
                if (snippetLines.HasValue)
                    payload["snippetLines"] = snippetLines.Value;
                if (options.FocusLine.HasValue)
                    payload["focusLine"] = options.FocusLine.Value;
                if (options.FocusColumn.HasValue)
                    payload["focusColumn"] = options.FocusColumn.Value;
                QueryCommandRunner.AddFindTerminalScanFields(payload, scan, count, options.CountOnly,
                    options.CountOnly ? null : effectiveLimit, scan.ResultLimitReached, next.Cursor, next.ResultStableAt);
                // A resumed page covers only its remaining scan segment, including the final page.
                var authoritative = resume.Path is null && !scan.Truncated && !scan.ResultLimitReached && scan.UnknownOriginMatches == 0;
                payload[options.CountOnly ? "authoritative_count" : "authoritative_rows"] = authoritative;
                payload["total_count_authoritative"] = authoritative;
                payload["truncated"] = scan.Truncated || scan.ResultLimitReached;
                payload["more_available"] = scan.Truncated || scan.ResultLimitReached;
                if (byteLimited)
                {
                    payload["partial_result"] = true;
                    payload["truncation_reason"] = "max_bytes";
                }
                if (next.Cursor is not null)
                    payload["recovery_guidance"] = "Pass next_cursor as cursor with the same query, scope, filters and countOnly mode; limit, maxBytes and lineScanLimit may change. Restart after indexing.";
                if (count == 0)
                    AddFreshnessHint(payload, reader);
                adjustments.ApplyTo(payload);
                var response = CreateToolResult(id, options.CountOnly ? $"Counted {count} match(es)." : $"Found {count} match(es) across {fileCount} file(s).", payload);
                if (response["result"] is not null
                    && TryMeasureJsonUtf8BytesWithinLimit(payload, _jsonOptions, maxBytes, out _))
                    return response;

                if (options.CountOnly || results.Count <= 1)
                    return CreateToolErrorResponse(id, "The find page and its continuation cannot fit the response byte budget.",
                        category: McpErrorEnvelope.CategoryInvalidArgument, retrySafe: true,
                        suggestion: "Increase maxBytes/server response budget or reduce before, after, snippetLines, or maxLineWidth. Retry the same cursor; no matches were consumed.",
                        extraData: new JsonObject { ["error_code"] = CommandErrorCodes.ResponseBudgetTooSmall, ["max_bytes"] = maxBytes, ["restart_required"] = false });

                // Reuse the scanner from the original position with a smaller page. Its raw match
                // ordinal and UTF-8 position remain correct even for zero-width/same-line matches.
                effectiveLimit = Math.Max(1, results.Count / 2);
                byteLimited = true;
            }
        }
        catch (FindContinuationException ex)
        {
            return CreateMcpCursorError(id, "find", ex.Reason, ex.Message, stale: ex.Reason == "cursor_stale");
        }
        catch (RegexMatchTimeoutException ex) when (options.Regex)
        {
            return CreateToolErrorResponse(id, RegexTimeoutPolicy.FormatFindTimeout(ex),
                category: RegexTimeoutPolicy.RegexTimeoutCategory, suggestion: RegexTimeoutPolicy.McpFindTimeoutSuggestion,
                retrySafe: true, extraData: new JsonObject
                {
                    ["error_code"] = CommandErrorCodes.RegexMatchTimeout,
                    ["timeout_ms"] = ex.MatchTimeout.TotalMilliseconds,
                });
        }
        catch (ArgumentException) when (options.Regex)
        {
            return CreateToolErrorResponse(id, "invalid regular expression. Check regex syntax and retry.");
        }
    }

    private static string[] BuildMcpFindCursorArguments(QueryCommandOptions options, FindSemanticFilters? filters, string? cursor)
    {
        var args = new List<string> { "--query=" + options.Query };
        void Value(string name, object? value)
        {
            if (value is not null)
                args.Add(name + "=" + Convert.ToString(value, CultureInfo.InvariantCulture));
        }
        void Flag(string name, bool enabled) { if (enabled) args.Add(name); }
        Value("--lang", options.Lang);
        foreach (var path in options.PathPatterns) Value("--path", path);
        foreach (var path in options.ExcludePaths) Value("--exclude-path", path);
        Flag("--all", options.All);
        Flag("--regex", options.Regex);
        Flag("--exact", options.Exact);
        Flag("--count", options.CountOnly);
        Flag("--exclude-tests", options.ExcludeTests);
        Flag("--include-generated", options.IncludeGenerated);
        Value("--before", options.ContextBefore);
        Value("--after", options.ContextAfter);
        Value("--max-line-width", options.MaxLineWidth);
        Value("--focus-line", options.FocusLine);
        Value("--focus-column", options.FocusColumn);
        if (filters is not null)
        {
            foreach (var value in filters.Origins) Value("--origin", value);
            foreach (var value in filters.ExcludedOrigins) Value("--exclude-origin", value);
            foreach (var value in filters.ResultKinds) Value("--result-kind", value);
            Flag("--exclude-comments", filters.ExcludeComments);
            Flag("--exclude-strings", filters.ExcludeStrings);
            Flag("--exclude-fixtures", filters.ExcludeFixtures);
        }
        Value("--cursor", cursor);
        return [.. args];
    }
}
